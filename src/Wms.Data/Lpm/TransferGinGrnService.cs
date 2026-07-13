using System.Text;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Backs the Transfer / GIN / GRN History report.
///
/// Country list = countries that have a per-country connection string configured.
/// Stores + transfer data = connect to the COUNTRY server, then use 3-part DB names:
///   [{DataName}]..transferheader    — country DB (e.g. bflksa)
///   BFLDATA..vGoodsIssue            — shared reporting DB on the same server
/// DataName is resolved from BFLDATA.dbo.DataSettings on the country server.
/// </summary>
public class TransferGinGrnService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 300;

    private SqlConnection OpenCountry(string country)
    {
        var b = new SqlConnectionStringBuilder(resolver.GetCountryConnectionString(country))
            { ConnectTimeout = ConnectTimeoutSeconds };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    // ── Dropdowns ────────────────────────────────────────────────────────────

    /// <summary>Returns countries that have a configured DB connection string.</summary>
    public List<string> GetCountries() => resolver.GetConfiguredCountries().ToList();

    /// <summary>
    /// Stores for the selected country, queried from BFLDATA on the country's server,
    /// excluding Warehouse-concept entries.
    /// </summary>
    public async Task<List<string>> GetStoresAsync(string country, CancellationToken ct = default)
    {
        await using var c = OpenCountry(country);
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT ShopName
              FROM BFLDATA.dbo.DataSettings
             WHERE SIMCountry   = @country
               AND ShopName     IS NOT NULL
               AND ShopName     <> ''
               AND Concept      <> 'Warehouse'
             ORDER BY ShopName",
            new { country },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ── Main query ────────────────────────────────────────────────────────────

    public async Task<List<TransferHistoryRow>> GetTransferHistoryAsync(
        TransferHistoryFilter f, CancellationToken ct = default)
    {
        await using var conn = OpenCountry(f.Country);

        // Resolve the country DB name (e.g. "bflksa") from BFLDATA on that server.
        var dataName = await WhBoxItemsSource.ResolveDataNameAsync(conn, f.Country, ct);
        if (string.IsNullOrWhiteSpace(dataName))
            throw new InvalidOperationException(
                $"No DataName found in BFLDATA.dbo.DataSettings for SIMCountry '{f.Country}'.");

        var sql  = BuildSql(dataName, f, out var parms);
        var rows = await conn.QueryAsync<TransferHistoryRow>(
            new CommandDefinition(sql, parms,
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ── SQL builder ───────────────────────────────────────────────────────────

    private static string BuildSql(
        string dataName, TransferHistoryFilter f, out DynamicParameters p)
    {
        p = new DynamicParameters();
        p.Add("@from", f.DateFrom.Date);
        p.Add("@to",   f.DateTo.Date.AddDays(1).AddSeconds(-1));

        var sb = new StringBuilder($@"
SELECT ROW_NUMBER() OVER (ORDER BY a.TrfNo) SrNo,
       e.ShopName,
       a.TrfNo,
       a.TrfDate,
       PalletNo   = (SELECT TOP 1 PalletNo FROM BFLDATA.dbo.vGoodsIssue
                      WHERE TrfNo = a.TrfNo ORDER BY PalletNo DESC),
       b.EntryDate  BuildDate,
       c.SrNo       GINNo,
       c.EntryDate  GINDate,
       d.EntryNo    GRNNo,
       d.EntryDate  GRNDate,
       ISNULL(f.Remarks, '') Remarks
  FROM [{dataName}]..transferheader   a
  LEFT JOIN BFLDATA.dbo.vGoodsIssue   b ON a.TrfNo = b.TrfNo
  LEFT JOIN BFLDATA.dbo.vGoodsIssueplt c ON a.TrfNo = c.TrfNo
  LEFT JOIN [{dataName}]..GRNHeaderRF  d ON a.TrfNo = d.TrfNo
  JOIN  BFLDATA.dbo.DataSettings       e ON a.CostCodeTo = e.CostCodeTo
  LEFT JOIN [{dataName}]..TransferReverse f ON a.TrfNo = f.TrfNo
 WHERE a.TrfNo NOT LIKE 'FN%'
   AND a.TrfDate >= @from AND a.TrfDate <= @to
   AND e.ShopName NOT IN (
       SELECT ShopName FROM BFLDATA.dbo.DataSettings WHERE Concept = 'Warehouse'
   )");

        if (!string.IsNullOrWhiteSpace(f.Store))
        {
            sb.Append("\n   AND e.ShopName = @store");
            p.Add("@store", f.Store);
        }

        if (f.WithoutPallet)
            sb.Append("\n   AND NOT EXISTS (" +
                      "SELECT 1 FROM BFLDATA.dbo.vGoodsIssue WHERE TrfNo = a.TrfNo)");

        if (f.WithoutGin)
            sb.Append("\n   AND c.SrNo IS NULL");

        if (f.WithoutGrn)
            sb.Append("\n   AND d.EntryNo IS NULL");

        if (!string.IsNullOrWhiteSpace(f.SearchValue))
        {
            p.Add("@search", $"%{f.SearchValue.Trim()}%");
            sb.Append(f.SearchBy switch
            {
                "PalletNo" => "\n   AND EXISTS (SELECT 1 FROM BFLDATA.dbo.vGoodsIssue " +
                              "WHERE TrfNo = a.TrfNo AND PalletNo LIKE @search)",
                "GIN"      => "\n   AND CAST(c.SrNo AS nvarchar(50)) LIKE @search",
                "GRN"      => "\n   AND CAST(d.EntryNo AS nvarchar(50)) LIKE @search",
                _          => "\n   AND a.TrfNo LIKE @search",
            });
        }

        sb.Append("\n ORDER BY e.ShopName, a.TrfNo");
        return sb.ToString();
    }
}
