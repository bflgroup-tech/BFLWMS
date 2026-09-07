using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// Compares two ECOM SOH sources into dbo.LPM_ECOM_SOH_COMPARISON, one row per
/// (Country, Itemcode) present in ANY of the four sources below (missing side(s)
/// written as 0):
///   IncreffSOH -> dbo.LPM_ECOM_INCREFF_SOH  (BigQuery INCREFF feed, populated
///                 by IncreffSohFromGcpService — run that first for a fresh
///                 compare)
///   MFCS_SOH   -> RACKS.dbo.lpm_locstock    (MFCS online-store stock;
///                 StoreID = 'ONLINE' for UAE, 'ONLINEKSA' for KSA)
///
/// GateKeeperRejectedSummer/Winter -> RACKS.dbo.WHBoxItems (PalletType 'GS'/'GW'
/// respectively), summed by Itemcode. UAE-only — that table carries no country
/// split, so KSA rows always get 0 for both. These participate in the same
/// "present in ANY source" row set as Increff/Mfcs (a UNION-built key spine,
/// not a two-way FULL OUTER JOIN, which doesn't extend cleanly to more than two
/// sources) — an Itemcode with a rejected pallet but no Increff/MFCS entry
/// still gets its own row (IncreffSOH/MFCS_SOH = 0), so the report's total GS/GW
/// always matches a plain SUM(Qty) over WHBoxItems, not just the subset that
/// happens to overlap with the other two sources.
///
/// InTransitUAE/KSA -> RACKS.dbo.MFCS_LOCSTOCK_INT (MFCS_TOLOCID 10007/20002
/// respectively), summed by Itemcode. The source carries no country column of
/// its own, but the join is still scoped to its own country (InTransitUAE only
/// on Country='UAE' rows, InTransitKSA only on Country='KSA' rows) — same
/// pattern as GS/GW. An earlier version joined both by Itemcode alone with no
/// country condition, which meant a country-unfiltered total counted every
/// item's in-transit quantity TWICE (once from its UAE row, once from its KSA
/// row) — caught in production (report totals were exactly double the raw
/// SUM(INTRANSIT_QTY)) and fixed here. Not part of the Variance formula —
/// informational columns only.
///
/// Variance (= MFCS_SOH - (IncreffSOH + GateKeeperRejectedSummer +
/// GateKeeperRejectedWinter), signed — negative when the right side is bigger)
/// is a PERSISTED computed column on the table itself, not written here — it
/// derives automatically on insert.
///
/// Division/Department/Class/Subclass/Family are denormalized in at write time
/// from DATAREPORTING.dbo.vUPC_SUBCLASS (LEFT JOIN on Itemcode, deduped to one
/// row per Itemcode via ROW_NUMBER — that view has a handful of duplicate
/// Itemcode rows) — so the ECOM Stock Variance Report reads them straight off
/// this table instead of joining the 20M-row view itself at read time.
///
/// Brand -> USA.dbo.UPCBarCodes.Vendor, same LEFT JOIN + ROW_NUMBER dedup
/// pattern (that table has ~7,400 Itemcodes with more than one distinct Vendor
/// out of ~18M rows — an arbitrary-but-deterministic pick, same tradeoff
/// already accepted for vUPC_SUBCLASS's own duplicates).
///
/// All sources live on the same on-prem SQL instance as LPMSIM — LPM_ECOM_INCREFF_SOH
/// is local to that DB, RACKS.dbo.lpm_locstock/WHBoxItems are reached via 3-part naming
/// (same pattern as the LPMSIM.dbo.* cross-references elsewhere in this codebase) — so
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
        GsRejected AS (
            SELECT 'UAE' AS Country, ItemCode AS Itemcode, SUM(Qty) AS Qty
              FROM RACKS.dbo.WHBoxItems
             WHERE PalletType = 'GS'
             GROUP BY ItemCode
        ),
        GwRejected AS (
            SELECT 'UAE' AS Country, ItemCode AS Itemcode, SUM(Qty) AS Qty
              FROM RACKS.dbo.WHBoxItems
             WHERE PalletType = 'GW'
             GROUP BY ItemCode
        ),
        InTransitUae AS (
            SELECT ITEMCODE AS Itemcode, SUM(INTRANSIT_QTY) AS Qty
              FROM RACKS.dbo.MFCS_LOCSTOCK_INT
             WHERE MFCS_TOLOCID = 10007
             GROUP BY ITEMCODE
        ),
        InTransitKsa AS (
            SELECT ITEMCODE AS Itemcode, SUM(INTRANSIT_QTY) AS Qty
              FROM RACKS.dbo.MFCS_LOCSTOCK_INT
             WHERE MFCS_TOLOCID = 20002
             GROUP BY ITEMCODE
        ),
        Spine AS (
            SELECT Country, Itemcode FROM Increff
            UNION
            SELECT Country, Itemcode FROM Mfcs
            UNION
            SELECT Country, Itemcode FROM GsRejected
            UNION
            SELECT Country, Itemcode FROM GwRejected
            UNION
            SELECT 'UAE', Itemcode FROM InTransitUae
            UNION
            SELECT 'KSA', Itemcode FROM InTransitKsa
        ),
        Subclass AS (
            SELECT Itemcode, Division, Department, class AS Class, subclass AS Subclass, Family,
                   ROW_NUMBER() OVER (PARTITION BY Itemcode ORDER BY (SELECT NULL)) AS rn
              FROM DATAREPORTING.dbo.vUPC_SUBCLASS
        ),
        Vendor AS (
            SELECT Itemcode, Vendor,
                   ROW_NUMBER() OVER (PARTITION BY Itemcode ORDER BY (SELECT NULL)) AS rn
              FROM USA.dbo.UPCBarCodes
        )
        INSERT INTO dbo.LPM_ECOM_SOH_COMPARISON
            (Country, Itemcode, IncreffSOH, MFCS_SOH, GateKeeperRejectedSummer, GateKeeperRejectedWinter,
             InTransitUAE, InTransitKSA, CreateTS, Division, Department, Class, Subclass, Family, Brand)
        SELECT
            sp.Country,
            sp.Itemcode,
            ISNULL(i.SOH, 0)                 AS IncreffSOH,
            ISNULL(m.SOH, 0)                 AS MFCS_SOH,
            ISNULL(gs.Qty, 0)                AS GateKeeperRejectedSummer,
            ISNULL(gw.Qty, 0)                AS GateKeeperRejectedWinter,
            ISNULL(iu.Qty, 0)                AS InTransitUAE,
            ISNULL(ik.Qty, 0)                AS InTransitKSA,
            DATEADD(hour, 4, SYSUTCDATETIME()) AS CreateTS,
            s.Division, s.Department, s.Class, s.Subclass, s.Family,
            v.Vendor AS Brand
          FROM Spine sp
          LEFT JOIN Increff i    ON i.Country = sp.Country AND i.Itemcode = sp.Itemcode
          LEFT JOIN Mfcs m       ON m.Country = sp.Country AND m.Itemcode = sp.Itemcode
          LEFT JOIN GsRejected gs ON gs.Country = sp.Country AND gs.Itemcode = sp.Itemcode
          LEFT JOIN GwRejected gw ON gw.Country = sp.Country AND gw.Itemcode = sp.Itemcode
          LEFT JOIN InTransitUae iu ON iu.Itemcode = sp.Itemcode AND sp.Country = 'UAE'
          LEFT JOIN InTransitKsa ik ON ik.Itemcode = sp.Itemcode AND sp.Country = 'KSA'
          LEFT JOIN Subclass s   ON s.Itemcode = sp.Itemcode AND s.rn = 1
          LEFT JOIN Vendor v     ON v.Itemcode = sp.Itemcode AND v.rn = 1;";

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
