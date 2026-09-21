using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

public record GinTrailerUpdateResult(bool Ok, string? Error);

file record GinRow(string? Trailorno, DateTime? EntryDate);

/// <summary>
/// GIN Trailer Update — a user picks a Trailer No. (dropdown only, sourced from
/// BFLDATA..WHTrailers, no manual entry) and types a GIN number. On submit, the
/// trailer is stamped onto that GIN's row as "{TrailerNo}-HH:mm" (UAE local time,
/// UTC+4), and GINTrailerLog gets an audit row recording the GIN, the old and new
/// Trailer No. values, who made the change and when — all in one transaction.
/// Rejected if the GIN's EntryDate is more than 2 days old, or if the chosen
/// Trailer No. was already used (any GIN) earlier the same day.
///
/// Currently points at bfldata.dbo.TEST_PLT (Trailorno/EntryDate columns, keyed by
/// Srno) rather than a production GIN table — a deliberate choice while write
/// permission and the end-to-end flow are being verified on a safe table first, same
/// table already used to test UPDATE permission ad hoc. GINTrailerLog's exact column
/// names (GIN, OldTrailerNo, NewTrailerNo, UpdatedBy, UpdatedTS below) are assumed to
/// match this codebase's usual naming convention — not confirmed against the live
/// schema (no DB connection in this session); a mismatch will surface as a clear SQL
/// error naming the wrong column.
///
/// FromWarehouse-based restriction (only a user from the GIN's own warehouse may
/// update it) is not yet implemented — pending confirmation of the GIN table's
/// FromWarehouse column and how it should be compared to the user's own warehouse.
/// </summary>
public class GinTrailerUpdateService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 60;
    private const int MaxEntryAgeDays = 2;

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    /// <summary>Trailer No. dropdown options — every trailer plate on record in
    /// BFLDATA..WHTrailers. No free-text entry anywhere in the UI.</summary>
    public async Task<List<string>> GetTrailerNosAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT TrailerNo = PlateNo
              FROM BFLDATA.dbo.WHTrailers WITH (NOLOCK)
             WHERE PlateNo IS NOT NULL AND PlateNo <> ''
             ORDER BY PlateNo",
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
        var stamped = $"{trailerNo}-{nowGst:HH:mm}";

        await using var c = OpenOnPremBackup();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            var ginRow = await c.QuerySingleOrDefaultAsync<GinRow?>(new CommandDefinition(@"
                SELECT Trailorno, EntryDate FROM bfldata.dbo.TEST_PLT WHERE Srno = @ginNo",
                new { ginNo }, tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            if (ginRow is null)
            {
                await tx.RollbackAsync(ct);
                return new(false, $"No row found for GIN {ginNo}.");
            }

            var today = nowGst.Date;
            if (ginRow.EntryDate is null || ginRow.EntryDate.Value.Date < today.AddDays(-MaxEntryAgeDays))
            {
                await tx.RollbackAsync(ct);
                return new(false, $"GIN {ginNo} is more than {MaxEntryAgeDays} days old and can no longer be updated.");
            }

            // Trailer No. must not repeat within the same day — check every log entry
            // written today, since NewTrailerNo is always "{TrailerNo}-HH:mm".
            var todaysLogs = await c.QueryAsync<string?>(new CommandDefinition(@"
                SELECT NewTrailerNo FROM BFLDATA.dbo.GINTrailerLog WITH (NOLOCK)
                 WHERE CAST(UpdatedTS AS date) = CAST(@today AS date)",
                new { today }, tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            if (todaysLogs.Any(t => t is not null && t.StartsWith(trailerNo + "-", StringComparison.OrdinalIgnoreCase)))
            {
                await tx.RollbackAsync(ct);
                return new(false, $"Trailer {trailerNo} has already been used today.");
            }

            await c.ExecuteAsync(new CommandDefinition(@"
                UPDATE bfldata.dbo.TEST_PLT
                   SET Trailorno = @stamped
                 WHERE Srno = @ginNo",
                new { stamped, ginNo }, tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            await c.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO BFLDATA.dbo.GINTrailerLog (GIN, OldTrailerNo, NewTrailerNo, UpdatedBy, UpdatedTS)
                VALUES (@ginNo, @oldTrailerNo, @stamped, @username, @nowGst)",
                new { ginNo, oldTrailerNo = ginRow.Trailorno, stamped, username, nowGst }, tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

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
