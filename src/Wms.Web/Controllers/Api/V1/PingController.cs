using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Wms.Web.Controllers.Api.V1;

/// <summary>Sample bearer-protected endpoint demonstrating the OAuth2
/// client-credentials mechanism — obtain a token from POST /api/v1/oauth/token,
/// then call this with "Authorization: Bearer &lt;token&gt;".</summary>
[ApiController]
[Route("api/v1/ping")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class PingController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        status = "ok",
        client = User.FindFirst("client_id")?.Value,
        serverTimeUtc = DateTime.UtcNow,
    });
}
