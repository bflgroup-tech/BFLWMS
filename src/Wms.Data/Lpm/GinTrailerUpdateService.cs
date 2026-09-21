using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

public record GinTrailerUpdateResult(bool Ok, string? Error);

/// <summary>
/// GIN Trailer Update — a user picks a Trailer No. (dropdown only, sourced from
/// BFLDATA..GINTrailerLog, no manual entry) and types a GIN number. On submit, the
/// trailer is stamped onto that GIN's row as "{TrailerNo} - HH:mm" (UAE local time,
/// UTC+4) and the submission is logged to GINTrailerLog, in one transaction.
///
/// Currently points at bfldata.dbo.TEST_PLT (Trailorno column, keyed by Srno) rather
/// than a production GIN table — a deliberate choice while write permission and the
/// end-to-end flow are being verified on a safe table first, same table already used
/// to test UPDATE permission ad hoc. GINTrailerLog's exact column names (GIN,
/// TrailerNo, UpdatedBy, UpdatedTS below) are assumed to match this codebase's usual
/// naming convention — not confirmed against the live schema (no DB connection in
/// this session); a mismatch will surface as a clear SQL error naming the wrong column.
/// </summary>
public class GinTrailerUpdateService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 60;

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    /// <summary>Trailer No. dropdown options — every distinct trailer already logged in
    /// BFLDATA..GINTrailerLog. No free-text entry anywhere in the UI.</summary>
    public async Task<List<string>> GetTrailerNosAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT TrailerNo
              FROM BFLDATA.dbo.GINTrailerLog WITH (NOLOCK)
             WHERE TrailerNo IS NOT NULL AND TrailerNo <> ''
             ORDER BY TrailerNo",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<GinTrailerUpdateResult> UpdateTrailerAsync(
        string gin, string trailerNo, string username, CancellationToken ct = default)
    {
        gin = (gin ?? "").Trim();
        trailerNo = (trailerNo ?? "").Trim();
        if (gin.Length == 0) return new(false, "GIN is required.");
        if (trailerNo.Length == 0) return new(false, "Trailer No. is required.");
        if (!int.TryParse(gin, out var ginNo)) return new(false, "GIN must be numeric.");

        var nowGst = DateTime.UtcNow.AddHours(4);
        var stamped = $"{trailerNo} - {nowGst:HH:mm}";

        await using var c = OpenOnPremBackup();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            // UPDATE first: if the GIN doesn't exist, roll back before ever writing a
            // log row for a submission that didn't actually take effect.
            var updated = await c.ExecuteAsync(new CommandDefinition(@"
                UPDATE bfldata.dbo.TEST_PLT
                   SET Trailorno = @stamped
                 WHERE Srno = @ginNo",
                new { stamped, ginNo }, tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            if (updated == 0)
            {
                await tx.RollbackAsync(ct);
                return new(false, $"No row found for GIN {ginNo}.");
            }

            await c.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO BFLDATA.dbo.GINTrailerLog (GIN, TrailerNo, UpdatedBy, UpdatedTS)
                VALUES (@ginNo, @trailerNo, @username, @nowGst)",
                new { ginNo, trailerNo, username, nowGst }, tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return new(true, null);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new(false, ex.Message);
        }
    }
}
