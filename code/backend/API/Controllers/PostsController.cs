using Microsoft.AspNetCore.Mvc;

namespace CA.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PostsController() : ControllerBase
    {
        [HttpGet]
        public async Task<ActionResult<Guid>> Create()
        {
            
            return Ok();
        }

    }
}
