using Pulumi.Automation;

class Program
{
    static async Task Main()
    {
        var workspaceArgs = new LocalWorkspaceOptions { WorkDir = "." };
        var workspace = await LocalWorkspace.CreateAsync(workspaceArgs);
        await workspace.InstallAsync();
    }
}
