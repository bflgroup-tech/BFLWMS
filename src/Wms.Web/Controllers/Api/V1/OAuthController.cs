using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Wms.Data.Api;

namespace Wms.Web.Controllers.Api.V1;

/// <summary>OAuth2 client-credentials token endpoint for machine-to-machine WMS API
/// clients (see Admin &gt; API &gt; Client Apps). Issues short-lived HMAC-signed JWTs
/// validated by the "Bearer" scheme registered in Program.cs.</summary>
[ApiController]
[Route("api/v1/oauth")]
[AllowAnonymous]
public class OAuthController(ApiClientAppService clientApps, IConfiguration config) : ControllerBase
{
    [HttpPost("token")]
    public async Task<IActionResult> Token(
        [FromForm] string grant_type, [FromForm] string client_id, [FromForm] string client_secret)
    {
        if (grant_type != "client_credentials")
            return BadRequest(new { error = "unsupported_grant_type" });

        if (string.IsNullOrEmpty(client_id) || string.IsNullOrEmpty(client_secret))
            return BadRequest(new { error = "invalid_request" });

        var app = await clientApps.ValidateAsync(client_id, client_secret);
        if (app is null)
            return Unauthorized(new { error = "invalid_client" });

        var jwtSection = config.GetSection("ApiJwt");
        var signingKey = jwtSection["SigningKey"];
        if (string.IsNullOrEmpty(signingKey))
            return StatusCode(500, new { error = "server_error", error_description = "API JWT signing key not configured" });

        var lifetimeMinutes = jwtSection.GetValue("AccessTokenMinutes", 60);
        var now = DateTime.UtcNow;
        var claims = new[]
        {
            new Claim("client_id", app.ClientId),
            new Claim("name", app.Name),
        };
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: jwtSection["Issuer"],
            audience: jwtSection["Audience"],
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(lifetimeMinutes),
            signingCredentials: creds);

        return Ok(new
        {
            access_token = new JwtSecurityTokenHandler().WriteToken(token),
            token_type = "Bearer",
            expires_in = lifetimeMinutes * 60,
        });
    }
}
