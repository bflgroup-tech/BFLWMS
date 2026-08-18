namespace Wms.Core;

/// <summary>
/// Catalogue of hideable sub-sections/sub-forms within a page — a finer grain than
/// MenuKeys (whole page/nav item). Checked via ICurrentUser.CanSeeSection, backed by
/// dbo.Wms_UserSectionAccess (see AuthStateCurrentUser) — same "explicit grant or Admin
/// bypass, empty = no access" model as country access, not menu access's claims model,
/// so a grant takes effect on the user's next page load without re-login.
/// </summary>
public static class SectionKeys
{
    public const string PRODSUMM_DTBW = "PRODSUMM_DTBW";

    /// <param name="PageLabel">Which page this section lives on — shown in the admin checklist.</param>
    /// <param name="Label">The section's own name.</param>
    public sealed record SectionEntry(string Key, string PageLabel, string Label);

    public static readonly IReadOnlyList<SectionEntry> All = new[]
    {
        new SectionEntry(PRODSUMM_DTBW, "Production Summary Report", "Daily Transfer Qty by Warehouse"),
    };
}
