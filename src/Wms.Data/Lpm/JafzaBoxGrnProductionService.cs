using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// JAFZA Box GRN Production Report. Qty = SUM(ScannedQty) from
/// BFLDATA.dbo.SuppBoxItemGrnDetail joined to BFLDATA.dbo.SuppBoxItemGrnHeader
/// on SrNo (Warehouse = 'JAFZA'). Division comes from HODATA.dbo.ItemMaster.GroupCode
/// via ItemCode, then USA.dbo.USAPriority.DivisionY via GroupCode — rows with
/// no matching/blank Division are dropped, same as Manual/Robo.
/// </summary>
public class JafzaBoxGrnProductionService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 120;

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    private record RawRow(DateTime TrnDate, string ItemCode, string? GroupCode, string? Division, int Qty);

    private const string RawQuerySql = @"
        SELECT
            TrnDate   = h.CreateDate,
            ItemCode  = d.Itemcode,
            GroupCode = im.GroupCode,
            Division  = p.DivisionY,
            Qty       = d.ScannedQty
          FROM BFLDATA.dbo.suppboxitemgrnheader h WITH (NOLOCK)
          JOIN BFLDATA.dbo.SuppBoxItemGrnDetail d WITH (NOLOCK) ON d.SrNo = h.SrNo
          LEFT JOIN HODATA.dbo.ItemMaster im WITH (NOLOCK) ON im.ItemCode = d.Itemcode
          LEFT JOIN USA.dbo.USAPriority p WITH (NOLOCK) ON p.GroupCode = im.GroupCode
         WHERE h.Warehouse = 'JAFZA'
           AND h.CreateDate >= @from AND h.CreateDate <= @to";

    private async Task<List<RawRow>> FetchRawAsync(DateTime fromDate, DateTime toDate, CancellationToken ct)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<RawRow>(new CommandDefinition(
            RawQuerySql, new { from = fromDate.Date, to = toDate.Date },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(r => !string.IsNullOrWhiteSpace(r.Division)).AsList();
    }

    /// <summary>Division-wise summary — one row per (TrnDate, Division).</summary>
    public async Task<List<JafzaBoxGrnSummaryRow>> GetSummaryAsync(
        DateTime fromDate, DateTime toDate, CancellationToken ct = default)
    {
        var raw = await FetchRawAsync(fromDate, toDate, ct);
        return raw
            .GroupBy(r => (r.TrnDate, r.Division))
            .Select(g => new JafzaBoxGrnSummaryRow(g.Key.TrnDate, g.Key.Division!, g.Sum(x => x.Qty)))
            .OrderBy(r => r.TrnDate).ThenBy(r => r.Division)
            .ToList();
    }

    /// <summary>Item-wise detail — one row per (TrnDate, ItemCode, GroupCode, Division).</summary>
    public async Task<List<JafzaBoxGrnDetailRow>> GetDetailAsync(
        DateTime fromDate, DateTime toDate, CancellationToken ct = default)
    {
        var raw = await FetchRawAsync(fromDate, toDate, ct);
        return raw
            .GroupBy(r => (r.TrnDate, r.ItemCode, r.GroupCode, r.Division))
            .Select(g => new JafzaBoxGrnDetailRow(g.Key.TrnDate, g.Key.ItemCode, g.Key.GroupCode, g.Key.Division!, g.Sum(x => x.Qty)))
            .OrderBy(r => r.TrnDate).ThenBy(r => r.ItemCode)
            .ToList();
    }

    /// <summary>Total Qty only, for the summary-card total. Just sums GetSummaryAsync's rows,
    /// same as the Robo/Export services — this table's volume is small (GRN audit records).
    /// Optionally restricted to a set of Divisions (from the report's Divisions filter).</summary>
    public async Task<int> GetTotalQtyAsync(
        DateTime fromDate, DateTime toDate, IReadOnlyCollection<string>? divisions = null, CancellationToken ct = default)
    {
        var rows = await GetSummaryAsync(fromDate, toDate, ct);
        return rows
            .Where(r => divisions is not { Count: > 0 } || divisions.Contains(r.Division, StringComparer.OrdinalIgnoreCase))
            .Sum(r => r.Qty);
    }
}
