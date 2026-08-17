using System.Net;
using Wms.Core;
using Wms.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Wms.Web.Auth;

public class AuthStateCurrentUser(
    AuthenticationStateProvider auth,
    IHttpContextAccessor http,
    IDbContextFactory<WmsDbContext> dbFactory,
    IMemoryCache cache) : ICurrentUser
{
    private bool _loaded;
    private string? _name;
    private string? _warehouse;
    private string? _country;
    private bool _hasAllCountriesAccess;
    private IReadOnlyCollection<string> _allowedCountries = Array.Empty<string>();

    public static string ProfileCacheKey(string username) => $"profile:{username}";
    public static string CountryAccessCacheKey(string username) => $"countryAccess:{username}";

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;

        // 1) Resolve username — try every known place (Identity.Name, common claim types,
        //    HttpContext fallback). Necessary because cloned/transformed Windows principals
        //    sometimes lose Identity.Name even though the underlying claim still exists.
        try
        {
            var state = await auth.GetAuthenticationStateAsync();
            _name = NameFromPrincipal(state?.User);
        }
        catch { }

        if (string.IsNullOrEmpty(_name))
            _name = NameFromPrincipal(http.HttpContext?.User);

        if (string.IsNullOrEmpty(_name))
        {
            _loaded = true;
            return;
        }

        // 2) Load Country / Warehouse with cache + 5s timeout.
        var key = ProfileCacheKey(_name);
        if (cache.TryGetValue<(string?, string?)>(key, out var cached) &&
            (cached.Item1 is not null || cached.Item2 is not null))
        {
            _warehouse = cached.Item1; _country = cached.Item2;
        }
        else
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                await using var db = await dbFactory.CreateDbContextAsync(cts.Token);
                var row = await db.Users.AsNoTracking()
                    .Where(u => u.Username == _name)
                    .Select(u => new { u.Warehouse, u.Country })
                    .FirstOrDefaultAsync(cts.Token);
                _warehouse = row?.Warehouse;
                _country   = row?.Country;
            }
            catch { /* leave nulls */ }

            var ttl = (_warehouse is null && _country is null)
                ? TimeSpan.FromSeconds(5)
                : TimeSpan.FromMinutes(2);
            cache.Set(key, (_warehouse, _country), ttl);
        }

        // Country access + admin bypass — cached separately so the shorter TTL on
        // profile-not-found doesn't repeatedly re-fetch the access rows.
        var caKey = CountryAccessCacheKey(_name);
        if (cache.TryGetValue<(bool IsAdmin, IReadOnlyCollection<string> Countries)>(caKey, out var caCached))
        {
            _hasAllCountriesAccess = caCached.IsAdmin;
            _allowedCountries      = caCached.Countries;
        }
        else
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                await using var db = await dbFactory.CreateDbContextAsync(cts.Token);
                var isAdmin = await db.UserRoles.AsNoTracking()
                    .AnyAsync(r => r.Username == _name && r.RoleCode == Roles.Admin, cts.Token);
                var countries = await db.UserCountryAccess.AsNoTracking()
                    .Where(a => a.Username == _name)
                    .Select(a => a.Country)
                    .ToListAsync(cts.Token);
                _hasAllCountriesAccess = isAdmin;
                _allowedCountries      = new HashSet<string>(countries, StringComparer.OrdinalIgnoreCase);
            }
            catch { /* leave defaults — empty access */ }

            cache.Set(caKey, (_hasAllCountriesAccess, _allowedCountries), TimeSpan.FromMinutes(2));
        }

        _loaded = true;
    }

    public string Name
    {
        get
        {
            if (_loaded) return _name ?? "anonymous";

            // Outside a Razor circuit — a BackgroundService scope, for instance —
            // the server AuthenticationStateProvider THROWS rather than returning
            // an anonymous state ("Do not call GetAuthenticationStateAsync outside
            // of the DI scope for a Razor component"). A property getter must not
            // propagate that: it took down the whole Monday VG batch. Fall through
            // to HttpContext (also absent on a timer) and finally "anonymous".
            // EnsureLoadedAsync already guards its own call the same way.
            try
            {
                var task = auth.GetAuthenticationStateAsync();
                if (task.IsCompletedSuccessfully)
                    return NameFromPrincipal(task.Result?.User)
                        ?? NameFromPrincipal(http.HttpContext?.User)
                        ?? "anonymous";
            }
            catch { }

            return NameFromPrincipal(http.HttpContext?.User) ?? "anonymous";
        }
    }

    private static string? NameFromPrincipal(System.Security.Claims.ClaimsPrincipal? p)
    {
        if (p is null) return null;
        var n = p.Identity?.Name;
        if (!string.IsNullOrEmpty(n)) return n;
        n = p.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;
        if (!string.IsNullOrEmpty(n)) return n;
        n = p.FindFirst(System.Security.Claims.ClaimTypes.Upn)?.Value;
        if (!string.IsNullOrEmpty(n)) return n;
        n = p.FindFirst(System.Security.Claims.ClaimTypes.WindowsAccountName)?.Value;
        if (!string.IsNullOrEmpty(n)) return n;
        // Walk all identities and try
        foreach (var id in p.Identities)
        {
            if (!string.IsNullOrEmpty(id.Name)) return id.Name;
            n = id.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;
            if (!string.IsNullOrEmpty(n)) return n;
        }
        return null;
    }

    public string? ClientIp => http.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? ClientPcName
    {
        get
        {
            var ip = http.HttpContext?.Connection.RemoteIpAddress;
            if (ip is null) return null;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var key = $"pcname:{ip}";
            if (cache.TryGetValue<string>(key, out var name)) return name;
            try
            {
                var entry = Dns.GetHostEntry(ip);
                name = entry.HostName.Split('.')[0];
            }
            catch { name = ip.ToString(); }
            cache.Set(key, name!, TimeSpan.FromMinutes(10));
            return name;
        }
    }

    public string? Warehouse => _warehouse;
    public string? Country   => _country;
    public bool HasAllCountriesAccess => _hasAllCountriesAccess;
    public IReadOnlyCollection<string> AllowedCountries => _allowedCountries;
    public IEnumerable<string> FilterCountries(IEnumerable<string> all)
    {
        if (_hasAllCountriesAccess) return all;
        var allowed = _allowedCountries;
        return all.Where(c => c is not null && allowed.Contains(c!));
    }
}
