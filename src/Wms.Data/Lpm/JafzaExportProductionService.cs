using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// JAFZA Export Production Report. Ported directly from a user-supplied
/// query against BFLDATA.dbo.DailyCountCategoryTrf (Warehouse = 'JAFZA').
/// Qty is the sum of every hour bucket (hr1a..hr22a plus HR0A) for that
/// (TrnDate, Division, ShopName). No item-level detail is available from
/// this source, unlike Manual/Robo — Summary only.
/// </summary>
public class JafzaExportProductionService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 120;

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    private const string HourSum =
        "hr1a + hr2a + hr3a + hr4a + hr5a + hr6a + hr7a + hr8a + hr9a + hr10a + hr11a + hr12a + hr13a + " +
        "hr14a + hr15a + hr16a + hr17a + hr18a + hr19a + hr20a + hr21a + hr22a + HR0A";

    /// <summary>Division-wise summary — one row per (TrnDate, Division, ShopName).</summary>
    public async Task<List<JafzaExportProductionRow>> GetSummaryAsync(
        DateTime fromDate, DateTime toDate, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<JafzaExportProductionRow>(new CommandDefinition($@"
            SELECT TrnDate, Division, ShopName, Qty = SUM({HourSum})
              FROM BFLDATA.dbo.DailyCountCategoryTrf WITH (NOLOCK)
             WHERE TrnDate >= @from AND TrnDate <= @to AND Warehouse = 'JAFZA'
             GROUP BY TrnDate, Division, ShopName
             ORDER BY TrnDate, Division, ShopName",
            new { from = fromDate.Date, to = toDate.Date },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Total Qty only, for the summary-card total — avoids materializing every row.
    /// Optionally restricted to a set of Divisions (from the report's Divisions filter).</summary>
    public async Task<int> GetTotalQtyAsync(
        DateTime fromDate, DateTime toDate, IReadOnlyCollection<string>? divisions = null, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var divisionFilterSql = divisions is { Count: > 0 } ? "AND Division IN @divisions" : "";
        var total = await c.ExecuteScalarAsync<int?>(new CommandDefinition($@"
            SELECT SUM({HourSum})
              FROM BFLDATA.dbo.DailyCountCategoryTrf WITH (NOLOCK)
             WHERE TrnDate >= @from AND TrnDate <= @to AND Warehouse = 'JAFZA' {divisionFilterSql}",
            new { from = fromDate.Date, to = toDate.Date, divisions },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return total ?? 0;
    }
}
