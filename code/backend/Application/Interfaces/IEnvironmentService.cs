using Domain.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Application.Interfaces
{
    public interface IEnvironmentService
    {
        Task<CyberEnvironment> GetOrCreateEnvironmentAsync(string environmentName);
        Task<List<CyberEnvironment>> GetAllEnvironmentsAsync();
        Task<string> ApplyManagementAsync();
        /// <param name="type">"ctf" or "kali-lab"; only used when the environment is created. Null = keep / default "ctf".</param>
        /// <param name="configJson">JSON object overriding the type's defaults; stored on the environment. Null = keep the stored config.</param>
        Task<string> ApplyEnvironmentAsync(string environmentName, string? type = null, string? configJson = null);
        Task<Dictionary<string, object?>> GetEnvironmentOutputsAsync(string environmentName);
        Task<string> DestroyEnvironmentAsync(string environmentName);
        Task SyncManagementTerraformAsync();
    }
}

