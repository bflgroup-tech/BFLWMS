namespace Wms.Data.Lpm;

/// <summary>
/// One order-sheet line for a container, read verbatim from usa.dbo.USAOrgFile.
/// Property names match the table's column names so Dapper maps them without
/// aliases — if a column is renamed at source, this is the one place to follow it.
/// </summary>
public record OrderSheetRow(
    string   ContNo,
    string?  PONO,
    string?  BOLNO,
    string?  Itemcode,
    string?  ItemName,
    string?  Division,
    int?     Qty,
    string?  Color,
    string?  Size);
