using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Notifications;

/// <summary>Reads / writes the single-row Pending Goods Receipt email config.
/// The scheduled service polls GetAsync to know when to fire and calls
/// RecordRunAsync to stamp the outcome. The admin razor page uses SaveAsync
/// to persist edits.</summary>
public class PendingGoodsReceiptEmailService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 60;

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(resolver.GetWmsAzureConnectionString());
        c.Open();
        return c;
    }

    /// <summary>Load the (single) row. Returns null if the table hasn't been seeded yet.</summary>
    public async Task<PendingGoodsReceiptEmailConfig?> GetAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        return (await c.QueryAsync<PendingGoodsReceiptEmailConfig>(new CommandDefinition(@"
            SELECT TOP 1 Id, Recipients, IntervalHours, IsActive,
                         LastRunTS, LastRunStatus, LastSentCount,
                         UpdatedTS, UpdatedBy
              FROM dbo.WmsPendingGoodsReceiptEmailConfig WITH (NOLOCK)
             ORDER BY Id",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).FirstOrDefault();
    }

    /// <summary>Update Recipients / IntervalHours / IsActive on the single row. Preserves
    /// LastRun* history. Auto-creates the row if the seed migration hasn't been run.</summary>
    public async Task SaveAsync(string recipients, int intervalHours, bool isActive, string updatedBy, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var existing = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 Id FROM dbo.WmsPendingGoodsReceiptEmailConfig ORDER BY Id",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        if (existing is null)
        {
            await c.ExecuteAsync(new CommandDefinition(@"
                INSERT dbo.WmsPendingGoodsReceiptEmailConfig (Recipients, IntervalHours, IsActive, UpdatedBy)
                VALUES (@recipients, @intervalHours, @isActive, @updatedBy)",
                new { recipients, intervalHours, isActive, updatedBy },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }
        else
        {
            await c.ExecuteAsync(new CommandDefinition(@"
                UPDATE dbo.WmsPendingGoodsReceiptEmailConfig
                   SET Recipients    = @recipients,
                       IntervalHours = @intervalHours,
                       IsActive      = @isActive,
                       UpdatedTS     = DATEADD(hour, 4, SYSUTCDATETIME()),
                       UpdatedBy     = @updatedBy
                 WHERE Id = @id",
                new { id = existing.Value, recipients, intervalHours, isActive, updatedBy },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }
    }

    /// <summary>Stamp LastRunTS / Status / SentCount after each run attempt. Runs
    /// even on skip / error so the admin page shows what happened last time.</summary>
    public async Task RecordRunAsync(int id, string status, int? sentCount, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        await c.ExecuteAsync(new CommandDefinition(@"
            UPDATE dbo.WmsPendingGoodsReceiptEmailConfig
               SET LastRunTS     = DATEADD(hour, 4, SYSUTCDATETIME()),
                   LastRunStatus = @status,
                   LastSentCount = @sentCount
             WHERE Id = @id",
            new { id, status, sentCount },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }
}
