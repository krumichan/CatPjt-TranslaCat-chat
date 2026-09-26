using Microsoft.AspNetCore.Mvc;

namespace TranslaCat.Chat.Api.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            status = "UP",
            service = "TranslaCat.Chat"
        });
    }
}
