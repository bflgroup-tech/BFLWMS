namespace Wms.Data.Lpm;

/// <summary>
/// One (ContNo, OraPONo) of CDC-held stock, for the PO picker on the Container
/// Allocation page. Qty and the LPM range are shown so the operator can see what
/// they are about to release before picking it.
/// </summary>
public sealed record CdcPoOption(
    string    ContNo,
    string    OraPONo,
    int       Items,
    int       Qty,
    DateTime? MinLpmDt,
    DateTime? MaxLpmDt)
{
    /// <summary>"NOV-2026", or "SEP-2026 → NOV-2026" when the PO spans months.</summary>
    public string LpmRangeLabel =>
        (MinLpmDt, MaxLpmDt) switch
        {
            (null, null)             => "",
            var (a, b) when a == b   => CdcStoreAllocationService.LpmLabel(a!.Value),
            var (a, b)               => $"{CdcStoreAllocationService.LpmLabel(a ?? b!.Value)} → {CdcStoreAllocationService.LpmLabel(b ?? a!.Value)}",
        };
}

/// <summary>
/// Whether a (ContNo, OraPONo) has already been CDC-allocated. Keyed by the pair,
/// unlike <see cref="AllocationStatus"/>, which is per container.
/// </summary>
public sealed record CdcAllocationStatus(
    bool      HasFinal,
    int       Rows,
    int       TotalQty,
    int?      BatchNo,
    DateTime? ProcessedTS,
    string?   ProcessedBy,
    DateTime? ApprovedDt)
{
    public static CdcAllocationStatus None { get; } = new(false, 0, 0, null, null, null, null);

    public bool IsApproved => ApprovedDt is not null;
}

/// <summary>
/// One row of the CDC Allocation Status report: a (ContNo, OraPONo) carrying
/// PalletType='CD' stock, and whether it has been allocated yet.
///
/// Status is null for pending, matching the convention the bulk allocation queue
/// already uses — the UI renders `Status ?? "Pending"`.
/// </summary>
public sealed record CdcAllocationStatusRow(
    string    ContNo,
    string    OraPONo,
    DateTime? LpmDt,
    int       Items,
    int       CdQty,
    string?   Status,          // null = Pending, else "Allocated"
    int?      BatchNo,
    int?      AllocatedQty,
    int?      AllocatedRows,
    DateTime? ProcessedTS,
    string?   ProcessedBy,
    DateTime? ApprovedDt)
{
    public string LpmLabel => LpmDt is null ? "" : CdcStoreAllocationService.LpmLabel(LpmDt.Value);

    /// <summary>First of the LPM month, so the column sorts chronologically.
    /// Sorting the label as text puts JAN before SEP.</summary>
    public DateTime LpmSortKey =>
        LpmDt is null ? DateTime.MaxValue : new DateTime(LpmDt.Value.Year, LpmDt.Value.Month, 1);

    public bool IsPending => Status is null;

    /// <summary>Allocated less than the CD stock it started from — the run placed
    /// what it could and the rest had nowhere eligible to go.</summary>
    public bool IsShort => !IsPending && (AllocatedQty ?? 0) < CdQty;
}
