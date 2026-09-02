using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Backs the "Ecom Production Reports" page (menu key RPT_ECOM_PRODUCTION_REPORTS).
/// Manages LPMSIM.dbo.UserWHDetail rows for Warehouse = 'Online' -- add or reactivate
/// (Empcode is the natural key: a person can't have two rows for this warehouse), and
/// deactivate (Active is a real column on this table already, so "remove" is a soft
/// deactivate, not a DELETE -- keeps history and is easily reversible). Also reports
/// YOTO box-prep production from USA.dbo.VUPCBOXDET for whichever of those users are
/// currently active, matched on either Empcode or UserName since VUPCBOXDET.PreparedBy
/// has been observed holding either, depending on how the scanning device logged in.
/// Reached through the same OnPremBackup connection the other ported LPMSIM/USA
/// reports use.
/// </summary>
public class EcomProductionReportsService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 60;
    public const string Warehouse = "Online";

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    public async Task<List<OnlineWhUserRow>> GetUsersAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<OnlineWhUserRow>(new CommandDefinition(@"
            SELECT Empcode, UserName, FullName, Active, AddedUser, CreateTS AS CreateTs
              FROM LPMSIM.dbo.UserWHDetail WITH (NOLOCK)
             WHERE Warehouse = @wh
             ORDER BY Active DESC, FullName",
            new { wh = Warehouse }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Adds a new Online user, or reactivates/updates an existing (Empcode) row.</summary>
    public async Task<(bool Ok, string? Error)> AddOrReactivateUserAsync(
        string empcode, string userName, string fullName, string addedUser, CancellationToken ct = default)
    {
        empcode  = (empcode  ?? "").Trim();
        userName = (userName ?? "").Trim();
        fullName = (fullName ?? "").Trim();
        if (string.IsNullOrEmpty(empcode))  return (false, "Emp Code is required.");
        if (string.IsNullOrEmpty(userName)) return (false, "Username is required.");
        if (string.IsNullOrEmpty(fullName)) return (false, "Employee Name is required.");

        await using var c = OpenOnPremBackup();
        var existing = await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM LPMSIM.dbo.UserWHDetail WHERE Warehouse = @wh AND Empcode = @empcode",
            new { wh = Warehouse, empcode }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        var nowGst = DateTime.UtcNow.AddHours(4);
        if (existing > 0)
        {
            await c.ExecuteAsync(new CommandDefinition(@"
                UPDATE LPMSIM.dbo.UserWHDetail
                   SET UserName = @userName, FullName = @fullName, Active = 1, AddedUser = @addedUser, CreateTS = @nowGst
                 WHERE Warehouse = @wh AND Empcode = @empcode",
                new { wh = Warehouse, empcode, userName, fullName, addedUser, nowGst },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }
        else
        {
            await c.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO LPMSIM.dbo.UserWHDetail (Empcode, UserName, FullName, Warehouse, Active, AddedUser, CreateTS)
                VALUES (@empcode, @userName, @fullName, @wh, 1, @addedUser, @nowGst)",
                new { empcode, userName, fullName, wh = Warehouse, addedUser, nowGst },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }
        return (true, null);
    }

    public async Task SetActiveAsync(string empcode, bool active, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE LPMSIM.dbo.UserWHDetail SET Active = @active WHERE Warehouse = @wh AND Empcode = @empcode",
            new { wh = Warehouse, empcode, active }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    /// <summary>YOTO box-prep production for currently-active Online WH users, one row per
    /// (TrnDate, Empcode) -- BoxCount is DISTINCT BoxNo, Qty is the summed line quantity.</summary>
    public async Task<List<EcomYotoProductionRow>> GetYotoProductionAsync(DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<EcomYotoProductionRow>(new CommandDefinition(@"
            ;WITH OnlineUsers AS (
                SELECT Empcode, UserName, FullName
                  FROM LPMSIM.dbo.UserWHDetail WITH (NOLOCK)
                 WHERE Warehouse = @wh AND Active = 1
            )
            SELECT
                TrnDate  = d.TrnDate,
                Empcode  = u.Empcode,
                UserName = u.UserName,
                FullName = u.FullName,
                BoxCount = CAST(COUNT(DISTINCT d.BoxNo) AS BIGINT),
                Qty      = CAST(ISNULL(SUM(d.Qty), 0) AS BIGINT)
              FROM USA.dbo.VUPCBOXDET d WITH (NOLOCK)
              JOIN OnlineUsers u ON d.PreparedBy = u.Empcode OR d.PreparedBy = u.UserName
             WHERE d.WHouse = 'YOTO' AND d.TrnDate BETWEEN @fromDt AND @toDt
             GROUP BY d.TrnDate, u.Empcode, u.UserName, u.FullName
             ORDER BY d.TrnDate DESC, u.FullName",
            new { wh = Warehouse, fromDt = fromDt.Date, toDt = toDt.Date.AddDays(1).AddSeconds(-1) },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
