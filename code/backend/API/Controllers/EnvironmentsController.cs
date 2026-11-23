using Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
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

        /// <summary>Stack outputs, e.g. the Guacamole URL and student logins of a kali-lab (secrets included).</summary>
        [HttpGet("{name}/outputs")]
        public async Task<IActionResult> GetOutputs(string name)
        {
            try
            {
                return Ok(await environmentService.GetEnvironmentOutputsAsync(name));
            }
            catch (System.Collections.Generic.KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
        }

        /// <summary>
        /// Creates or updates an environment. The body is either just the name (a JSON string, deploys
        /// type "ctf") or an object: { "name": "lab1", "type": "kali-lab", "config": { "vmCount": 20 } }.
        /// </summary>
        [HttpPost("create")]
        public async Task<IActionResult> Create([FromBody] JsonElement body)
        {
            string? name, type = null, config = null;
            if (body.ValueKind == JsonValueKind.String)
            {
                name = body.GetString();
            }
            else if (body.ValueKind == JsonValueKind.Object)
            {
                name = body.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                type = body.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (body.TryGetProperty("config", out var c) && c.ValueKind != JsonValueKind.Null)
                {
                    if (c.ValueKind != JsonValueKind.Object) return BadRequest("config must be a JSON object.");
                    config = c.GetRawText();
                }
            }
            else
            {
                return BadRequest("Body must be an environment name or { name, type, config }.");
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest("Environment name is required.");
            }

            try
            {
                var result = await environmentService.ApplyEnvironmentAsync(name, type, config);
                return Ok(new { Output = result });
            }
            catch (System.Exception ex) when (ex is System.ArgumentException or System.NotSupportedException or JsonException)
            {
                return BadRequest(ex.Message);
            }
            catch (System.Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        [HttpDelete("{name}")]
        public async Task<IActionResult> Delete(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest("Environment name is required.");
            }

            try
            {
                var result = await environmentService.DestroyEnvironmentAsync(name);
                return Ok(new { Output = result });
            }
            catch (System.Collections.Generic.KeyNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (System.Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }
    }
}
