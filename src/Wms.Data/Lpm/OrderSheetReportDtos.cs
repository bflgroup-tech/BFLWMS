namespace Wms.Data.Lpm;

/// <summary>
/// One order-sheet line for a container from usa.dbo.usaorgfile_LPM — the table
/// the allocation engine actually reads its PO lines from, one row per UPC.
///
/// Two columns are looked up per item rather than read off the line:
///   Division — datareporting.dbo.vupc_subclass (UPC-grained, so TOP 1 per item)
///   ItemName — usa.dbo.USAOrgFile, which carries the name; usaorgfile_LPM does not
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
    DateTime? CreatedAt);
