using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Notifications;

/// <summary>
/// Reads / writes the single-row RD/RS ageing email config on the Azure WMS DB.
/// The scheduled service polls GetAsync to know when to fire and calls
/// RecordRunAsync to stamp the outcome; the admin page uses SaveAsync.
///
/// Same shape as PendingGoodsReceiptEmailService — deliberately, so the two
/// scheduled mailers stay recognisable as the same thing.
/// </summary>
public class RdRsAgeingEmailService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 60;

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(resolver.GetWmsAzureConnectionString());
        c.Open();
        return c;
    }

    /// <summary>Load the single row. Null when the migration has not been run.</summary>
    public async Task<RdRsAgeingEmailConfig?> GetAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        return (await c.QueryAsync<RdRsAgeingEmailConfig>(new CommandDefinition(@"
            SELECT TOP 1 Id, Recipients, CcRecipients, [DayOfWeek], HourGst, MinuteGst, IsActive,
                         LastRunDate, LastRunTS, LastRunStatus, LastSentRows, LastSentQty,
                         UpdatedTS, UpdatedBy
              FROM dbo.WmsRdRsAgeingEmailConfig WITH (NOLOCK)
             ORDER BY Id",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).FirstOrDefault();
    }

    /// <summary>
    /// Persist the editable fields. Preserves LastRun* history, and creates the
    /// row if the seed migration has not been run.
    /// </summary>
    public async Task SaveAsync(
        string recipients, string? ccRecipients, byte dayOfWeek, byte hourGst, byte minuteGst,
        bool isActive, string updatedBy, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var existing = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 Id FROM dbo.WmsRdRsAgeingEmailConfig ORDER BY Id",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        if (existing is null)
        {
            await c.ExecuteAsync(new CommandDefinition(@"
                INSERT dbo.WmsRdRsAgeingEmailConfig
                    (Recipients, CcRecipients, [DayOfWeek], HourGst, MinuteGst, IsActive, UpdatedBy)
                VALUES (@recipients, @ccRecipients, @dayOfWeek, @hourGst, @minuteGst, @isActive, @updatedBy)",
                new { recipients, ccRecipients, dayOfWeek, hourGst, minuteGst, isActive, updatedBy },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }
        else
        {
            await c.ExecuteAsync(new CommandDefinition(@"
                UPDATE dbo.WmsRdRsAgeingEmailConfig
                   SET Recipients   = @recipients,
                       CcRecipients = @ccRecipients,
                       [DayOfWeek]  = @dayOfWeek,
                       HourGst      = @hourGst,
                       MinuteGst    = @minuteGst,
                       IsActive     = @isActive,
                       UpdatedTS    = DATEADD(hour, 4, SYSUTCDATETIME()),
                       UpdatedBy    = @updatedBy
                 WHERE Id = @id",
                new { id = existing.Value, recipients, ccRecipients, dayOfWeek, hourGst, minuteGst, isActive, updatedBy },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }
    }

    /// <summary>
    /// Stamp the outcome of a run attempt. LastRunDate is set even on skip and on
    /// error, which is what stops the every-minute poller retrying a failing send
    /// sixty times an hour — one attempt per configured day, then wait.
    /// </summary>
    public async Task RecordRunAsync(
        int id, DateTime runDateGst, string status, int? rows, long? qty, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        await c.ExecuteAsync(new CommandDefinition(@"
            UPDATE dbo.WmsRdRsAgeingEmailConfig
               SET LastRunDate   = @runDateGst,
                   LastRunTS     = DATEADD(hour, 4, SYSUTCDATETIME()),
                   LastRunStatus = @status,
                   LastSentRows  = @rows,
                   LastSentQty   = @qty
             WHERE Id = @id",
            new { id, runDateGst = runDateGst.Date, status, rows, qty },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }
}
