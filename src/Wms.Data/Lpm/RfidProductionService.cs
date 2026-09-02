using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

public record RfidProductionRow(DateTime TrnDate, string? Username, int Qty);

/// <summary>
/// RFID PDA production counts per user per day, from BFLDATA.dbo.TransferNoReturn
/// (Dept LIKE '%APDA%') joined to BFLDATA.dbo.PDAUSERS for the username. Adapted
/// from an ad-hoc SQL report.
///
/// The original query staged rows into a PERSISTENT table
/// (tempdata..RFIDPDAProductionReport, TRUNCATE + INSERT + UPDATE) before the
/// final aggregation — unsafe for a multi-user web app (concurrent requests
/// would truncate each other's in-flight data), and tempdata doesn't even
/// exist on this app's on-prem connection. The staged columns beyond
/// TrnDate/UserId/Quantity (ShopName, TrfNo, and several blank placeholder
/// columns) are never read by the final SELECT, so this skips the staging
/// table entirely and aggregates directly in one query. Verified live: the
/// UserId -> PDAUSERS join has zero unmatched rows over a full week.
/// </summary>
public class RfidProductionService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 300;

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

    private const string ReportSql = @"
        SELECT TrnDate = t.TrnDate, Username = pu.UserName, Qty = SUM(t.Quantity)
          FROM BFLDATA.dbo.TransferNoReturn t
          LEFT JOIN BFLDATA.dbo.PDAUSERS pu ON pu.UserId = t.UserId
         WHERE t.Dept LIKE '%APDA%'
           AND t.TrnDate >= @fromDate AND t.TrnDate <= @toDate
           AND (@userFilter IS NULL OR pu.UserName = @userFilter)
         GROUP BY t.TrnDate, pu.UserName
         ORDER BY t.TrnDate DESC, pu.UserName;";

    public async Task<List<RfidProductionRow>> GetReportAsync(
        DateTime fromDate, DateTime toDate, string? userSearch, CancellationToken ct = default)
    {
        var userFilter = string.IsNullOrWhiteSpace(userSearch) ? null : userSearch.Trim();

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<RfidProductionRow>(new CommandDefinition(
            ReportSql, new { fromDate, toDate, userFilter },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
