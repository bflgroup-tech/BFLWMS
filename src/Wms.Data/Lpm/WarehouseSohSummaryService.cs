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

    // Sub-codes folded into their parent warehouse for display -- TECHNO-E is part of
    // TECHNO, YOTO-BU is part of YOTO. LFL-WH/3PLF&B stay as their own rows since they
    // aren't sub-codes of any of the 4 main warehouses.
    private const string WarehouseGroupCaseSql = @"
        CASE WHEN ISNULL(Warehouse, '') = ''  THEN 'TECHNO'
             WHEN Warehouse = 'TECHNO-E'      THEN 'TECHNO'
             WHEN Warehouse = 'YOTO-BU'       THEN 'YOTO'
             ELSE Warehouse
        END";

    /// <summary>Stock On Hand grouped by Warehouse, one row per distinct group in
    /// RACKS.dbo.WHBoxItems -- blank/NULL Warehouse rows are folded into TECHNO instead
    /// of being silently excluded (as an exact-match WHERE Warehouse = 'TECHNO' would
    /// do), and TECHNO-E/YOTO-BU are folded into their parent TECHNO/YOTO. LFL-WH and
    /// 3PLF&amp;B still come back as their own distinct rows.</summary>
    public async Task<List<(string Warehouse, WhStockOnHand Stock)>> GetStockOnHandByWarehouseAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                Warehouse = {WarehouseGroupCaseSql},
                TotalQuantity     = CAST(ISNULL(SUM(qty), 0) AS BIGINT),
                TotalBoxesStock   = CAST(ISNULL(SUM(CASE WHEN BoxNo <> '' THEN qty ELSE 0 END), 0) AS BIGINT),
                NumberOfBoxes     = CAST(COUNT(DISTINCT CASE WHEN BoxNo <> '' THEN BoxNo END) AS BIGINT),
                TotalPalletsStock = CAST(ISNULL(SUM(CASE WHEN PalletNo <> '' THEN qty ELSE 0 END), 0) AS BIGINT),
                NumberOfPallets   = CAST(COUNT(DISTINCT CASE WHEN PalletNo <> '' THEN PalletNo END) AS BIGINT),
                TotalActiveSkus   = CAST(COUNT(DISTINCT ItemCode) AS BIGINT)
              FROM RACKS.dbo.WHBoxItems
             GROUP BY {WarehouseGroupCaseSql}
             ORDER BY 1";
        cmd.CommandTimeout = CommandTimeoutSeconds;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var result = new List<(string Warehouse, WhStockOnHand Stock)>();
        while (await rdr.ReadAsync(ct))
        {
            result.Add((
                rdr.GetString(0),
                new WhStockOnHand(
                    rdr.GetInt64(1), rdr.GetInt64(2), rdr.GetInt64(3), rdr.GetInt64(4), rdr.GetInt64(5), rdr.GetInt64(6))));
        }
        return result;
    }

    /// <summary>Stock On Hand for a non-UAE country, from that country's own
    /// {DataName}.dbo.WHBoxItemsExport -- DataName resolved the same way the Warehouse
    /// Box Details report does (WhBoxItemsSource, via bfldata.dbo.DataSettings.DataName),
    /// instead of a hardcoded country->database map. No per-warehouse split -- each of
    /// these countries has a single warehouse facility, so this is the country's whole
    /// total. Throws if no DataName is configured for this country -- callers should
    /// catch and skip it.</summary>
    public async Task<WhStockOnHand> GetStockOnHandForCountryAsync(string country, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var src = await WhBoxItemsSource.ResolveAsync(c, country, ct);

        await using var cmd = c.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                TotalQuantity     = CAST(ISNULL(SUM(Qty), 0) AS BIGINT),
                TotalBoxesStock   = CAST(ISNULL(SUM(CASE WHEN BoxNo <> '' THEN Qty ELSE 0 END), 0) AS BIGINT),
                NumberOfBoxes     = CAST(COUNT(DISTINCT CASE WHEN BoxNo <> '' THEN BoxNo END) AS BIGINT),
                TotalPalletsStock = CAST(ISNULL(SUM(CASE WHEN PalletNo <> '' THEN Qty ELSE 0 END), 0) AS BIGINT),
                NumberOfPallets   = CAST(COUNT(DISTINCT CASE WHEN PalletNo <> '' THEN PalletNo END) AS BIGINT),
                TotalActiveSkus   = CAST(COUNT(DISTINCT ItemCode) AS BIGINT)
              FROM {src}";
        cmd.CommandTimeout = CommandTimeoutSeconds;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        await rdr.ReadAsync(ct);
        return new WhStockOnHand(
            rdr.GetInt64(0), rdr.GetInt64(1), rdr.GetInt64(2), rdr.GetInt64(3), rdr.GetInt64(4), rdr.GetInt64(5));
    }

    /// <summary>Storage Capacity for a non-UAE country, from that country's own
    /// {DataName}.dbo.BinRackMaster (total slots, one row per physical Barcode) and
    /// dbo.BinRack (filled slots -- DataName resolved via WhBoxItemsSource, same as
    /// Stock On Hand above). Free/Filled are computed by matching BinRackMaster's
    /// Barcode against BinRack's distinct Location, not a plain COUNT(*) difference --
    /// BinRack can carry duplicate/stale Location rows that don't map 1:1 onto physical
    /// slots, which previously produced nonsensical results like &gt;100% utilization.
    /// Same shape as UAE's BINRACK rack type, no separate pallet-rack table, so
    /// everything here counts as "box" slots and the pallet slots stay 0. BinRackMaster
    /// doesn't exist in these databases yet for every country (pending), so this throws
    /// until it's created there -- callers should catch and default to zero.</summary>
    public async Task<WhStorageCapacity> GetStorageCapacityForCountryAsync(string country, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var dataName = await WhBoxItemsSource.ResolveDataNameAsync(c, country, ct)
            ?? throw new ArgumentException($"'{country}' resolves to the UAE source, not a per-country database.", nameof(country));

        await using var cmd = c.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                TotalRackLocations   = CAST(ISNULL((SELECT COUNT(*) FROM [{dataName}].dbo.BinRackMaster), 0) AS BIGINT),
                FreeBoxRackLocations = CAST(ISNULL((SELECT COUNT(*) FROM [{dataName}].dbo.BinRackMaster
                                                      WHERE Barcode NOT IN (SELECT DISTINCT Location FROM [{dataName}].dbo.BinRack)), 0) AS BIGINT),
                FilledBoxLocations   = CAST(ISNULL((SELECT COUNT(*) FROM [{dataName}].dbo.BinRackMaster
                                                      WHERE Barcode IN (SELECT DISTINCT Location FROM [{dataName}].dbo.BinRack)), 0) AS BIGINT)";
        cmd.CommandTimeout = CommandTimeoutSeconds;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        await rdr.ReadAsync(ct);
        return new WhStorageCapacity(
            rdr.GetInt64(0), rdr.GetInt64(1), rdr.GetInt64(2), 0, 0);
    }

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
        -- BUG FIX: WAREHOUSE-type rows (TECHNO-E/YOTO/YOTO-BU/YOTO-SF) never had `used`
        -- populated at all, so Free Pallet Rack always showed the full total capacity
        -- and Utilization ignored their occupancy entirely. WarehouseRackDet is the
        -- filled-cell detail table for WarehouseRacks' total capacity -- every row there
        -- already represents an occupied cell (verified: no blank PalletNo1/PalletNo2).
        UPDATE #racklocation SET used = (SELECT COUNT(*) FROM racks.dbo.WarehouseRackDet WHERE Warehouse = a.warehouse) FROM #racklocation a WHERE racktype = 'WAREHOUSE';
        -- Same bug, same fix, for TECHNORACK: TechnoRackDet is TechnoRacks' filled-cell
        -- detail table -- no Warehouse column since it's implicitly TECHNO-only, same as
        -- TechnoRacks itself.
        UPDATE #racklocation SET used = (SELECT COUNT(*) FROM racks.dbo.TechnoRackDet) WHERE racktype = 'TECHNORACK';
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

    /// <summary>Storage Capacity for a warehouse group (e.g. 'TECHNO', 'JAFZA', 'YOTO',
    /// 'BlackBOX' -- case-insensitive against #racklocation's 'BLACKBOX' row). TECHNO-E's
    /// and YOTO-BU's #racklocation rows are folded into TECHNO/YOTO respectively (same
    /// grouping as GetStockOnHandByWarehouseAsync), so passing 'TECHNO' or 'YOTO' picks up
    /// their sub-code rows too. Works for BlackBOX too -- it only ever has one BINRACK-type
    /// row, so Box picks it up and Pallet naturally comes back 0, matching
    /// GetStorageCapacityForBlackboxAsync's shape.</summary>
    public async Task<WhStorageCapacity> GetStorageCapacityForWarehouseAsync(string warehouse, CancellationToken ct = default)
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
             WHERE (CASE WHEN warehouse = 'TECHNO-E' THEN 'TECHNO'
                         WHEN warehouse = 'YOTO-BU'  THEN 'YOTO'
                         ELSE warehouse
                    END) = @warehouse;
            DROP TABLE #racklocation;";
        cmd.Parameters.Add(new SqlParameter("@warehouse", warehouse));
        cmd.CommandTimeout = CommandTimeoutSeconds;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        await rdr.ReadAsync(ct);
        return new WhStorageCapacity(
            rdr.GetInt64(0), rdr.GetInt64(1), rdr.GetInt64(2), rdr.GetInt64(3), rdr.GetInt64(4));
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

    /// <summary>Critical Alerts &amp; Aging Inventory units for a non-UAE country, from that
    /// country's own {DataName}.dbo.WHBoxItemsExport (DataName resolved via
    /// WhBoxItemsSource, same as Stock On Hand). LPMDt is a real DATE column here
    /// (first-of-month), unlike UAE's varchar dd/MM/yyyy column, so buckets compare dates
    /// directly instead of string-converting.</summary>
    public async Task<WhAgingUnits> GetAgingUnitsForCountryAsync(string country, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var src = await WhBoxItemsSource.ResolveAsync(c, country, ct);

        await using var cmd = c.CreateCommand();
        cmd.CommandText = $@"
            SET NOCOUNT ON;
            DECLARE @m0 DATE = DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1);
            DECLARE @m1 DATE = DATEADD(MONTH, 1, @m0);
            DECLARE @m2 DATE = DATEADD(MONTH, 2, @m0);
            DECLARE @m3 DATE = DATEADD(MONTH, 3, @m0);

            SELECT
                Days1To30   = CAST(ISNULL(SUM(CASE WHEN LPMDt = @m0 THEN Qty ELSE 0 END), 0) AS BIGINT),
                Days30To60  = CAST(ISNULL(SUM(CASE WHEN LPMDt = @m1 THEN Qty ELSE 0 END), 0) AS BIGINT),
                Days60To90  = CAST(ISNULL(SUM(CASE WHEN LPMDt = @m2 THEN Qty ELSE 0 END), 0) AS BIGINT),
                DaysAbove90 = CAST(ISNULL(SUM(CASE WHEN LPMDt = @m3 THEN Qty ELSE 0 END), 0) AS BIGINT),
                ElapsedLpm  = CAST(ISNULL(SUM(CASE WHEN LPMDt < @m0 THEN Qty ELSE 0 END), 0) AS BIGINT)
              FROM {src}";
        cmd.CommandTimeout = CommandTimeoutSeconds;
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        await rdr.ReadAsync(ct);
        return new WhAgingUnits(
            rdr.GetInt64(0), rdr.GetInt64(1), rdr.GetInt64(2), rdr.GetInt64(3), rdr.GetInt64(4));
    }
}
