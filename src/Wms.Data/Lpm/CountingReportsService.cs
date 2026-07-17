using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// Data services for the two Counting reports:
///   - Counting Report (Summary + Detail) — one row per (ContNo, PONo);
///     Detail per ContNo pulls from WmsUPCBoxHead + WmsUPCBoxDet.
///   - Cont Counting Production Report — one row per (ContNo, TrnDate, User)
///     grouped from WMSContBuildScanData.
///
/// Summary reads Azure WMS DB (allocation + scan ledger + completion tables)
/// and OnPremBackup (bfldata.Contreceipt.ReceiptDt + usa.usapurchase.TrnDate).
/// RTV and MC Hold pallet-type codes are looked up from dbo.WmsPalletType by
/// TypeName LIKE — adjust the LIKE pattern if a stricter match is needed.
/// </summary>
public class CountingReportsService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 180;

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(resolver.GetWmsAzureConnectionString());
        c.Open();
        return c;
    }

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    /// <summary>Summary rows, one per (ContNo, ORAPONo). Country-scoped.</summary>
    public async Task<List<CountingSummaryRow>> GetSummaryAsync(string? country, CancellationToken ct = default)
    {
        // 1) Pull the Azure part.
        var azureRows = new List<AzureSummaryRow>();
        await using (var w = OpenWms())
        {
            var sql = @"
                WITH RtvPT AS (
                    SELECT PalletType FROM dbo.WmsPalletType WITH (NOLOCK)
                     WHERE UPPER(TypeName) LIKE '%RTV%' OR UPPER(PalletType) = 'RTV'
                ),
                McHoldPT AS (
                    SELECT PalletType FROM dbo.WmsPalletType WITH (NOLOCK)
                     WHERE UPPER(TypeName) LIKE '%MC%HOLD%' OR UPPER(PalletType) IN ('MCH','MCHOLD')
                ),
                Started AS (
                    SELECT ContNo, MIN(ScannedTS) AS CountingStartedDt
                      FROM dbo.WMSContBuildScanData WITH (NOLOCK)
                     WHERE Reversed = 'N'
                       AND (@ct IS NULL OR Country = @ct)
                     GROUP BY ContNo
                ),
                Completed AS (
                    SELECT ContNo,
                           MAX(CAST(Trndate AS DATETIME) + CAST(ISNULL(TrnTime, '00:00:00') AS DATETIME)) AS CountingCompletedDt
                      FROM dbo.WmsBuildingCompletion WITH (NOLOCK)
                     WHERE (@ct IS NULL OR Country = @ct)
                     GROUP BY ContNo
                )
                SELECT a.ContNo,
                       a.ORAPONo AS PONo,
                       SUM(ISNULL(a.AllocatedQty,0)) AS OrderSheetQty,
                       SUM(ISNULL(a.QtyIssue,0))    AS CountedQty,
                       SUM(CASE WHEN a.FinalResult IN (SELECT PalletType FROM RtvPT)    THEN ISNULL(a.AllocatedQty,0) ELSE 0 END) AS RtvQty,
                       SUM(CASE WHEN a.FinalResult IN (SELECT PalletType FROM McHoldPT) THEN ISNULL(a.AllocatedQty,0) ELSE 0 END) AS McHoldQty,
                       MIN(st.CountingStartedDt)    AS CountingStartedDt,
                       MAX(cp.CountingCompletedDt)  AS CountingCompletedDt
                  FROM dbo.WMS_ContAllocationData a WITH (NOLOCK)
                  LEFT JOIN Started   st ON st.ContNo = a.ContNo
                  LEFT JOIN Completed cp ON cp.ContNo = a.ContNo
                 WHERE (@ct IS NULL OR a.Country = @ct)
                 GROUP BY a.ContNo, a.ORAPONo
                 ORDER BY a.ContNo, a.ORAPONo";
            azureRows = (await w.QueryAsync<AzureSummaryRow>(new CommandDefinition(
                sql, new { ct = country }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
        }

        if (azureRows.Count == 0) return new();

        // 2) Enrich with ReceiptDt (bfldata.Contreceipt) + PurchaseDt (usa.usapurchase).
        var contnos = azureRows.Select(r => r.ContNo).Distinct().ToArray();
        var receiptLookup  = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var purchaseLookup = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await using var opb = OpenOnPremBackup();
            const int chunkSize = 1000;
            for (int i = 0; i < contnos.Length; i += chunkSize)
            {
                var chunk = contnos.Skip(i).Take(chunkSize).ToArray();

                try
                {
                    var receiptRows = await opb.QueryAsync<(string ContNo, DateTime? ReceiptDt)>(new CommandDefinition(
                        @"SELECT TCMNo AS ContNo, MAX(ReceiptDt) AS ReceiptDt
                            FROM bfldata.dbo.Contreceipt WITH (NOLOCK)
                           WHERE TCMNo IN @c
                           GROUP BY TCMNo",
                        new { c = chunk }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                    foreach (var r in receiptRows)
                        if (r.ReceiptDt is DateTime dt) receiptLookup[r.ContNo] = dt;
                }
                catch { /* silent — leave as NULL */ }

                try
                {
                    // usa.dbo.usapurchase — TrnDate by ContNo (best-effort; schema
                    // may vary — falls through cleanly on any column mismatch).
                    var purchaseRows = await opb.QueryAsync<(string ContNo, DateTime? TrnDate)>(new CommandDefinition(
                        @"SELECT ContNo, MAX(TrnDate) AS TrnDate
                            FROM usa.dbo.usapurchase WITH (NOLOCK)
                           WHERE ContNo IN @c
                           GROUP BY ContNo",
                        new { c = chunk }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                    foreach (var r in purchaseRows)
                        if (r.TrnDate is DateTime dt) purchaseLookup[r.ContNo] = dt;
                }
                catch { /* silent — leave as NULL */ }
            }
        }
        catch { /* enrichment is best-effort */ }

        return azureRows.Select(r => new CountingSummaryRow(
            ContNo:              r.ContNo,
            PONo:                r.PONo,
            ReceiptDt:           receiptLookup.TryGetValue(r.ContNo, out var rd) ? rd : null,
            CountingStartedDt:   r.CountingStartedDt,
            OrderSheetQty:       r.OrderSheetQty,
            CountedQty:          r.CountedQty,
            RtvQty:              r.RtvQty,
            McHoldQty:           r.McHoldQty,
            CountingCompletedDt: r.CountingCompletedDt,
            PurchaseDt:          purchaseLookup.TryGetValue(r.ContNo, out var pd) ? pd : null
        )).ToList();
    }

    /// <summary>Detail rows for a single ContNo, from WmsUPCBoxHead + WmsUPCBoxDet.</summary>
    public async Task<List<CountingDetailRow>> GetDetailByContnoAsync(string contno, string? country, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno)) return new();
        await using var w = OpenWms();
        var rows = await w.QueryAsync<CountingDetailRow>(new CommandDefinition(@"
            SELECT h.PONo,
                   d.BoxNo,
                   h.LPMDT       AS LpmDt,
                   h.ToteID      AS ToteId,
                   h.PalletType,
                   pt.TypeName   AS PalletTypeName,
                   d.StoreId,
                   d.Itemcode,
                   d.Qty,
                   ContNo = ISNULL(d.imgfile, @c)
              FROM dbo.WmsUPCBoxDet d WITH (NOLOCK)
              JOIN dbo.WmsUPCBoxHead h WITH (NOLOCK) ON h.Country = d.Country AND h.BoxNo = d.BoxNo
              OUTER APPLY (
                   SELECT TOP 1 TypeName FROM dbo.WmsPalletType WITH (NOLOCK) WHERE PalletType = h.PalletType
              ) pt
             WHERE ISNULL(d.imgfile, '') = @c
               AND (@ct IS NULL OR d.Country = @ct)
             ORDER BY d.BoxNo, d.SrNo",
            new { c = contno.Trim(), ct = country },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Cont Counting Production Report — grouped by (ContNo, TrnDate, ScannedBy).</summary>
    public async Task<List<CountingProductionRow>> GetProductionAsync(
        DateTime fromDate, DateTime toDate, string? country, CancellationToken ct = default)
    {
        await using var w = OpenWms();
        var rows = await w.QueryAsync<CountingProductionRow>(new CommandDefinition(@"
            SELECT s.ContNo,
                   CAST(s.ScannedTS AS DATE) AS TrnDate,
                   s.ScannedBy               AS UserName,
                   COUNT_BIG(*)              AS Qty
              FROM dbo.WMSContBuildScanData s WITH (NOLOCK)
             WHERE s.Reversed = 'N'
               AND s.ScannedTS >= @from
               AND s.ScannedTS <  DATEADD(day, 1, @to)
               AND (@ct IS NULL OR s.Country = @ct)
             GROUP BY s.ContNo, CAST(s.ScannedTS AS DATE), s.ScannedBy
             ORDER BY s.ContNo, s.ScannedBy, TrnDate",
            new { from = fromDate.Date, to = toDate.Date, ct = country },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    private sealed class AzureSummaryRow
    {
        public string    ContNo              { get; set; } = "";
        public string?   PONo                { get; set; }
        public int       OrderSheetQty       { get; set; }
        public int       CountedQty          { get; set; }
        public int       RtvQty              { get; set; }
        public int       McHoldQty           { get; set; }
        public DateTime? CountingStartedDt   { get; set; }
        public DateTime? CountingCompletedDt { get; set; }
    }
}
