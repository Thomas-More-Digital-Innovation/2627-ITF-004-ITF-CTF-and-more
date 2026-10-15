using Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EnvironmentsController(IEnvironmentService environmentService) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var envs = await environmentService.GetAllEnvironmentsAsync();
            return Ok(envs);
        }

        [HttpGet("{name}")]
        public async Task<IActionResult> GetByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest("Environment name is required.");
            }

            var env = await environmentService.GetOrCreateEnvironmentAsync(name);
            return Ok(env);
        }

        [HttpPost("create")]
        public async Task<IActionResult> Create([FromBody] string environmentName)
        {
            if (string.IsNullOrWhiteSpace(environmentName))
            {
                return BadRequest("Environment name is required.");
            }

            try
            {                
                var result = await environmentService.ApplyEnvironmentAsync(environmentName);
                return Ok(new { Output = result });
            }
            catch (System.Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        [HttpPost("sync-network")]
        public async Task<IActionResult> SyncNetwork()
        {
            try
            {
                await environmentService.SyncManagementTerraformAsync();
                return Ok(new { Message = "Management network configuration synced successfully." });
            }
            catch (System.Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }
    }
}
