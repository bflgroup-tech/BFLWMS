using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Backs the Container Receipt Report (Inbound).
///
/// Everything lives on the single OnPremBackup (UAE master) server —
/// bfldata/USA/etc. are sibling catalogs on that same server, reached via
/// 3-part naming. The report's "country" filter is bfldata.dbo.DataSettings
/// .SIMCountry; contreceiptExport.Country instead stores the resolved
/// DataName (e.g. "BFLKSA"), so DataName is looked up per country via the
/// same WhBoxItemsSource helper ContainerAllocation/WarehouseBoxes use.
///
/// One row per GIN:
///   - GinDate: earliest bfldata..vGoodsIssueplt.EntryDate for that GIN.
///   - ReleasedOn/ShipNo/TotalQty/TransferCount: USA.dbo.ExportPass.
///   - ReceiptDt: bfldata..contreceiptExport (also the driver/date-range table).
///   - ReceivedBoxes: count of bfldata..VerifyGin rows with Verified='Y'.
///   - GinToExportPassDays / ReleasedOnToReceiptDtDays: day-diffs for the two gaps.
///   - BoxCountDiff: TransferCount - ReceivedBoxes.
/// </summary>
public class ContainerReceiptService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 120;

    private SqlConnection OpenOnPremBackup()
    {
        var b = new SqlConnectionStringBuilder(resolver.GetOnPremBackupConnectionString())
            { ConnectTimeout = ConnectTimeoutSeconds };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(
            @"SELECT DISTINCT SIMCountry FROM bfldata..DataSettings
              WHERE SIMCountry NOT IN ('', 'ECOM', 'Ex2Locations', 'UAE')
              ORDER BY SIMCountry",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    public async Task<ContainerReceiptResult> GetContainerReceiptsAsync(
        ContainerReceiptFilter f, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(f.Country))
        {
            var rows = await GetForCountryAsync(f.Country, f.DateFrom, f.DateTo, ct);
            return new ContainerReceiptResult(rows, []);
        }

        var countries = await GetCountriesAsync(ct);
        var warnings = new List<string>();
        var tasks = countries.Select(async country =>
        {
            try
            {
                return await GetForCountryAsync(country, f.DateFrom, f.DateTo, ct);
            }
            catch (Exception ex)
            {
                lock (warnings) warnings.Add($"{country}: {ex.Message}");
                return new List<ContainerReceiptRow>();
            }
        });

        var perCountry = await Task.WhenAll(tasks);
        var allRows = perCountry.SelectMany(r => r).OrderBy(r => r.ReceiptDt).ToList();
        return new ContainerReceiptResult(allRows, warnings);
    }

    private async Task<List<ContainerReceiptRow>> GetForCountryAsync(
        string country, DateTime dateFrom, DateTime dateTo, CancellationToken ct)
    {
        var from = dateFrom.Date;
        var to   = dateTo.Date.AddDays(1).AddSeconds(-1);

        await using var conn = OpenOnPremBackup();
        var dataName = await WhBoxItemsSource.ResolveDataNameAsync(conn, country, ct);
        if (string.IsNullOrWhiteSpace(dataName))
            throw new InvalidOperationException(
                $"No DataName found in bfldata.dbo.DataSettings for country '{country}'.");

        var rawRows = await conn.QueryAsync<RawRow>(new CommandDefinition(
            Sql, new { country = dataName, from, to },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return rawRows.Select(r => new ContainerReceiptRow(
            Country:                   country,
            GinNo:                     r.GinNo,
            GinDate:                   r.GinDate,
            ReleasedOn:                r.ReleasedOn,
            GinToExportPassDays:       DayDiff(r.GinDate, r.ReleasedOn),
            ShipNo:                    r.ShipNo,
            TotalQty:                  r.TotalQty,
            TransferCount:             r.TransferCount,
            ReceiptDt:                 r.ReceiptDt,
            ReleasedOnToReceiptDtDays: DayDiff(r.ReleasedOn, r.ReceiptDt),
            ReceivedBoxes:             r.ReceivedBoxes,
            BoxCountDiff:              r.TransferCount - r.ReceivedBoxes
        )).OrderBy(r => r.ReceiptDt).ToList();
    }

    private static int? DayDiff(DateTime? from, DateTime? to) =>
        from.HasValue && to.HasValue ? (to.Value.Date - from.Value.Date).Days : null;

    private record RawRow(
        string GinNo, DateTime ReceiptDt, DateTime? GinDate, DateTime? ReleasedOn,
        string ShipNo, int TotalQty, int TransferCount, int ReceivedBoxes);

    private const string Sql = @"
        SELECT
            cre.GINNO       AS GinNo,
            cre.ReceiptDt   AS ReceiptDt,
            gi.EntryDate    AS GinDate,
            ep.ReleasedDate AS ReleasedOn,
            ISNULL(ep.Shipno,'')        AS ShipNo,
            ISNULL(ep.TotalQty,0)       AS TotalQty,
            ISNULL(ep.TransferCount,0)  AS TransferCount,
            ISNULL(vg.ReceivedBoxes,0)  AS ReceivedBoxes
        FROM bfldata..contreceiptExport cre WITH (NOLOCK)
        LEFT JOIN (
            SELECT srno, MIN(entrydate) AS EntryDate
            FROM bfldata..vGoodsIssueplt WITH (NOLOCK)
            GROUP BY srno
        ) gi ON gi.srno = cre.GINNO
        LEFT JOIN USA.dbo.ExportPass ep WITH (NOLOCK) ON ep.GINNo = cre.GINNO
        LEFT JOIN (
            SELECT GINNO, COUNT(TrfNo) AS ReceivedBoxes
            FROM bfldata..VerifyGin WITH (NOLOCK)
            WHERE Verified = 'Y'
            GROUP BY GINNO
        ) vg ON vg.GINNO = cre.GINNO
        WHERE cre.country = @country AND cre.ReceiptDt >= @from AND cre.ReceiptDt <= @to
        ORDER BY cre.ReceiptDt";
}
