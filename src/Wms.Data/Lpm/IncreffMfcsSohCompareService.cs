using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// Compares two ECOM SOH sources into dbo.LPM_ECOM_SOH_COMPARISON, one row per
/// (Country, Itemcode) present in EITHER source (FULL OUTER JOIN, missing side
/// written as 0):
///   IncreffSOH -> dbo.LPM_ECOM_INCREFF_SOH  (BigQuery INCREFF feed, populated
///                 by IncreffSohFromGcpService — run that first for a fresh
///                 compare)
///   MFCS_SOH   -> RACKS.dbo.lpm_locstock    (MFCS online-store stock;
///                 StoreID = 'ONLINE' for UAE, 'ONLINEKSA' for KSA)
///
/// Variance (= ABS(IncreffSOH - MFCS_SOH)) is a PERSISTED computed column on the
/// table itself, not written here — it derives automatically on insert.
///
/// Division/Department/Class/Subclass/Family are denormalized in at write time
/// from DATAREPORTING.dbo.vUPC_SUBCLASS (LEFT JOIN on Itemcode, deduped to one
/// row per Itemcode via ROW_NUMBER — that view has a handful of duplicate
/// Itemcode rows) — so the ECOM Stock Variance Report reads them straight off
/// this table instead of joining the 20M-row view itself at read time.
///
/// Both sources live on the same on-prem SQL instance as LPMSIM — LPM_ECOM_INCREFF_SOH
/// is local to that DB, RACKS.dbo.lpm_locstock is reached via 3-part naming (same
/// pattern as the LPMSIM.dbo.* cross-references elsewhere in this codebase) — so
/// the whole compare-and-refresh is one set-based SQL statement, no C#-side join.
///
/// TRUNCATE + INSERT of the WHOLE table every run (not per-country) — this is a
/// full comparison snapshot, not an incremental feed.
///
/// No timer yet — triggered from the Nightly Batches admin page's Refresh Now,
/// same as IncreffSohFromGCP.
/// </summary>
public class IncreffMfcsSohCompareService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 600;
    public const string JobName = "IncreffMfcsSohCompare";

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

    private const string InsertSql = @"
        ;WITH Increff AS (
            SELECT Country, Itemcode, SUM(SOH) AS SOH
              FROM dbo.LPM_ECOM_INCREFF_SOH
             WHERE SOH <> 0
             GROUP BY Country, Itemcode
        ),
        Mfcs AS (
            SELECT 'UAE' AS Country, Itemcode, SUM(SOH) AS SOH
              FROM RACKS.dbo.lpm_locstock
             WHERE StoreID = 'ONLINE' AND SOH <> 0
             GROUP BY Itemcode
            UNION ALL
            SELECT 'KSA' AS Country, Itemcode, SUM(SOH) AS SOH
              FROM RACKS.dbo.lpm_locstock
             WHERE StoreID = 'ONLINEKSA' AND SOH <> 0
             GROUP BY Itemcode
        ),
        Subclass AS (
            SELECT Itemcode, Division, Department, class AS Class, subclass AS Subclass, Family,
                   ROW_NUMBER() OVER (PARTITION BY Itemcode ORDER BY (SELECT NULL)) AS rn
              FROM DATAREPORTING.dbo.vUPC_SUBCLASS
        )
        INSERT INTO dbo.LPM_ECOM_SOH_COMPARISON
            (Country, Itemcode, IncreffSOH, MFCS_SOH, CreateTS, Division, Department, Class, Subclass, Family)
        SELECT
            COALESCE(i.Country, m.Country)   AS Country,
            COALESCE(i.Itemcode, m.Itemcode) AS Itemcode,
            ISNULL(i.SOH, 0)                 AS IncreffSOH,
            ISNULL(m.SOH, 0)                 AS MFCS_SOH,
            DATEADD(hour, 4, SYSUTCDATETIME()) AS CreateTS,
            s.Division, s.Department, s.Class, s.Subclass, s.Family
          FROM Increff i
          FULL OUTER JOIN Mfcs m ON m.Country = i.Country AND m.Itemcode = i.Itemcode
          LEFT JOIN Subclass s ON s.Itemcode = COALESCE(i.Itemcode, m.Itemcode) AND s.rn = 1;";

    /// <summary>On-demand "Refresh Now" — rebuilds dbo.LPM_ECOM_SOH_COMPARISON from
    /// scratch. Run IncreffSohFromGcpService.RefreshAsync first for a fresh compare.</summary>
    public async Task<int> RefreshAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            await c.ExecuteAsync(new CommandDefinition(
                "TRUNCATE TABLE dbo.LPM_ECOM_SOH_COMPARISON;",
                transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var rows = await c.ExecuteAsync(new CommandDefinition(
                InsertSql, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return rows;
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }
}
