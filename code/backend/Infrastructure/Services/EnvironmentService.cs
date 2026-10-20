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
        IOptions<CtfClusterOptions> ctfClusterOptions) : IEnvironmentService
    {
        private readonly AppDbContext _dbContext = dbContext;

        private string GetManagementPath()
        {
            var basePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "terraform", "management"));
            return basePath;
        }

        private string GetEnvironmentsBasePath()
        {
            var basePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "terraform", "environments"));
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
                        cidr = e.Cidr
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

                        _dbContext.Environments.Add(new CyberEnvironment
                        {
                            Id = Guid.NewGuid(),
                            Name = name,
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

        public async Task<string> ApplyEnvironmentAsync(string environmentName)
        {
            var targetEnvPath = Path.Combine(GetEnvironmentsBasePath(), environmentName);
            if (!Directory.Exists(targetEnvPath))
            {
                throw new DirectoryNotFoundException($"Environment directory not found: {targetEnvPath}");
            }

            // Retrieve or allocate and store the environment with its VLAN ID and /16 CIDR
            var envRecord = await GetOrCreateEnvironmentAsync(environmentName);

            // Configure OPNsense (VLAN, interface assignment, firewall) through its API FIRST
            Console.WriteLine($"Configuring OPNsense network for {environmentName} (VLAN {envRecord.VlanId})...");
            await opnsense.ConfigureEnvironmentNetworkAsync(envRecord.VlanId, envRecord.Cidr, envRecord.Name);

            var env = environmentName.Trim().ToLowerInvariant();
            if (env != "ctf")
            {
                throw new NotSupportedException($"No Pulumi program is available for environment '{environmentName}'.");
            }

            var px = proxmoxOptions.Value;
            var clusterOpts = ctfClusterOptions.Value;
            var vlanId = envRecord.VlanId;

            var envVars = new Dictionary<string, string?>
            {
                { "PULUMI_BACKEND_URL", "file://~" },
                { "PULUMI_CONFIG_PASSPHRASE", "default_passphrase" },
                { "PROXMOX_VE_ENDPOINT", px.Endpoint },
                { "PROXMOX_VE_INSECURE", px.Insecure ? "true" : "false" }
            };
            if (!string.IsNullOrWhiteSpace(px.ApiToken)) envVars["PROXMOX_VE_API_TOKEN"] = px.ApiToken;
            if (!string.IsNullOrWhiteSpace(px.Username)) envVars["PROXMOX_VE_USERNAME"] = px.Username;
            if (!string.IsNullOrWhiteSpace(px.Password)) envVars["PROXMOX_VE_PASSWORD"] = px.Password;

            // Inline C# Pulumi program (replaces terraform/environments/<env>)
            var args = new InlineProgramArgs("cyberkit-environments", env, PulumiFn.Create(() => CtfEnvironmentProgram.Run(px, vlanId, envRecord.Cidr, env, clusterOptions: clusterOpts)))
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
    }
}
