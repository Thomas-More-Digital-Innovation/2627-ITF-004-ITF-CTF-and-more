using Application.Interfaces;
using Pulumi.Automation;

namespace Infrastructure.Services
{
    public class EnvironmentService : IEnvironmentService
    {
        public async Task<string> ApplyEnvironmentAsync(string environmentName)
        {
            var basePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "terraform", "environments"));
            var targetEnvPath = Path.Combine(basePath, environmentName);

            if (!Directory.Exists(targetEnvPath))
            {
                throw new DirectoryNotFoundException($"Environment directory not found: {targetEnvPath}");
            }

            var stackName = "dev";
            var workspaceArgs = new LocalWorkspaceOptions
            {
                WorkDir = targetEnvPath,
                EnvironmentVariables = new System.Collections.Generic.Dictionary<string, string?>
                {
                    { "PULUMI_BACKEND_URL", "file://~" },
                    { "PULUMI_CONFIG_PASSPHRASE", "default_passphrase" }, // Required for local state secrets manager
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
                Console.WriteLine($"Pulumi cancel notice: {ex.Message}");
            }

            await stack.RefreshAsync(new RefreshOptions { OnStandardOutput = Console.WriteLine });

            var result = await stack.UpAsync(new UpOptions { OnStandardOutput = Console.WriteLine });

            return result.StandardOutput;
        }
    }
}
