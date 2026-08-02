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
/// Storage Capacity figures for one warehouse group. For the box+pallet group
/// (TECHNO/JAFZA/YOTO), FreeBoxRackLocations/FilledBoxLocations are BINRACK-type
/// locations and FreePalletRackLocations/FilledPalletLocations are everything else
/// (TMPWH/WAREHOUSE/TECHNORACK). BlackBOX has no rack-type split, so its single
/// Free/Filled Storage Locations figures are carried in the Box slots and the
/// Pallet slots stay 0 (the UI shows the box-only field set for that group anyway).
/// </summary>
public sealed record WhStorageCapacity(
    long TotalRackLocations,
    long FreeBoxRackLocations,
    long FilledBoxLocations,
    long FreePalletRackLocations,
    long FilledPalletLocations)
{
    public double OverallUtilizationPct => TotalRackLocations > 0
        ? Math.Round(100.0 * (FilledBoxLocations + FilledPalletLocations) / TotalRackLocations, 1)
        : 0;
}

/// <summary>Aging-bucket units for Critical Alerts &amp; Aging Inventory -- a single global
/// total across RACKS.dbo.WHBoxItems (that table is UAE-only), not split per warehouse group.</summary>
public sealed record WhAgingUnits(
    long Days1To30,
    long Days30To60,
    long Days60To90,
    long DaysAbove90,
    long ElapsedLpm);

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

    // Materializes #racklocation: one row per (racktype, warehouse) with its slot capacity
    // (raw count from the source table) and totalcapacity (capacity scaled per rack type --
    // TECHNORACK holds 59 pallets/rack, certain WAREHOUSE racks hold 3 or 100), plus how many
    // of those slots are currently used. Must run in the SAME command as whatever SELECTs
    // from it -- same reason as BadBoxesPrefix in ReportsService.
    private const string RackLocationSetupSql = @"
        SET NOCOUNT ON;
        IF OBJECT_ID('tempdb..#racklocation') IS NOT NULL DROP TABLE #racklocation;
        CREATE TABLE #racklocation(racktype varchar(15), warehouse varchar(15), capacity int, totalcapacity int, used int);

        INSERT INTO #racklocation SELECT 'BINRACK', warehouse, COUNT(*), 0, 0 FROM racks.dbo.BinRackMaster GROUP BY warehouse;
        INSERT INTO #racklocation SELECT 'TMPWH', 'JAFZA', COUNT(*), 0, 0 FROM racks.dbo.tmpwhracks;
        INSERT INTO #racklocation SELECT 'WAREHOUSE', warehouse, COUNT(*), 0, 0 FROM racks.dbo.WarehouseRacks GROUP BY warehouse;
        INSERT INTO #racklocation SELECT 'TECHNORACK', 'TECHNO', COUNT(*), 0, 0 FROM racks.dbo.TechnoRacks;
        INSERT INTO #racklocation VALUES('BINRACK', 'BLACKBOX', 121847, 121847, 0);

        UPDATE #racklocation SET totalcapacity = capacity WHERE racktype IN ('BINRACK','TMPWH');
        UPDATE #racklocation SET totalcapacity = capacity * 59 WHERE racktype = 'TECHNORACK';
        UPDATE #racklocation SET totalcapacity = capacity * 3 WHERE racktype = 'WAREHOUSE' AND warehouse IN ('YOTO-BU','TECHNO-E','YOTO-SF');
        UPDATE #racklocation SET totalcapacity = capacity * 100 WHERE racktype = 'WAREHOUSE' AND warehouse IN ('YOTO');
        UPDATE #racklocation SET used = (SELECT COUNT(*) FROM racks.dbo.tmpwhracks WHERE PalletNo1 <> '' OR PalletNo2 <> '') WHERE racktype = 'TMPWH';
        UPDATE #racklocation SET used = (SELECT COUNT(*) FROM racks.dbo.BinRack WHERE Warehouse = a.warehouse) FROM #racklocation a WHERE racktype = 'BINRACK';
        ";

    /// <summary>Storage Capacity for TECHNO/JAFZA/YOTO combined -- everything except BlackBOX.
    /// Box = BINRACK-type locations, Pallet = everything else (TMPWH/WAREHOUSE/TECHNORACK).</summary>
    public async Task<WhStorageCapacity> GetStorageCapacityExcludingBlackboxAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = RackLocationSetupSql + @"
            SELECT
                TotalRackLocations      = CAST(ISNULL(SUM(totalcapacity), 0) AS BIGINT),
                FreeBoxRackLocations    = CAST(ISNULL(SUM(CASE WHEN racktype = 'BINRACK' THEN totalcapacity - used ELSE 0 END), 0) AS BIGINT),
                FilledBoxLocations      = CAST(ISNULL(SUM(CASE WHEN racktype = 'BINRACK' THEN used ELSE 0 END), 0) AS BIGINT),
                FreePalletRackLocations = CAST(ISNULL(SUM(CASE WHEN racktype <> 'BINRACK' THEN totalcapacity - used ELSE 0 END), 0) AS BIGINT),
                FilledPalletLocations   = CAST(ISNULL(SUM(CASE WHEN racktype <> 'BINRACK' THEN used ELSE 0 END), 0) AS BIGINT)
              FROM #racklocation
             WHERE warehouse <> 'BLACKBOX';
            DROP TABLE #racklocation;";
        cmd.CommandTimeout = CommandTimeoutSeconds;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        await rdr.ReadAsync(ct);
        return new WhStorageCapacity(
            rdr.GetInt64(0), rdr.GetInt64(1), rdr.GetInt64(2), rdr.GetInt64(3), rdr.GetInt64(4));
    }

    /// <summary>Storage Capacity for BlackBOX only -- no rack-type split, so Free/Filled
    /// Storage Locations are carried in the Box slots (matching the UI's box-only field set).</summary>
    public async Task<WhStorageCapacity> GetStorageCapacityForBlackboxAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = RackLocationSetupSql + @"
            SELECT
                TotalRackLocations   = CAST(ISNULL(SUM(totalcapacity), 0) AS BIGINT),
                FreeBoxRackLocations = CAST(ISNULL(SUM(totalcapacity - used), 0) AS BIGINT),
                FilledBoxLocations   = CAST(ISNULL(SUM(used), 0) AS BIGINT)
              FROM #racklocation
             WHERE warehouse = 'BLACKBOX';
            DROP TABLE #racklocation;";
        cmd.CommandTimeout = CommandTimeoutSeconds;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        await rdr.ReadAsync(ct);
        return new WhStorageCapacity(
            rdr.GetInt64(0), rdr.GetInt64(1), rdr.GetInt64(2), 0, 0);
    }

    /// <summary>Critical Alerts &amp; Aging Inventory units, bucketed by LPM month. LPMDt is stored
    /// as a dd/MM/yyyy string of the LPM batch's first-of-month date; buckets compare against
    /// the current month's first-of-month (and +1/+2/+3 months) in that same string format.</summary>
    public async Task<WhAgingUnits> GetAgingUnitsAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            SET NOCOUNT ON;
            DECLARE @lpm_1_30 VARCHAR(15), @lpm_30_60 VARCHAR(15), @lpm_60_90 VARCHAR(15), @lpm_above_90 VARCHAR(15);
            SELECT @lpm_1_30 = CONVERT(VARCHAR(10), DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1), 103),
                   @lpm_30_60 = CONVERT(VARCHAR(10), DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)), 103),
                   @lpm_60_90 = CONVERT(VARCHAR(10), DATEADD(MONTH, 2, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)), 103),
                   @lpm_above_90 = CONVERT(VARCHAR(10), DATEADD(MONTH, 3, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)), 103);

            SELECT
                Days1To30   = CAST(ISNULL(SUM(CASE WHEN LPMDt = @lpm_1_30    THEN qty ELSE 0 END), 0) AS BIGINT),
                Days30To60  = CAST(ISNULL(SUM(CASE WHEN LPMDt = @lpm_30_60   THEN qty ELSE 0 END), 0) AS BIGINT),
                Days60To90  = CAST(ISNULL(SUM(CASE WHEN LPMDt = @lpm_60_90   THEN qty ELSE 0 END), 0) AS BIGINT),
                DaysAbove90 = CAST(ISNULL(SUM(CASE WHEN LPMDt = @lpm_above_90 THEN qty ELSE 0 END), 0) AS BIGINT),
                ElapsedLpm  = CAST(ISNULL(SUM(CASE WHEN LPMDt < @lpm_1_30    THEN qty ELSE 0 END), 0) AS BIGINT)
              FROM RACKS.dbo.WHBoxItems";
        cmd.CommandTimeout = CommandTimeoutSeconds;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        await rdr.ReadAsync(ct);
        return new WhAgingUnits(
            rdr.GetInt64(0), rdr.GetInt64(1), rdr.GetInt64(2), rdr.GetInt64(3), rdr.GetInt64(4));
    }
}
