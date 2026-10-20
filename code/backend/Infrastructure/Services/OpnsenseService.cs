using Application.Interfaces;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Infrastructure.Services
{
    /// <summary>
    /// Configures OPNsense through its REST API:
    /// 1. VLAN device  (interfaces/vlan_settings)
    /// 2. Interface assignment (interfaces/assignment)
    /// 3. Firewall rule: env CIDR -> !RFC1918 (firewall/filter)
    /// All steps are idempotent (matched on description / device).
    /// </summary>
    public class OpnsenseService(HttpClient http, IOptions<OpnsenseOptions> options) : IOpnsenseService
    {
        private const string Rfc1918AliasName = "RFC1918_Networks";
        private const string Prefix = "cyberkit_";

        private string ParentInterface => options.Value.ParentInterface;

        public async Task ConfigureEnvironmentNetworkAsync(int vlanId, string cidr, string environmentName, CancellationToken ct = default)
        {
            var descr = $"{Prefix}{environmentName}";

            var device = await EnsureVlanAsync(vlanId, descr, ct);
            var identifier = await EnsureAssignmentAsync(device, descr, cidr, ct);
            await EnsureRfc1918AliasAsync(ct);
            await EnsureInternetRuleAsync(identifier, cidr, descr, vlanId, ct);
        }

        private async Task<string> EnsureVlanAsync(int vlanId, string descr, CancellationToken ct)
        {
            var rows = await SearchAsync("interfaces/vlan_settings/searchItem", ct);

            // 1. Check if a VLAN with the same tag exists on the CURRENT parent interface
            var existing = rows.FirstOrDefault(r => (string?)r["tag"] == vlanId.ToString() && (string?)r["if"] == ParentInterface);
            if (existing is not null)
            {
                // Ensure description is up to date
                var existingUuid = (string?)existing["uuid"];
                if ((string?)existing["descr"] != descr && !string.IsNullOrEmpty(existingUuid))
                {
                    await PostAsync($"interfaces/vlan_settings/setItem/{existingUuid}", new
                    {
                        vlan = new { @if = ParentInterface, tag = vlanId.ToString(), pcp = "0", descr }
                    }, ct);
                    await PostAsync("interfaces/vlan_settings/reconfigure", new { }, ct);
                }
                return VlanDevice(existing, vlanId);
            }

            // 2. Check if a VLAN for this tag or description already exists on a DIFFERENT parent interface
            var oldParentVlan = rows.FirstOrDefault(r => (string?)r["tag"] == vlanId.ToString() || (string?)r["descr"] == descr);
            if (oldParentVlan is not null)
            {
                var oldUuid = (string?)oldParentVlan["uuid"];
                var oldDevice = VlanDevice(oldParentVlan, vlanId);
                var oldParentIf = (string?)oldParentVlan["if"];

                Console.WriteLine($"VLAN {vlanId} was previously configured on parent interface '{oldParentIf}'. Updating to '{ParentInterface}'...");

                // Before modifying or recreating the VLAN, check if old assignment exists and remove lock so it can be updated
                await CleanupStaleAssignmentAsync(oldDevice, ct);

                if (!string.IsNullOrEmpty(oldUuid))
                {
                    // Update parent interface on the existing VLAN item
                    await PostAsync($"interfaces/vlan_settings/setItem/{oldUuid}", new
                    {
                        vlan = new { @if = ParentInterface, tag = vlanId.ToString(), pcp = "0", descr }
                    }, ct);
                    await PostAsync("interfaces/vlan_settings/reconfigure", new { }, ct);

                    rows = await SearchAsync("interfaces/vlan_settings/searchItem", ct);
                    var updated = rows.FirstOrDefault(r => (string?)r["uuid"] == oldUuid);
                    if (updated is not null)
                    {
                        return VlanDevice(updated, vlanId);
                    }
                }
            }

            // 3. Create fresh VLAN
            await PostAsync("interfaces/vlan_settings/addItem", new
            {
                vlan = new { @if = ParentInterface, tag = vlanId.ToString(), pcp = "0", descr }
            }, ct);
            await PostAsync("interfaces/vlan_settings/reconfigure", new { }, ct);

            rows = await SearchAsync("interfaces/vlan_settings/searchItem", ct);
            var created = rows.FirstOrDefault(r => (string?)r["tag"] == vlanId.ToString() && (string?)r["if"] == ParentInterface)
                ?? throw new InvalidOperationException($"VLAN {vlanId} was not found after creation.");
            return VlanDevice(created, vlanId);
        }

        private async Task CleanupStaleAssignmentAsync(string oldDevice, CancellationToken ct)
        {
            var assignments = await SearchAsync("interfaces/assignment/searchItem", ct);
            var stale = assignments.FirstOrDefault(r => (string?)r["if"] == oldDevice);
            if (stale is not null)
            {
                var id = (string?)stale["identifier"] ?? (string?)stale["uuid"];
                if (!string.IsNullOrEmpty(id))
                {
                    try
                    {
                        // Unset lock first
                        await PostAsync($"interfaces/assignment/setItem/{id}", new
                        {
                            @interface = new { @lock = "0" }
                        }, ct);
                        await PostAsync($"interfaces/assignment/delItem/{id}", new { }, ct);
                        await PostAsync("interfaces/assignment/reconfigure", new { }, ct);
                        Console.WriteLine($"Cleaned up stale interface assignment '{id}' for old device '{oldDevice}'.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Notice: could not auto-remove stale assignment '{id}': {ex.Message}");
                    }
                }
            }
        }

        // searchItem returns "vlanif" as e.g. "vlan0.100" or "vlan0.100 [LAN_NAME]" once assigned.
        private string VlanDevice(JsonNode row, int vlanId) =>
            ((string?)row["vlanif"])?.Split(' ')[0] ?? $"{ParentInterface}.{vlanId}";

        private async Task<string> EnsureAssignmentAsync(string device, string descr, string cidr, CancellationToken ct)
        {
            var gatewayIp = GetGatewayIpWithPrefix(cidr);
            var rows = await SearchAsync("interfaces/assignment/searchItem", ct);

            // 1. Check if an assignment already exists for this physical/vlan device
            var existingByDevice = rows.FirstOrDefault(r => (string?)r["if"] == device);

            // 2. Check if an assignment with the same description or IP already exists (possibly bound to an older device)
            var existingByDescrOrIp = rows.FirstOrDefault(r =>
                (string?)r["descr"] == descr ||
                (!string.IsNullOrEmpty((string?)r["ipaddr"]) && (string?)r["ipaddr"] == gatewayIp));

            var targetAssignment = existingByDevice ?? existingByDescrOrIp;

            if (targetAssignment is null)
            {
                await PostAsync("interfaces/assignment/addItem", new
                {
                    @interface = new
                    {
                        @if = device,
                        descr,
                        enable = "1",
                        type4 = "staticv4",
                        ipaddr = gatewayIp
                    }
                }, ct);
                await PostAsync("interfaces/assignment/reconfigure", new { }, ct);
                rows = await SearchAsync("interfaces/assignment/searchItem", ct);
                targetAssignment = rows.FirstOrDefault(r => (string?)r["if"] == device)
                    ?? throw new InvalidOperationException($"Interface for {device} was not found after assignment.");
            }
            else
            {
                var identifier = (string?)targetAssignment["identifier"] ?? (string?)targetAssignment["uuid"]
                    ?? throw new InvalidOperationException($"Could not determine interface identifier for {device}.");

                var currentDevice = (string?)targetAssignment["if"];
                var currentDescr = (string?)targetAssignment["descr"];
                var currentIp = (string?)targetAssignment["ipaddr"];

                // If device, description, or IP differs, update the assignment in place
                if (currentDevice != device || currentDescr != descr || currentIp != gatewayIp)
                {
                    Console.WriteLine($"Updating interface assignment '{identifier}' (Device: '{currentDevice}' -> '{device}', IP: '{currentIp}' -> '{gatewayIp}')...");
                    await PostAsync($"interfaces/assignment/setItem/{identifier}", new
                    {
                        @interface = new
                        {
                            @if = device,
                            descr,
                            enable = "1",
                            type4 = "staticv4",
                            ipaddr = gatewayIp
                        }
                    }, ct);
                    await PostAsync("interfaces/assignment/reconfigure", new { }, ct);
                }
            }

            // The identifier (opt1, opt2, ...) is what firewall rules reference.
            return (string?)targetAssignment["identifier"] ?? (string?)targetAssignment["uuid"]
                ?? throw new InvalidOperationException($"Could not determine interface identifier for {device}.");
        }

        private static string GetGatewayIpWithPrefix(string cidr)
        {
            // e.g. "10.100.0.0/16" -> "10.100.0.1/16"
            var parts = cidr.Split('/');
            var ipParts = parts[0].Split('.');
            if (parts.Length == 2 && ipParts.Length == 4 && int.TryParse(ipParts[3], out var lastOctet))
            {
                var gw = $"{ipParts[0]}.{ipParts[1]}.{ipParts[2]}.{lastOctet + 1}/{parts[1]}";
                return gw;
            }
            return cidr;
        }

        private async Task EnsureRfc1918AliasAsync(CancellationToken ct)
        {
            var rows = await SearchAsync("firewall/alias/searchItem", ct);
            if (rows.Any(r => (string?)r["name"] == Rfc1918AliasName)) return;

            await PostAsync("firewall/alias/addItem", new
            {
                alias = new
                {
                    enabled = "1",
                    name = Rfc1918AliasName,
                    type = "network",
                    content = "10.0.0.0/8\n172.16.0.0/12\n192.168.0.0/16",
                    description = "Private IPv4 ranges (managed by CyberKit)"
                }
            }, ct);
            await PostAsync("firewall/alias/reconfigure", new { }, ct);
        }

        private async Task EnsureInternetRuleAsync(string identifier, string cidr, string descr, int vlanId, CancellationToken ct)
        {
            var ruleDescr = $"{descr} internet access";
            var rows = await SearchAsync("firewall/filter/searchRule", ct);
            if (rows.Any(r => (string?)r["description"] == ruleDescr)) return;

            await PostAsync("firewall/filter/addRule", new
            {
                rule = new
                {
                    enabled = "1",
                    sequence = (100 + vlanId).ToString(),
                    action = "pass",
                    quick = "1",
                    interface_ = identifier,
                    direction = "in",
                    ipprotocol = "inet",
                    protocol = "any",
                    source_net = cidr,
                    destination_net = Rfc1918AliasName,
                    destination_not = "1",
                    description = ruleDescr
                }
            }, ct, renameInterfaceKey: true);
            await PostAsync("firewall/filter/apply", new { }, ct);
        }

        private async Task<List<JsonNode>> SearchAsync(string path, CancellationToken ct)
        {
            using var resp = await http.PostAsJsonAsync($"api/{path}", new { current = 1, rowCount = -1 }, ct);
            resp.EnsureSuccessStatusCode();
            var node = await resp.Content.ReadFromJsonAsync<JsonNode>(ct);
            return node?["rows"]?.AsArray().Where(n => n is not null).Select(n => n!).ToList() ?? [];
        }

        private async Task PostAsync(string path, object body, CancellationToken ct, bool renameInterfaceKey = false)
        {
            var json = JsonSerializer.SerializeToNode(body)!;
            if (renameInterfaceKey && json["rule"] is JsonObject rule && rule.Remove("interface_", out var val))
            {
                rule["interface"] = val; // "interface" is reserved in C#; renamed here
            }

            using var resp = await http.PostAsJsonAsync($"api/{path}", json, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            resp.EnsureSuccessStatusCode();

            // OPNsense returns 200 with {"result":"failed", "validations":{...}} on validation errors.
            if (JsonNode.Parse(text)?["result"]?.GetValue<string>() == "failed")
            {
                throw new InvalidOperationException($"OPNsense rejected {path}: {text}");
            }
        }

    }
}