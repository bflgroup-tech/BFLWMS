using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Backs the YOTO VNA Dashboard — offloading progress for containers received at
/// the YOTO warehouse (the VNA-racked facility), split into "Total Imports" (RefNo
/// starting AEINT) and "Local Purchased" (RefNo starting AELOC).
///
/// All tables live on OnPremBackupDB as sibling catalogs:
///   - usa.dbo.UsaPallets / usa.dbo.KNBBoxes — the VNA putaway ledger. Despite the
///     column name, .Contno in BOTH tables actually stores the RefNo-style value
///     (e.g. "AEINT7703"), not bfldata..ContReceipt.ContNo (the real physical
///     container number, e.g. "WLC60706541") — confirmed live; a container only
///     shows up here once it has actually been offloaded onto pallets/into boxes.
///   - bfldata.dbo.ContReceipt — the receipt event (ContNo, RefNo, ReceiptDt,
///     Warehouse). A RefNo present here with no matching UsaPallets rows yet is
///     "pending for offloading".
///   - hodata.dbo.vUSAOrder — order/PO line items, keyed by refno; MULTIPLE rows
///     per refno (one per line item), so Qty must be pre-aggregated with
///     SUM(...) GROUP BY refno before joining, or a naive join fans out and
///     double-counts.
/// </summary>
public class YotoVnaDashboardService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 120;
    private const string Warehouse = "YOTO";

    // "Pending for offloading" containers going back years (found live: 2023-2025
    // entries with no AEINT/AELOC prefix at all) are dead/abandoned backlog that
    // never got processed — not real pending work. Flooring to the current year
    // keeps the number meaningful, same fix applied to the In-Transit floor in
    // ShipmentStatusService.
    private static DateTime PendingFloor => new(DateTime.Today.Year, 1, 1);

    private SqlConnection OpenOnPremBackup()
    {
        var b = new SqlConnectionStringBuilder(resolver.GetOnPremBackupConnectionString())
            { ConnectTimeout = ConnectTimeoutSeconds };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    private const string OrderAggCte = @"
        WITH OrderAgg AS (
            SELECT refno, SUM(Qty) AS Qty
            FROM hodata.dbo.vUSAOrder WITH (NOLOCK)
            WHERE refno IS NOT NULL
            GROUP BY refno
        )";

    /// <summary>Containers fully offloaded (present in UsaPallets/KNBBoxes) within [from, toExclusive).</summary>
    public async Task<List<YotoOffloadGroupRow>> GetCompletedOffloadingAsync(
        DateTime from, DateTime toExclusive, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<YotoOffloadGroupRow>(new CommandDefinition($@"
            {OrderAggCte},
            ContainerLevel AS (
                SELECT
                    a.Contno,
                    COUNT(DISTINCT a.PalletNo) AS Pallets,
                    COUNT(DISTINCT b.Boxno)    AS Boxes
                FROM usa.dbo.UsaPallets a WITH (NOLOCK)
                JOIN usa.dbo.KNBBoxes b WITH (NOLOCK)
                    ON a.PalletNo = b.palletno AND a.Contno = b.Contno
                JOIN bfldata.dbo.ContReceipt cr WITH (NOLOCK) ON cr.RefNo = a.Contno
                JOIN OrderAgg oa ON oa.refno = a.Contno
                WHERE a.whouse = @wh
                  AND (a.Contno LIKE 'AEINT%' OR a.Contno LIKE 'AELOC%')
                  AND a.trndate >= @from AND a.trndate < @to
                GROUP BY a.Contno
            )
            SELECT
                CASE WHEN Contno LIKE 'AEINT%' THEN 'AEINT' ELSE 'AELOC' END AS [Group],
                COUNT(*)      AS Containers,
                SUM(Pallets)  AS Pallets,
                SUM(Boxes)    AS Boxes
            FROM ContainerLevel
            GROUP BY CASE WHEN Contno LIKE 'AEINT%' THEN 'AEINT' ELSE 'AELOC' END",
            new { wh = Warehouse, from, to = toExclusive },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Containers received (bfldata..ContReceipt) but not yet offloaded, as of now.</summary>
    public async Task<List<YotoPendingGroupRow>> GetPendingOffloadingAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<YotoPendingGroupRow>(new CommandDefinition($@"
            {OrderAggCte}
            SELECT
                CASE WHEN cr.RefNo LIKE 'AEINT%' THEN 'AEINT' ELSE 'AELOC' END AS [Group],
                COUNT(DISTINCT cr.ContNo)      AS Containers,
                SUM(ISNULL(oa.Qty, 0))         AS Qty
            FROM bfldata.dbo.ContReceipt cr WITH (NOLOCK)
            JOIN OrderAgg oa ON oa.refno = cr.RefNo
            WHERE cr.Warehouse = @wh
              AND cr.ReceiptDt >= @floor
              AND (cr.RefNo LIKE 'AEINT%' OR cr.RefNo LIKE 'AELOC%')
              AND NOT EXISTS (
                  SELECT 1 FROM usa.dbo.UsaPallets a WITH (NOLOCK) WHERE a.Contno = cr.RefNo
              )
            GROUP BY CASE WHEN cr.RefNo LIKE 'AEINT%' THEN 'AEINT' ELSE 'AELOC' END",
            new { wh = Warehouse, floor = PendingFloor },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Cumulative offload totals per calendar month of the given year (every container, not just AEINT/AELOC).</summary>
    public async Task<List<YotoInboundPeriodRow>> GetMonthlyInboundSummaryAsync(int year, CancellationToken ct = default)
    {
        var yearStart = new DateTime(year, 1, 1);
        var yearEnd = yearStart.AddYears(1);

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<(int Mo, int Containers, int Pallets, int Boxes, int Pcs)>(new CommandDefinition($@"
            {OrderAggCte},
            ContainerLevel AS (
                SELECT
                    a.Contno,
                    MIN(a.trndate)              AS TrnDate,
                    COUNT(DISTINCT a.PalletNo)  AS Pallets,
                    COUNT(DISTINCT b.Boxno)     AS Boxes,
                    MAX(oa.Qty)                 AS Pcs
                FROM usa.dbo.UsaPallets a WITH (NOLOCK)
                JOIN usa.dbo.KNBBoxes b WITH (NOLOCK)
                    ON a.PalletNo = b.palletno AND a.Contno = b.Contno
                JOIN bfldata.dbo.ContReceipt cr WITH (NOLOCK) ON cr.RefNo = a.Contno
                JOIN OrderAgg oa ON oa.refno = a.Contno
                WHERE a.whouse = @wh
                  AND a.trndate >= @yearStart AND a.trndate < @yearEnd
                GROUP BY a.Contno
            )
            SELECT
                MONTH(TrnDate)  AS Mo,
                COUNT(*)        AS Containers,
                SUM(Pallets)    AS Pallets,
                SUM(Boxes)      AS Boxes,
                SUM(Pcs)        AS Pcs
            FROM ContainerLevel
            GROUP BY MONTH(TrnDate)
            ORDER BY Mo",
            new { wh = Warehouse, yearStart, yearEnd },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return rows.Select(r => new YotoInboundPeriodRow(
            new DateTime(2000, r.Mo, 1).ToString("MMM"), r.Mo, r.Containers, r.Pallets, r.Boxes, r.Pcs)).ToList();
    }

    /// <summary>Cumulative offload totals per calendar week within the given month (every container, not just AEINT/AELOC).</summary>
    /// <summary>Container-level offload totals for one explicit date range -- used for the single
    /// selected week (matching Production Summary Report's Sun-Sat week picker) instead of
    /// spanning multiple weeks the way the Monthly view spans multiple months.</summary>
    public async Task<YotoInboundPeriodRow> GetInboundSummaryForRangeAsync(
        DateTime from, DateTime toExclusive, string periodLabel, int periodIndex, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var row = await c.QuerySingleAsync<(int Containers, int Pallets, int Boxes, int Pcs)>(new CommandDefinition($@"
            {OrderAggCte},
            ContainerLevel AS (
                SELECT
                    a.Contno,
                    COUNT(DISTINCT a.PalletNo)  AS Pallets,
                    COUNT(DISTINCT b.Boxno)     AS Boxes,
                    MAX(oa.Qty)                 AS Pcs
                FROM usa.dbo.UsaPallets a WITH (NOLOCK)
                JOIN usa.dbo.KNBBoxes b WITH (NOLOCK)
                    ON a.PalletNo = b.palletno AND a.Contno = b.Contno
                JOIN bfldata.dbo.ContReceipt cr WITH (NOLOCK) ON cr.RefNo = a.Contno
                JOIN OrderAgg oa ON oa.refno = a.Contno
                WHERE a.whouse = @wh
                  AND a.trndate >= @from AND a.trndate < @to
                GROUP BY a.Contno
            )
            SELECT
                COUNT(*)            AS Containers,
                ISNULL(SUM(Pallets), 0) AS Pallets,
                ISNULL(SUM(Boxes), 0)   AS Boxes,
                ISNULL(SUM(Pcs), 0)     AS Pcs
            FROM ContainerLevel",
            new { wh = Warehouse, from, to = toExclusive },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return new YotoInboundPeriodRow(periodLabel, periodIndex, row.Containers, row.Pallets, row.Boxes, row.Pcs);
    }

    // One definition per "box" on the Internal Transfer Summary section. FromWarehouse/
    // ToWarehouse null = "any" (the Total Inbound/Outbound boxes only pin the YOTO side;
    // confirmed live that these are NOT the sum of the per-partner boxes below -- a
    // single trailer can serve more than one route in a day, so COUNT(DISTINCT
    // trailerno) across all partners is naturally <= the sum of per-partner counts).
    private static readonly (string Label, string CountLabel, string? From, string? To)[] InternalTransferDefs =
    {
        ("Total Inbound",       "NO. OF TRAILERS",   null,     "YOTO"),
        ("Total Outbound",      "NO. OF TRAILERS",   "YOTO",   null),
        ("Inbound from JAFZA",  "NO. OF TRAILERS",   "JAFZA",  "YOTO"),
        ("Outbound to JAFZA",   "NO. OF TRAILERS",   "YOTO",   "JAFZA"),
        ("Inbound from Techno", "NO. OF TRAILERS",   "TECHNO", "YOTO"),
        ("Outbound to Techno",  "NO. OF TRAILERS",   "YOTO",   "TECHNO"),
        ("Inbound from Online", "NO. OF TRAILERS",   "ONLINE", "YOTO"),
        ("Outbound to Online",  "NO. OF GIN",         "YOTO",   "ONLINE"),
    };

    // The partner warehouse for a box's badge -- whichever of From/To isn't "YOTO".
    // Both are null for the two Total boxes (one side is YOTO, the other is "any").
    private static string? PartnerWarehouse((string Label, string CountLabel, string? From, string? To) d) =>
        d.From == Warehouse ? d.To : d.To == Warehouse ? d.From : null;

    /// <summary>All 8 Internal Transfer Summary boxes, each spanning every month of the given year.</summary>
    public async Task<List<YotoInternalTransferBox>> GetInternalTransferMonthlyAsync(
        int year, CancellationToken ct = default)
    {
        var yearStart = new DateTime(year, 1, 1);
        var yearEnd = yearStart.AddYears(1);

        await using var c = OpenOnPremBackup();
        var boxes = new List<YotoInternalTransferBox>();
        foreach (var d in InternalTransferDefs)
        {
            var rows = await c.QueryAsync<(int Mo, int Trips, int Pallets, int Boxes, int Quantity)>(new CommandDefinition(@"
                SELECT
                    MONTH(EntryDate)          AS Mo,
                    COUNT(DISTINCT trailerno) AS Trips,
                    COUNT(DISTINCT PalletNo)  AS Pallets,
                    COUNT(DISTINCT Boxno)     AS Boxes,
                    ISNULL(SUM(qty), 0)       AS Quantity
                FROM bfldata.dbo.vPLTDeliveryDetails WITH (NOLOCK)
                WHERE EntryDate >= @yearStart AND EntryDate < @yearEnd
                  AND (@fromWh IS NULL OR WarehouseFrom = @fromWh)
                  AND (@toWh IS NULL OR WarehouseTo = @toWh)
                GROUP BY MONTH(EntryDate)
                ORDER BY Mo",
                new { yearStart, yearEnd, fromWh = d.From, toWh = d.To },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var periods = rows.Select(r => new YotoInternalTransferPeriodRow(
                new DateTime(2000, r.Mo, 1).ToString("MMM"), r.Mo, r.Trips, r.Pallets, r.Boxes, r.Quantity)).ToList();
            boxes.Add(new YotoInternalTransferBox(d.Label, d.CountLabel, PartnerWarehouse(d), periods));
        }
        return boxes;
    }

    /// <summary>All 8 Internal Transfer Summary boxes for one explicit date range -- used for the
    /// single selected week (matching Production Summary Report's Sun-Sat week picker) instead of
    /// spanning multiple weeks the way the Monthly view spans multiple months.</summary>
    public async Task<List<YotoInternalTransferBox>> GetInternalTransferForRangeAsync(
        DateTime from, DateTime toExclusive, string periodLabel, int periodIndex, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var boxes = new List<YotoInternalTransferBox>();
        foreach (var d in InternalTransferDefs)
        {
            var row = await c.QuerySingleAsync<(int Trips, int Pallets, int Boxes, int Quantity)>(new CommandDefinition(@"
                SELECT
                    COUNT(DISTINCT trailerno) AS Trips,
                    COUNT(DISTINCT PalletNo)  AS Pallets,
                    COUNT(DISTINCT Boxno)     AS Boxes,
                    ISNULL(SUM(qty), 0)       AS Quantity
                FROM bfldata.dbo.vPLTDeliveryDetails WITH (NOLOCK)
                WHERE EntryDate >= @from AND EntryDate < @to
                  AND (@fromWh IS NULL OR WarehouseFrom = @fromWh)
                  AND (@toWh IS NULL OR WarehouseTo = @toWh)",
                new { from, to = toExclusive, fromWh = d.From, toWh = d.To },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var periods = new List<YotoInternalTransferPeriodRow>
            {
                new(periodLabel, periodIndex, row.Trips, row.Pallets, row.Boxes, row.Quantity)
            };
            boxes.Add(new YotoInternalTransferBox(d.Label, d.CountLabel, PartnerWarehouse(d), periods));
        }
        return boxes;
    }
}
