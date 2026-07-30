using Wms.Data.Configuration;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>Stock On Hand figures for one warehouse group (e.g. TECHNO/JAFZA/YOTO combined, or BlackBOX alone).</summary>
public sealed record WhStockOnHand(
    long TotalQuantity,
    long TotalBoxesStock,
    long NumberOfBoxes,
    long TotalPalletsStock,
    long NumberOfPallets,
    long TotalActiveSkus);

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

    /// <summary>Stock On Hand for TECHNO/JAFZA/YOTO combined -- everything except BlackBOX.</summary>
    public Task<WhStockOnHand> GetStockOnHandExcludingBlackboxAsync(CancellationToken ct = default) =>
        GetStockOnHandAsync("Warehouse <> 'BLACKBOX'", ct);

    /// <summary>Stock On Hand for BlackBOX only.</summary>
    public Task<WhStockOnHand> GetStockOnHandForBlackboxAsync(CancellationToken ct = default) =>
        GetStockOnHandAsync("Warehouse = 'BLACKBOX'", ct);

    private async Task<WhStockOnHand> GetStockOnHandAsync(string whereClause, CancellationToken ct)
    {
        await using var c = OpenOnPremBackup();
        await using var cmd = c.CreateCommand();
        // qty/BoxNo/PalletNo aggregates come back as SQL int, not bigint -- cast explicitly
        // so GetInt64 (which requires an exact type match, no implicit widening) doesn't throw.
        cmd.CommandText = $@"
            SELECT
                TotalQuantity     = CAST(ISNULL(SUM(qty), 0) AS BIGINT),
                TotalBoxesStock   = CAST(ISNULL(SUM(CASE WHEN BoxNo <> '' THEN qty ELSE 0 END), 0) AS BIGINT),
                NumberOfBoxes     = CAST(COUNT(DISTINCT CASE WHEN BoxNo <> '' THEN BoxNo END) AS BIGINT),
                TotalPalletsStock = CAST(ISNULL(SUM(CASE WHEN PalletNo <> '' THEN qty ELSE 0 END), 0) AS BIGINT),
                NumberOfPallets   = CAST(COUNT(DISTINCT CASE WHEN PalletNo <> '' THEN PalletNo END) AS BIGINT),
                TotalActiveSkus   = CAST(COUNT(DISTINCT ItemCode) AS BIGINT)
              FROM RACKS.dbo.WHBoxItems
             WHERE {whereClause}";
        cmd.CommandTimeout = CommandTimeoutSeconds;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        await rdr.ReadAsync(ct);
        return new WhStockOnHand(
            rdr.GetInt64(0),
            rdr.GetInt64(1),
            rdr.GetInt64(2),
            rdr.GetInt64(3),
            rdr.GetInt64(4),
            rdr.GetInt64(5));
    }
}
