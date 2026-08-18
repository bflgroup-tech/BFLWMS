namespace Wms.Core;

public static class Roles
{
    public const string Admin        = "Admin";
    public const string WHAssociate  = "WHAssociate";
    public const string WHSupervisor = "WHSupervisor";
    public const string WHManager    = "WHManager";
    public const string Reports      = "Reports";

    public const string AnyWarehouse = "Admin,WHAssociate,WHSupervisor,WHManager";
    public const string SupervisorOrAbove = "Admin,WHSupervisor,WHManager";
    public const string ReportsOrAdmin    = "Admin,Reports";
}

public static class AuthPolicies
{
    public const string RequireActiveUser = "RequireActiveUser";
}

public interface ICurrentUser
{
    string Name { get; }
    string? ClientIp { get; }
    string? ClientPcName { get; }
    string? Warehouse { get; }
    string? Country { get; }
    /// <summary>true when the user has role 'Admin' — bypasses per-user country access.</summary>
    bool HasAllCountriesAccess { get; }
    /// <summary>Explicit country access rows from dbo.WmsUserCountryAccess. Ignored when
    /// HasAllCountriesAccess is true. Case-insensitive comparisons expected at call sites.</summary>
    IReadOnlyCollection<string> AllowedCountries { get; }
    /// <summary>Return the subset of `all` the current user is allowed to see. Preserves order.</summary>
    IEnumerable<string> FilterCountries(IEnumerable<string> all);
    /// <summary>true if the user may see the sub-section identified by sectionKey
    /// (Wms.Core.SectionKeys.*) — explicit grant in dbo.Wms_UserSectionAccess, or Admin role.</summary>
    bool CanSeeSection(string sectionKey);
    /// <summary>
    /// Awaits the AuthenticationStateProvider, reads the principal, then loads the
    /// user's Country/Warehouse from the DB. Caches the result on the instance.
    /// Pages should call this once in OnInitializedAsync before reading properties.
    /// </summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);
}
