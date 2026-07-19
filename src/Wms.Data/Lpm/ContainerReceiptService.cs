using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Backs the Container Receipt Report.
///
/// Connection routing mirrors SyncDataCountService/TransferGinGrnService:
///   - UAE has no dedicated connection string — always OnPremBackup, and only
///     runs the ContReceipt/vUSAOrder branch (UAE doesn't use the
///     contreceiptExport + goodsissue/verifygin GIN/GRN flow).
///   - KSA/Kuwait/Qatar/Bahrain each have their own server. Resolve the
///     DataName via WhBoxItemsSource, then connect with InitialCatalog
///     overridden to that DataName — bfldata/usa/hodata are then local
///     sibling databases on that server.
/// </summary>
public class ContainerReceiptService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 300;

    private const string UaeWarehouse = "UAE";

    public static readonly string[] Warehouses = ["KSA", "UAE", "Kuwait", "Qatar", "Bahrain"];

    private SqlConnection OpenOnPrem()
    {
        var b = new SqlConnectionStringBuilder(resolver.GetOnPremBackupConnectionString())
            { ConnectTimeout = ConnectTimeoutSeconds };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    private async Task<SqlConnection> OpenWarehouseAsync(string warehouse, CancellationToken ct)
    {
        await using var onprem = OpenOnPrem();
        var dataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, warehouse, ct);
        if (string.IsNullOrWhiteSpace(dataName))
            throw new InvalidOperationException(
                $"No DataName found in BFLDATA.dbo.DataSettings for warehouse '{warehouse}'.");

        var csb = new SqlConnectionStringBuilder(resolver.GetCountryConnectionString(warehouse))
        {
            InitialCatalog = dataName,
            ConnectTimeout = ConnectTimeoutSeconds
        };
        var conn = new SqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    public async Task<ContainerReceiptResult> GetContainerReceiptsAsync(
        ContainerReceiptFilter f, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(f.Warehouse))
        {
            var singleRows = await GetForWarehouseAsync(f.Warehouse, f.DateFrom, f.DateTo, ct);
            return new ContainerReceiptResult(singleRows, []);
        }

        var warnings = new List<string>();
        var tasks = Warehouses.Select(async w =>
        {
            try
            {
                return await GetForWarehouseAsync(w, f.DateFrom, f.DateTo, ct);
            }
            catch (Exception ex)
            {
                lock (warnings) warnings.Add($"{w}: {ex.Message}");
                return new List<ContainerReceiptRow>();
            }
        });

        var perWarehouse = await Task.WhenAll(tasks);
        var rows = perWarehouse.SelectMany(r => r).OrderBy(r => r.ReceiptDt).ToList();
        return new ContainerReceiptResult(rows, warnings);
    }

    private async Task<List<ContainerReceiptRow>> GetForWarehouseAsync(
        string warehouse, DateTime dateFrom, DateTime dateTo, CancellationToken ct)
    {
        var from = dateFrom.Date;
        var to   = dateTo.Date.AddDays(1).AddSeconds(-1);

        if (string.Equals(warehouse, UaeWarehouse, StringComparison.OrdinalIgnoreCase))
        {
            await using var c = OpenOnPrem();
            var rows = await c.QueryAsync<ContainerReceiptRow>(new CommandDefinition(
                UaeSql, new { warehouse = UaeWarehouse, from, to },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return rows.AsList();
        }

        await using var conn = await OpenWarehouseAsync(warehouse, ct);
        var whRows = await conn.QueryAsync<ContainerReceiptRow>(new CommandDefinition(
            NonUaeSql, new { warehouse, from, to },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return whRows.AsList();
    }

    private const string NonUaeSql = @"
;WITH GoodsIssueAgg AS (
    SELECT GINNo, SUM(Qty) AS ShipmentQty, COUNT(DISTINCT TrfNo) AS BoxCount
    FROM bfldata..goodsissue WITH (NOLOCK)
    GROUP BY GINNo
),
VerifyGinAgg AS (
    SELECT GINNo, COUNT(*) AS GRNDone
    FROM bfldata..verifygin WITH (NOLOCK)
    WHERE Verified = 'Y'
    GROUP BY GINNo
),
UsaOrgAgg AS (
    SELECT ContNo, SUM(OrgQty) AS ShipmentQty
    FROM usa..usaorgfile WITH (NOLOCK)
    GROUP BY ContNo
)
SELECT
    @warehouse AS Warehouse, x.TCMNo AS ContNo, x.GinNo, x.ReceiptDt,
    '' AS InvoiceNo, x.ReceivedBy, x.Supplier AS SuppCode,
    ISNULL(gi.ShipmentQty,0) AS ShipmentQty, ISNULL(gi.BoxCount,0) AS BoxCount, ISNULL(vg.GRNDone,0) AS GRNDone
FROM bfldata.dbo.contreceiptExport x WITH (NOLOCK)
LEFT JOIN GoodsIssueAgg gi ON gi.GINNo = x.GinNo
LEFT JOIN VerifyGinAgg  vg ON vg.GINNo = x.GinNo
WHERE x.ReceiptDt >= @from AND x.ReceiptDt <= @to

UNION ALL

SELECT
    @warehouse AS Warehouse, A.TCMNo AS ContNo, '' AS GinNo, A.ReceiptDt,
    A.InvoiceNo, A.ReceivedBy, B.suppcode,
    ISNULL(uo.ShipmentQty,0) AS ShipmentQty, 0 AS BoxCount, 0 AS GRNDone
FROM BFLDATA.dbo.ContReceipt A WITH (NOLOCK)
INNER JOIN HODATA.dbo.vUSAOrder B WITH (NOLOCK) ON B.refno = A.TCMNo
LEFT JOIN UsaOrgAgg uo ON uo.ContNo = B.refno
WHERE A.ReceiptDt >= @from AND A.ReceiptDt <= @to

ORDER BY ReceiptDt;";

    private const string UaeSql = @"
;WITH UsaOrgAgg AS (
    SELECT ContNo, SUM(OrgQty) AS ShipmentQty
    FROM usa..usaorgfile WITH (NOLOCK)
    GROUP BY ContNo
)
SELECT
    @warehouse AS Warehouse, A.TCMNo AS ContNo, '' AS GinNo, A.ReceiptDt,
    A.InvoiceNo, A.ReceivedBy, B.suppcode,
    ISNULL(uo.ShipmentQty,0) AS ShipmentQty, 0 AS BoxCount, 0 AS GRNDone
FROM BFLDATA.dbo.ContReceipt A WITH (NOLOCK)
INNER JOIN HODATA.dbo.vUSAOrder B WITH (NOLOCK) ON B.refno = A.TCMNo
LEFT JOIN UsaOrgAgg uo ON uo.ContNo = B.refno
WHERE A.ReceiptDt >= @from AND A.ReceiptDt <= @to
ORDER BY ReceiptDt;";
}
