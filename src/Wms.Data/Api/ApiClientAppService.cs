using Microsoft.EntityFrameworkCore;
using Wms.Core;
using Wms.Core.Entities;
using Wms.Core.Security;

namespace Wms.Data.Api;

public class ApiClientAppService(IDbContextFactory<WmsDbContext> dbFactory, ICurrentUser currentUser)
{
    public async Task<List<ApiClientApp>> ListAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ApiClientApps.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
    }

    /// <summary>Creates a new client app and returns the plaintext secret — shown once
    /// to the caller; only its hash is persisted, so it cannot be retrieved again.</summary>
    public async Task<(string ClientId, string Secret)> CreateAsync(string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var clientId = "capp_" + ClientSecretHasher.GenerateToken(12);
        var secret = ClientSecretHasher.GenerateToken(32);

        db.ApiClientApps.Add(new ApiClientApp
        {
            ClientId = clientId,
            Name = name,
            SecretHash = ClientSecretHasher.Hash(secret),
            Enabled = true,
            CreateTS = DateTime.Now,
            CreatedBy = currentUser.Name,
        });
        await db.SaveChangesAsync();
        return (clientId, secret);
    }

    public async Task SetEnabledAsync(string clientId, bool enabled)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var app = await db.ApiClientApps.FirstOrDefaultAsync(x => x.ClientId == clientId);
        if (app is null) return;
        app.Enabled = enabled;
        await db.SaveChangesAsync();
    }

    /// <summary>Validates client_id/client_secret for the OAuth2 client-credentials
    /// token endpoint. Returns null on any failure (unknown client, disabled, wrong secret).</summary>
    public async Task<ApiClientApp?> ValidateAsync(string clientId, string secret)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var app = await db.ApiClientApps.AsNoTracking().FirstOrDefaultAsync(x => x.ClientId == clientId);
        if (app is null || !app.Enabled) return null;
        return ClientSecretHasher.Verify(secret, app.SecretHash) ? app : null;
    }
}
