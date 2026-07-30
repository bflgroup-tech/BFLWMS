using Wms.Data.Configuration;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Backs the Warehouse SOH Summary report. Reads RACKS.dbo.WHBoxItems via the shared
/// OnPremBackup (LOGBACKUP) connection -- same as WarehouseBoxesService -- not a
/// per-country connection string, since this table isn't split per country.
/// </summary>
public class WarehouseSohSummaryService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 60;

    private static string WithConnectTimeout(string cs)
    {
        var b = new SqlConnectionStringBuilder(cs) { ConnectTimeout = ConnectTimeoutSeconds };
        return b.ConnectionString;
    }

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetOnPremBackupConnectionString()));
        c.Open();
        return c;
    }

    /// <summary>Total Quantity (Units) across TECHNO/JAFZA/YOTO -- everything except BlackBOX.</summary>
    public async Task<long> GetTotalQuantityExcludingBlackboxAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ISNULL(SUM(qty), 0) FROM RACKS.dbo.WHBoxItems WHERE Warehouse <> 'BLACKBOX'";
        cmd.CommandTimeout = CommandTimeoutSeconds;
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }
}
