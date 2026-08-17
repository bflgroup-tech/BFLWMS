namespace Wms.Data.Lpm;

/// <summary>Existing LPMSIM.dbo.WMS_WH_MAXMIN_CAP row, for the view/delete grid.</summary>
public record WhMaxMinCapRow(
    DateTime CreateTs,
    string?  WeekId,
    string   Country,
    string   Warehouse,
    string   Division,
    double   MaxCapWeek,
    double   MinCapWeek,
    int?     Week,
    int?     Month,
    int?     Year,
    string?  CreatedUser);

/// <summary>One parsed row from the uploaded Excel (WAREHOUSE, DIVISION, MIN_CAP_WEEK, MAX_CAP_WEEK), before it's saved.</summary>
public record WhMaxMinCapUploadRow(
    string Warehouse,
    string Division,
    double MinCapWeek,
    double MaxCapWeek);

public record MaxCapSaveResult(bool Ok, string? Error, int RowsSaved);
