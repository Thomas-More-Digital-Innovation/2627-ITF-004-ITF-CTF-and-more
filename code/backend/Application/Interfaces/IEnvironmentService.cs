using System.Threading.Tasks;

namespace Application.Interfaces
{
    public interface IEnvironmentService
    {
        Task<string> ApplyEnvironmentAsync(string environmentName);
    }
}
