using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Wms.Web.Controllers.Api.V1;

/// <summary>Sample endpoints for testing the API: one bearer-protected, one open.
/// For the protected one, obtain a token from POST /api/v1/oauth/token, then call
/// it with "Authorization: Bearer &lt;token&gt;".</summary>
[ApiController]
[Route("api/v1/ping")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class PingController : ControllerBase
{
    /// <summary>GET /api/v1/ping — requires a valid bearer token.</summary>
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        status = "ok",
        client = User.FindFirst("client_id")?.Value,
        serverTimeUtc = DateTime.UtcNow,
    });

    /// <summary>GET /api/v1/ping/anonymous — no auth required, for connectivity testing.</summary>
    [HttpGet("anonymous")]
    [AllowAnonymous]
    public IActionResult GetAnonymous() => Ok(new
    {
        status = "ok",
        serverTimeUtc = DateTime.UtcNow,
    });
}
