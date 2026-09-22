using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

public record GinTrailerUpdateResult(bool Ok, string? Error);

public record GinTrailerRow(int GinNo, DateTime? EntryDate, string? TrailerNo, string? Remarks, string? WarehouseFrom, string? WarehouseTo);

public record GinTrailerLoadResult(List<GinTrailerRow> Valid, List<int> NotFound, List<int> TooOld);

public record GinTrailerLogEntry(int GinNo, string? OldTrailerNo, string? NewTrailerNo, string? UpdatedUser, DateTime? UpdatedTS);

/// <summary>
/// GIN Trailer Update — a user enters one or more GINs (comma-separated) and picks a
/// Trailer No. (dropdown only, sourced from BFLDATA..WHTrailers, no manual entry).
/// Loading the GINs shows each one's current EntryDate/TrailerNo/Remarks/WarehouseFrom/
/// WarehouseTo from bfldata.dbo.PLTDeliveryHead (keyed by SRNo) and splits them into "too old"
/// (EntryDate more than 2 days in the past — not allowed) and valid rows. On submit, the
/// trailer is stamped onto every valid GIN's TrailerNo column as "{TrailerNo}-HH:mm"
/// (UAE local time, UTC+4), and GINTrailerLog gets one audit row per GIN recording the
/// old and new Trailer No., who made the change and when — all in one transaction.
/// Rejected if the chosen Trailer No. was already used (any GIN) earlier the same day.
///
/// GINTrailerLog columns: SRNo (the GIN), OldTrailerNo, NewTrailerNo, UpdatedUser (the
/// updating user's email), UpdatedTS.
///
/// FromWarehouse-based restriction (only a user from the GIN's own warehouse may
/// update it) is not yet implemented — pending confirmation of how a GIN's warehouse
/// should be compared to the user's own warehouse.
/// </summary>
public class GinTrailerUpdateService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 60;
    private const int MaxEntryAgeDays = 2;

    private SqlConnection OpenConnection()
    {
        // WmsProductionDb, not OnPremBackupDB — the latter's login was denied UPDATE
        // on BFLDATA.dbo.TEST_PLT (the placeholder table this feature was verified
        // against before switching to the real bfldata.dbo.PLTDeliveryHead) even after
        // a GRANT was applied. WmsProductionDb isn't configured in local dev secrets
        // (add ConnectionStrings:WmsProductionDb to user-secrets to test locally), but
        // is already used in production by several other BFLDATA-writing features
        // (GenerateEan13Service, JafzaExportCheckingService,
        // ContainerAllocationDataSyncService, TechnoBuildingService).
        var c = new SqlConnection(resolver.GetWmsProductionDbConnectionString());
        c.Open();
        return c;
    }

    /// <summary>Trailer No. dropdown options — every trailer plate on record in
    /// BFLDATA..WHTrailers. No free-text entry anywhere in the UI.</summary>
    public async Task<List<string>> GetTrailerNosAsync(CancellationToken ct = default)
    {
        await using var c = OpenConnection();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT TrailerNo = PlateNo
              FROM BFLDATA.dbo.WHTrailers WITH (NOLOCK)
             WHERE PlateNo IS NOT NULL AND PlateNo <> ''
             ORDER BY PlateNo",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Loads the given GINs from bfldata.dbo.PLTDeliveryHead and splits them into: not
    /// found, too old (EntryDate more than <see cref="MaxEntryAgeDays"/> days in the
    /// past), and valid.</summary>
    public async Task<GinTrailerLoadResult> LoadGinsAsync(IEnumerable<int> ginNos, CancellationToken ct = default)
    {
        var wanted = ginNos.Distinct().ToList();
        if (wanted.Count == 0) return new(new(), new(), new());

        await using var c = OpenConnection();
        var rows = (await c.QueryAsync<GinTrailerRow>(new CommandDefinition(@"
            SELECT GinNo = SRNo, EntryDate, TrailerNo, Remarks, WarehouseFrom, WarehouseTo
              FROM bfldata.dbo.PLTDeliveryHead WITH (NOLOCK)
             WHERE SRNo IN @wanted",
            new { wanted }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        var today = DateTime.UtcNow.AddHours(4).Date;
        var foundGins = rows.Select(r => r.GinNo).ToHashSet();
        var notFound = wanted.Where(g => !foundGins.Contains(g)).ToList();
        var tooOldGins = rows
            .Where(r => r.EntryDate is null || r.EntryDate.Value.Date < today.AddDays(-MaxEntryAgeDays))
            .Select(r => r.GinNo).ToHashSet();
        var valid = rows.Where(r => !tooOldGins.Contains(r.GinNo)).ToList();

        return new(valid, notFound, tooOldGins.ToList());
    }

    /// <summary>Past trailer updates for the given GINs from BFLDATA..GINTrailerLog,
    /// newest first.</summary>
    public async Task<List<GinTrailerLogEntry>> GetLogHistoryAsync(IEnumerable<int> ginNos, CancellationToken ct = default)
    {
        var wanted = ginNos.Distinct().ToList();
        if (wanted.Count == 0) return new();

        await using var c = OpenConnection();
        var rows = await c.QueryAsync<GinTrailerLogEntry>(new CommandDefinition(@"
            SELECT GinNo = SRNo, OldTrailerNo, NewTrailerNo, UpdatedUser, UpdatedTS
              FROM BFLDATA.dbo.GINTrailerLog WITH (NOLOCK)
             WHERE SRNo IN @wanted
             ORDER BY UpdatedTS DESC",
            new { wanted }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Stamps the chosen Trailer No. onto every given GIN's TrailerNo column and
    /// logs one GINTrailerLog row per GIN, all in one transaction. The whole batch is
    /// rejected if the Trailer No. was already used (any GIN) earlier the same day.</summary>
    public async Task<GinTrailerUpdateResult> UpdateManyAsync(
        IEnumerable<int> ginNos, string trailerNo, string username, CancellationToken ct = default)
    {
        var gins = ginNos.Distinct().ToList();
        trailerNo = (trailerNo ?? "").Trim();
        if (gins.Count == 0) return new(false, "No GINs to update.");
        if (trailerNo.Length == 0) return new(false, "Trailer No. is required.");

        var nowGst = DateTime.UtcNow.AddHours(4);
        var stamped = $"{trailerNo}-{nowGst:HH:mm}";
        var today = nowGst.Date;

        await using var c = OpenConnection();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
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

            foreach (var ginNo in gins)
            {
                var oldTrailerNo = await c.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(@"
                    SELECT TrailerNo FROM bfldata.dbo.PLTDeliveryHead WHERE SRNo = @ginNo",
                    new { ginNo }, tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

                var updated = await c.ExecuteAsync(new CommandDefinition(@"
                    UPDATE bfldata.dbo.PLTDeliveryHead
                       SET TrailerNo = @stamped
                     WHERE SRNo = @ginNo",
                    new { stamped, ginNo }, tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

                if (updated == 0)
                {
                    await tx.RollbackAsync(ct);
                    return new(false, $"No row found for GIN {ginNo}.");
                }

                await c.ExecuteAsync(new CommandDefinition(@"
                    INSERT INTO BFLDATA.dbo.GINTrailerLog (SRNo, OldTrailerNo, NewTrailerNo, UpdatedUser, UpdatedTS)
                    VALUES (@ginNo, @oldTrailerNo, @stamped, @username, @nowGst)",
                    new { ginNo, oldTrailerNo, stamped, username, nowGst }, tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            }

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
