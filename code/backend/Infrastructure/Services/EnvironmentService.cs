using Application.Interfaces;
using Domain.Models;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Pulumi.Automation;
using System.Text.Json;

namespace Infrastructure.Services
{
    public class EnvironmentService(AppDbContext dbContext) : IEnvironmentService
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

            // Ensure management stack (OPNsense VLAN interface & firewall rules) is applied FIRST
            Console.WriteLine($"Applying central management/OPNsense network for {environmentName} (VLAN {envRecord.VlanId})...");
            try
            {
                await ApplyManagementAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Management network apply notice: {ex.Message}");
            }

            // Pass the dynamically assigned VLAN ID via terraform.tfvars.json in the environment directory
            var envTfvarsJsonPath = Path.Combine(targetEnvPath, "terraform.tfvars.json");
            var envTfvars = new { vlan_id = envRecord.VlanId };
            await File.WriteAllTextAsync(envTfvarsJsonPath, JsonSerializer.Serialize(envTfvars, new JsonSerializerOptions { WriteIndented = true }));


            var stackName = "dev";
            var workspaceArgs = new LocalWorkspaceOptions
            {
                WorkDir = targetEnvPath,
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    { "PULUMI_BACKEND_URL", "file://~" },
                    { "PULUMI_CONFIG_PASSPHRASE", "default_passphrase" },
                    { "PROXMOX_VE_INSECURE", "true" },
                    { "TF_VAR_vlan_id", envRecord.VlanId.ToString() }
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
                Console.WriteLine($"Pulumi cancel notice: {ex.Message}");
            }

            await stack.RefreshAsync(new RefreshOptions { OnStandardOutput = Console.WriteLine });

            var result = await stack.UpAsync(new UpOptions { OnStandardOutput = Console.WriteLine });

            return result.StandardOutput;
        }
    }
}
