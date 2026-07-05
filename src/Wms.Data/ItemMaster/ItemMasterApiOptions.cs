namespace Wms.Data.ItemMaster;

/// <summary>Config for the external WMS Itemmaster API used by the LPM Manual
/// Building scan flow when an item isn't in the picked container's allocation
/// data. Populated from configuration section "ItemMasterApi".</summary>
public sealed class ItemMasterApiOptions
{
    public const string SectionName = "ItemMasterApi";

    /// <summary>Base URL, no trailing slash. Example:
    /// <c>http://bfltp.dynalias.com:8076/api</c>.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Login username.</summary>
    public string Username { get; set; } = "";

    /// <summary>Login password.</summary>
    public string Password { get; set; } = "";

    /// <summary>How long a cached token is considered valid before we
    /// re-login proactively. Independent of the 401 auto-retry path.</summary>
    public TimeSpan TokenTtl { get; set; } = TimeSpan.FromHours(20);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password);
}
