using Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EnvironmentsController(IEnvironmentService environmentService) : ControllerBase
    {
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
    }
}
