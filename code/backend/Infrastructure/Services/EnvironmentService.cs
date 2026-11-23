using Application.Interfaces;
using Domain.Models;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Infrastructure.Pulumi;
using Microsoft.Extensions.Options;
using global::Pulumi.Automation;
using System.Text.Json;

namespace Infrastructure.Services
{
    public class EnvironmentService(
        AppDbContext dbContext,
        IOpnsenseService opnsense,
        IOptions<ProxmoxOptions> proxmoxOptions,
        IOptions<CtfClusterOptions> ctfClusterOptions,
        IOptions<KaliLabOptions> kaliLabOptions) : IEnvironmentService
    {
        private readonly AppDbContext _dbContext = dbContext;

        private string GetManagementPath()
        {
            var basePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "terraform", "management"));
            return basePath;
        }

        public async Task<CyberEnvironment> GetOrCreateEnvironmentAsync(string environmentName)
        {
            var normalizedName = environmentName.Trim().ToLowerInvariant();
            var existing = await _dbContext.Environments.FirstOrDefaultAsync(e => e.Name.ToLower() == normalizedName);
            if (existing != null)
            {
                return existing;
            }

            // Sync from existing auto.tfvars.json if in-memory database was reset
            await SeedFromManagementTfvarsAsync();
            existing = await _dbContext.Environments.FirstOrDefaultAsync(e => e.Name.ToLower() == normalizedName);
            if (existing != null)
            {
                return existing;
            }

            var all = await _dbContext.Environments.ToListAsync();
            var usedVlanIds = all.Select(e => e.VlanId).ToHashSet();
            var usedOctets = all
                .Select(e =>
                {
                    var parts = e.Cidr.Split('.');
                    return parts.Length > 1 && int.TryParse(parts[1], out var octet) ? octet : -1;
                })
                .Where(o => o > 0)
                .ToHashSet();

            // Allocate next available VLAN ID starting at 100
            var nextVlanId = 100;
            while (usedVlanIds.Contains(nextVlanId))
            {
                nextVlanId++;
            }

            // Allocate /16 subnet in 10.0.0.0 (10.X.0.0/16)
            var nextOctet = (nextVlanId <= 254 && !usedOctets.Contains(nextVlanId)) ? nextVlanId : 1;
            while (usedOctets.Contains(nextOctet) || nextOctet == 0 || nextOctet > 254)
            {
                nextOctet++;
                if (nextOctet > 254)
                {
                    throw new InvalidOperationException("No available /16 subnets left in 10.0.0.0/8.");
                }
            }

            var newEnv = new CyberEnvironment
            {
                Id = Guid.NewGuid(),
                Name = normalizedName,
                VlanId = nextVlanId,
                Cidr = $"10.{nextOctet}.0.0/16",
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.Environments.Add(newEnv);
            await _dbContext.SaveChangesAsync();

            // Persist to central management terraform tfvars
            await SyncManagementTerraformAsync();

            return newEnv;
        }

        public async Task<List<CyberEnvironment>> GetAllEnvironmentsAsync()
        {
            await SeedFromManagementTfvarsAsync();
            return await _dbContext.Environments.OrderBy(e => e.VlanId).ToListAsync();
        }

        public async Task SyncManagementTerraformAsync()
        {
            var managementPath = GetManagementPath();
            if (!Directory.Exists(managementPath))
            {
                return;
            }

            var all = await _dbContext.Environments.ToListAsync();
            var tfvarsData = new
            {
                environments = all.ToDictionary(
                    e => e.Name,
                    e => new
                    {
                        vlan_id = e.VlanId,
                        cidr = e.Cidr,
                        type = e.Type,
                        config = e.Config
                    }
                )
            };

            var jsonPath = Path.Combine(managementPath, "environments.auto.tfvars.json");
            var json = JsonSerializer.Serialize(tfvarsData, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(jsonPath, json);
        }

        private async Task SeedFromManagementTfvarsAsync()
        {
            var count = await _dbContext.Environments.CountAsync();
            if (count > 0)
            {
                return;
            }

            var managementPath = GetManagementPath();
            var jsonPath = Path.Combine(managementPath, "environments.auto.tfvars.json");
            if (!File.Exists(jsonPath))
            {
                return;
            }

            try
            {
                var content = await File.ReadAllTextAsync(jsonPath);
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("environments", out var envsElement) && envsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in envsElement.EnumerateObject())
                    {
                        var name = prop.Name;
                        var vlanId = prop.Value.GetProperty("vlan_id").GetInt32();
                        var cidr = prop.Value.GetProperty("cidr").GetString() ?? $"10.{vlanId}.0.0/16";
                        var type = prop.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : CtfType;
                        var config = prop.Value.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

                        _dbContext.Environments.Add(new CyberEnvironment
                        {
                            Id = Guid.NewGuid(),
                            Name = name,
                            Type = type,
                            Config = config,
                            VlanId = vlanId,
                            Cidr = cidr,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                    await _dbContext.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Notice: could not seed environments from tfvars: {ex.Message}");
            }
        }

        public async Task<string> ApplyManagementAsync()
        {
            var managementPath = GetManagementPath();
            if (!Directory.Exists(managementPath))
            {
                throw new DirectoryNotFoundException($"Management directory not found: {managementPath}");
            }

            var stackName = "dev";
            var workspaceArgs = new LocalWorkspaceOptions
            {
                WorkDir = managementPath,
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    { "PULUMI_BACKEND_URL", "file://~" },
                    { "PULUMI_CONFIG_PASSPHRASE", "default_passphrase" },
                    { "PROXMOX_VE_INSECURE", "true" }
                }
            };

            var workspace = await LocalWorkspace.CreateAsync(workspaceArgs);
            await workspace.InstallAsync();
            var stack = await WorkspaceStack.CreateOrSelectAsync(stackName, workspace);

            try
            {
                await stack.CancelAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Pulumi cancel notice for management: {ex.Message}");
            }

            var result = await stack.UpAsync(new UpOptions { OnStandardOutput = Console.WriteLine });
            return result.StandardOutput;
        }

        public const string CtfType = "ctf";
        public static readonly string[] SupportedTypes = [CtfType, KaliLabProgram.EnvironmentType];

        public async Task<string> ApplyEnvironmentAsync(string environmentName, string? type = null, string? configJson = null)
        {
            var env = environmentName.Trim().ToLowerInvariant();
            var requestedType = type?.Trim().ToLowerInvariant();
            if (requestedType is not null && !SupportedTypes.Contains(requestedType))
            {
                throw new NotSupportedException($"Unknown environment type '{type}'. Supported: {string.Join(", ", SupportedTypes)}.");
            }

            // Retrieve or allocate and store the environment with its VLAN ID and /16 CIDR
            var isNew = !await _dbContext.Environments.AnyAsync(e => e.Name.ToLower() == env);
            // The name ends up in Proxmox VM names, which must be valid DNS labels.
            if (isNew && !System.Text.RegularExpressions.Regex.IsMatch(env, "^[a-z][a-z0-9-]{0,30}$"))
            {
                throw new ArgumentException($"Invalid environment name '{environmentName}': use lowercase letters, digits and '-', starting with a letter (max 31 characters).");
            }
            var envRecord = await GetOrCreateEnvironmentAsync(environmentName);
            if (isNew || requestedType is not null && envRecord.Type != requestedType)
            {
                if (!isNew)
                {
                    throw new ArgumentException($"Environment '{env}' already exists as type '{envRecord.Type}'. Destroy it first to change its type.");
                }
                // Old callers only send a name: "ctf" keeps working as before.
                envRecord.Type = requestedType ?? CtfType;
            }

            // Validate before storing so a bad key fails the request instead of the deployment.
            if (configJson is not null)
            {
                envRecord.Config = envRecord.Type == CtfType
                    ? RuntimeConfig.Normalize(ctfClusterOptions.Value, configJson)
                    : RuntimeConfig.Normalize(kaliLabOptions.Value, configJson);
            }
            await _dbContext.SaveChangesAsync();
            await SyncManagementTerraformAsync();

            // Configure OPNsense (VLAN, interface assignment, firewall) through its API FIRST
            Console.WriteLine($"Configuring OPNsense network for {environmentName} (VLAN {envRecord.VlanId})...");
            await opnsense.ConfigureEnvironmentNetworkAsync(envRecord.VlanId, envRecord.Cidr, envRecord.Name);

            var stack = await SelectEnvironmentStackAsync(env, envRecord, prepareImages: true);

            try
            {
                await stack.CancelAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Pulumi cancel notice: {ex.Message}");
            }

            var result = await stack.UpAsync(new UpOptions { OnStandardOutput = Console.WriteLine });

            return result.StandardOutput;
        }

        public async Task<Dictionary<string, object?>> GetEnvironmentOutputsAsync(string environmentName)
        {
            var env = environmentName.Trim().ToLowerInvariant();
            var envRecord = await _dbContext.Environments.FirstOrDefaultAsync(e => e.Name.ToLower() == env)
                ?? throw new KeyNotFoundException($"Environment '{environmentName}' does not exist.");
            var stack = await SelectEnvironmentStackAsync(env, envRecord, prepareImages: false);
            var outputs = await stack.GetOutputsAsync();
            return outputs.ToDictionary(o => o.Key, o => o.Value.Value);
        }

        private const string EnvironmentsProjectName = "cyberkit-environments";

        private async Task<WorkspaceStack> SelectEnvironmentStackAsync(string env, CyberEnvironment envRecord, bool prepareImages)
        {
            var px = proxmoxOptions.Value;
            var vlanId = envRecord.VlanId;

            var envVars = new Dictionary<string, string?>
            {
                { "PULUMI_BACKEND_URL", "file://~" },
                { "PULUMI_CONFIG_PASSPHRASE", "default_passphrase" },
                { "PROXMOX_VE_ENDPOINT", px.Endpoint },
                { "PROXMOX_VE_INSECURE", px.Insecure ? "true" : "false" },
                { "PROXMOX_VE_SSH_USERNAME", px.SshUsername },
                { "PROXMOX_VE_SSH_AGENT", px.SshAgent ? "true" : "false" },
            };
            if (!string.IsNullOrWhiteSpace(px.ApiToken)) envVars["PROXMOX_VE_API_TOKEN"] = px.ApiToken;
            if (!string.IsNullOrWhiteSpace(px.Username)) envVars["PROXMOX_VE_USERNAME"] = px.Username;
            if (!string.IsNullOrWhiteSpace(px.Password)) envVars["PROXMOX_VE_PASSWORD"] = px.Password;
            var sshPassword = string.IsNullOrWhiteSpace(px.SshPassword) ? px.Password : px.SshPassword;
            if (!string.IsNullOrWhiteSpace(sshPassword)) envVars["PROXMOX_VE_SSH_PASSWORD"] = sshPassword;

            // Inline C# Pulumi program, chosen by environment type, with the runtime config applied.
            PulumiFn program;
            switch (envRecord.Type)
            {
                case CtfType:
                    var clusterOpts = RuntimeConfig.Merge(ctfClusterOptions.Value, envRecord.Config);
                    program = PulumiFn.Create(() => CtfEnvironmentProgram.Run(px, vlanId, envRecord.Cidr, env, clusterOptions: clusterOpts));
                    break;
                case KaliLabProgram.EnvironmentType:
                    var labOpts = RuntimeConfig.Merge(kaliLabOptions.Value, envRecord.Config);
                    // Only `up` creates the image uploads; other operations never run the program's creates.
                    var imagePath = prepareImages
                        ? await KaliImageCache.EnsureAsync(labOpts.KaliImageUrl)
                        : Path.Combine(KaliImageCache.CacheDir, "unused.qcow2");
                    program = PulumiFn.Create(() => KaliLabProgram.Run(px, vlanId, envRecord.Cidr, env, labOpts, imagePath));
                    break;
                default:
                    throw new NotSupportedException($"No Pulumi program is available for environment type '{envRecord.Type}'.");
            }

            var args = new InlineProgramArgs(EnvironmentsProjectName, env, program)
            {
                EnvironmentVariables = envVars
            };

            var stack = await LocalWorkspace.CreateOrSelectStackAsync(args);

            // Explicitly set the provider config on the stack
            await stack.SetConfigAsync("proxmoxve:endpoint", new ConfigValue(px.Endpoint));
            await stack.SetConfigAsync("proxmoxve:insecure", new ConfigValue(px.Insecure ? "true" : "false"));
            if (!string.IsNullOrWhiteSpace(px.ApiToken))
            {
                await stack.SetConfigAsync("proxmoxve:apiToken", new ConfigValue(px.ApiToken, isSecret: true));
            }
            if (!string.IsNullOrWhiteSpace(px.Username))
            {
                await stack.SetConfigAsync("proxmoxve:username", new ConfigValue(px.Username));
            }
            if (!string.IsNullOrWhiteSpace(px.Password))
            {
                await stack.SetConfigAsync("proxmoxve:password", new ConfigValue(px.Password, isSecret: true));
            }

            return stack;
        }

        public async Task<string> DestroyEnvironmentAsync(string environmentName)
        {
            var env = environmentName.Trim().ToLowerInvariant();
            var envRecord = await _dbContext.Environments.FirstOrDefaultAsync(e => e.Name.ToLower() == env);
            if (envRecord is null)
            {
                await SeedFromManagementTfvarsAsync();
                envRecord = await _dbContext.Environments.FirstOrDefaultAsync(e => e.Name.ToLower() == env)
                    ?? throw new KeyNotFoundException($"Environment '{environmentName}' does not exist.");
            }

            var output = string.Empty;
            if (SupportedTypes.Contains(envRecord.Type))
            {
                var stack = await SelectEnvironmentStackAsync(env, envRecord, prepareImages: false);
                try
                {
                    await stack.CancelAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Pulumi cancel notice: {ex.Message}");
                }

                // Everything inside the cluster (Cilium, ArgoCD, namespaces, secrets, ...) dies with the VMs.
                // Deleting it through the Kubernetes API first is slow, can hang on ArgoCD/namespace
                // finalizers and fails outright when the cluster is already broken, so it is only
                // dropped from the Pulumi state. The state is topologically ordered, so walking it in
                // reverse removes dependents before the resources (and provider) they depend on.
                var deployment = await stack.ExportStackAsync();
                if (envRecord.Type == CtfType &&
                    deployment.Json.TryGetProperty("deployment", out var dep) &&
                    dep.TryGetProperty("resources", out var resources))
                {
                    var k8sUrns = resources.EnumerateArray()
                        .Select(r => (Urn: r.GetProperty("urn").GetString(), Type: r.GetProperty("type").GetString()))
                        .Where(r => r.Urn is not null && r.Type is not null &&
                                    (r.Type.StartsWith("kubernetes:") || r.Type == "pulumi:providers:kubernetes"))
                        .Select(r => r.Urn!)
                        .Reverse()
                        .ToList();

                    foreach (var urn in k8sUrns)
                    {
                        await stack.State.DeleteAsync(urn, force: true);
                    }
                    Console.WriteLine($"Dropped {k8sUrns.Count} in-cluster resources from state for {env}.");
                }

                // Talos resources are no-ops on delete; this removes the VMs (and the ISO download).
                var result = await stack.DestroyAsync(new DestroyOptions { OnStandardOutput = Console.WriteLine });
                output = result.StandardOutput;
                await stack.Workspace.RemoveStackAsync(env);
            }

            Console.WriteLine($"Removing OPNsense network for {envRecord.Name} (VLAN {envRecord.VlanId})...");
            await opnsense.RemoveEnvironmentNetworkAsync(envRecord.VlanId, envRecord.Name);

            _dbContext.Environments.Remove(envRecord);
            await _dbContext.SaveChangesAsync();
            await SyncManagementTerraformAsync();

            return output;
        }
    }
}
