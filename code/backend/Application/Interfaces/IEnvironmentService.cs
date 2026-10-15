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
        Task<string> ApplyEnvironmentAsync(string environmentName);
        Task SyncManagementTerraformAsync();
    }
}

