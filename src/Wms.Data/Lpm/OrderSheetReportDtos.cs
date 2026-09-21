namespace Wms.Data.Lpm;

/// <summary>
/// One order-sheet line for a container from usa.dbo.usaorgfile_LPM — the table
/// the allocation engine actually reads its PO lines from, one row per UPC.
///
/// Three columns are looked up rather than read off the line:
///   Division — datareporting.dbo.vupc_subclass (UPC-grained, so TOP 1 per item)
///   ItemName — usa.dbo.USAOrgFile, which carries the name; usaorgfile_LPM does not
///   EDI — BFLDATA.dbo.ContColorHeader (container-grained, TOP 1 per container, not
///   per item) -- same value repeats on every line of the same container.
/// </summary>
public record OrderSheetRow(
    string    ContNo,
    string?   PONo,
    string?   BOL,
    string?   ItemCode,
    string?   ItemName,
    string?   Division,
    double?   Qty,
    string?   LPM,
    DateTime? LPMDt,
    string?   Style,
    string?   UPC,
    DateTime? CreatedAt,
    string?   EDI);
