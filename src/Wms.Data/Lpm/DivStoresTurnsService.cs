using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>One row as it exists in the table.</summary>
public record DivStoresTurnRow(
    string    Country,
    string    StoreID,
    int       DivCode,
    decimal   Turns,
    int?      SoldQty,
    int?      SohQty,
    int?      Year1,
    int?      Month1,
    DateTime  UpdatedTS,
    string?   UpdatedBy);

/// <summary>One parsed Excel row, before it is written.</summary>
public record DivStoresTurnUploadRow(
    string   Country,
    string   StoreID,
    int      DivCode,
    decimal  Turns,
    int?     SoldQty,
    int?     SohQty);

public record DivStoresTurnsSaveResult(bool Ok, string? Error, int RowsSaved, List<string> Warnings);

/// <summary>
/// Backs the "Div Stores Turns Upload" page — the per-store, per-division stock
/// turn that Flagged Allocation (Pass 5) uses to decide which stores are worth
/// topping up. Reads and writes LPMSIM.dbo.LPM_DivStoresTurns.
///
/// The table is keyed (Country, StoreID, DivCode) with no period in the key, so
/// an upload REPLACES a store/division's turn rather than accumulating. Year1 and
/// Month1 ride along as plain columns recording which period the figure came from.
///
/// Country is upper-cased on the way in. Pass 5 upper-cases both sides of its
/// lookup, but storing a consistent case keeps this table legible next to
/// WmsOtsPoAllocationRun, and stops 'Uae' and 'UAE' becoming two separate rows for
/// one store — which the primary key would happily allow under a case-insensitive
/// collation only by chance.
/// </summary>
public class DivStoresTurnsService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 120;

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    private static DateTime NowGst() => DateTime.UtcNow.AddHours(4);

    /// <summary>Countries already present in the table, for the view/delete filter.</summary>
    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT Country
              FROM LPMSIM.dbo.LPM_DivStoresTurns WITH (NOLOCK)
             WHERE Country IS NOT NULL AND LTRIM(RTRIM(Country)) <> ''
             ORDER BY Country",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<DivStoresTurnRow>> GetRowsAsync(string? country, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<DivStoresTurnRow>(new CommandDefinition(@"
            SELECT Country, StoreID, DivCode, Turns, SoldQty, SohQty, Year1, Month1, UpdatedTS, UpdatedBy
              FROM LPMSIM.dbo.LPM_DivStoresTurns WITH (NOLOCK)
             WHERE (@country IS NULL OR Country = @country)
             ORDER BY Country, DivCode, Turns DESC",
            new { country = string.IsNullOrWhiteSpace(country) ? null : country },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<int> GetRowCountAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM LPMSIM.dbo.LPM_DivStoresTurns WITH (NOLOCK)",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    /// <summary>
    /// Upserts every uploaded row: an existing (Country, StoreID, DivCode) is
    /// updated in place, a new one inserted. Whole thing runs in one transaction,
    /// so a file that fails halfway leaves the table as it was rather than
    /// half-loaded — a partially-loaded turns table would silently change which
    /// stores clear the average in Pass 5.
    ///
    /// Rows naming a (StoreID, DivCode) that has no row in the current OTS run are
    /// still saved, but reported: they are most likely a typo or a stale store
    /// code, and they would sit in the table doing nothing.
    /// </summary>
    public async Task<DivStoresTurnsSaveResult> SaveAsync(
        List<DivStoresTurnUploadRow> rows, string updatedBy, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        if (rows.Count == 0) return new DivStoresTurnsSaveResult(false, "Nothing to save.", 0, warnings);

        var nowGst = NowGst();
        await using var c = OpenOnPremBackup();

        // Flag store/division pairs the allocation engine will never ask about, so a
        // mistyped store code is visible now rather than as "no store above average
        // turns" three screens later.
        try
        {
            var known = (await c.QueryAsync<(string StoreID, int DivCode)>(new CommandDefinition(@"
                SELECT DISTINCT StoreID, DivCode
                  FROM LPMSIM.dbo.WmsOtsPoAllocationRun WITH (NOLOCK)
                 WHERE [Month] = @m AND [Year] = @y",
                new { m = nowGst.Month, y = nowGst.Year },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .Select(r => (r.StoreID.Trim().ToUpperInvariant(), r.DivCode))
                .ToHashSet();

            if (known.Count > 0)
            {
                var unknown = rows
                    .Where(r => !known.Contains((r.StoreID.Trim().ToUpperInvariant(), r.DivCode)))
                    .Select(r => $"{r.StoreID}/{r.DivCode}")
                    .Distinct()
                    .Take(10)
                    .ToList();
                if (unknown.Count > 0)
                    warnings.Add(
                        $"{unknown.Count}{(unknown.Count == 10 ? "+" : "")} store/division pair(s) are not in " +
                        $"this month's OTS run and will never be used by allocation: {string.Join(", ", unknown)}. " +
                        "Saved anyway — check the store codes and division codes if that is unexpected.");
            }
        }
        catch
        {
            // Diagnostic only. A missing/renamed OTS run must not block a turns upload.
        }

        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            var saved = 0;
            foreach (var r in rows)
            {
                saved += await c.ExecuteAsync(new CommandDefinition(@"
                    UPDATE dbo.LPM_DivStoresTurns
                       SET Turns = @turns, SoldQty = @sold, SohQty = @soh,
                           Year1 = @y, Month1 = @m, UpdatedTS = @ts, UpdatedBy = @by
                     WHERE Country = @country AND StoreID = @store AND DivCode = @div;

                    IF @@ROWCOUNT = 0
                        INSERT INTO dbo.LPM_DivStoresTurns
                            (Country, StoreID, DivCode, Turns, SoldQty, SohQty, Year1, Month1, UpdatedTS, UpdatedBy)
                        VALUES (@country, @store, @div, @turns, @sold, @soh, @y, @m, @ts, @by);",
                    new
                    {
                        country = r.Country.Trim().ToUpperInvariant(),
                        store   = r.StoreID.Trim(),
                        div     = r.DivCode,
                        turns   = r.Turns,
                        sold    = r.SoldQty,
                        soh     = r.SohQty,
                        y       = nowGst.Year,
                        m       = nowGst.Month,
                        ts      = nowGst,
                        by      = updatedBy,
                    },
                    transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            }

            await tx.CommitAsync(ct);
            return new DivStoresTurnsSaveResult(true, null, rows.Count, warnings);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new DivStoresTurnsSaveResult(false, ex.Message, 0, warnings);
        }
    }

    /// <summary>Clears one country's rows, or the whole table when country is null/blank.</summary>
    public async Task<int> DeleteAsync(string? country, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        return await c.ExecuteAsync(new CommandDefinition(@"
            DELETE FROM dbo.LPM_DivStoresTurns
             WHERE (@country IS NULL OR Country = @country)",
            new { country = string.IsNullOrWhiteSpace(country) ? null : country },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }
}
