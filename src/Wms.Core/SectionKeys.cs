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
    public const string PRODSUMM_DIVCAP = "PRODSUMM_DIVCAP";
    public const string ECOMPROD_USERS_TABLE = "ECOMPROD_USERS_TABLE";
    public const string ECOMPROD_ADD_USER    = "ECOMPROD_ADD_USER";

    // The two halves of bulk allocation. Separate keys on purpose: pushing a batch
    // is compiling a worklist, running it writes allocations for dozens of
    // containers. Someone may reasonably be trusted with the first and not the second.
    public const string BULKALLOC_PUSH_QUEUE = "BULKALLOC_PUSH_QUEUE";
    public const string BULKALLOC_RUN        = "BULKALLOC_RUN";

    /// <param name="PageLabel">Which page this section lives on — shown in the admin checklist.</param>
    /// <param name="Label">The section's own name.</param>
    public sealed record SectionEntry(string Key, string PageLabel, string Label);

    public static readonly IReadOnlyList<SectionEntry> All = new[]
    {
        new SectionEntry(PRODSUMM_DTBW, "Production Summary Report", "Daily Transfer Qty by Warehouse"),
        new SectionEntry(PRODSUMM_DIVCAP, "Production Summary Report", "Week-wise Division Comparison"),
        new SectionEntry(ECOMPROD_USERS_TABLE, "Ecom Production Report", "Online WH Users (table)"),
        new SectionEntry(ECOMPROD_ADD_USER,    "Ecom Production Report", "Online WH Users Add/Remove (form)"),
        new SectionEntry(BULKALLOC_PUSH_QUEUE, "Pending for Counting",   "Push contnos to Bulk Queue"),
        new SectionEntry(BULKALLOC_RUN,        "Container Allocation",   "Bulk PO Allocation (run / re-queue)"),
    };
}
