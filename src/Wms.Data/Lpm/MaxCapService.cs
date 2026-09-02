using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Backs the "Max Cap Upload" page (menu key MAX_CAP_UPLOAD) — per-Warehouse,
/// per-Division min/max WH capacity, uploaded via Excel. Reads/writes the
/// existing LPMSIM.dbo.WMS_WH_MAXMIN_CAP table directly (not created by this
/// app), reached through the same OnPremBackup connection ReportsService
/// uses for LPMSIM/BFLDATA. The Excel template carries no Week/WeekId/Month/
/// Year/CreatedUser columns, so those are stamped at save time from the
/// caller-chosen (Year, Month) and the logged-in user's identity —
/// WeekId/Week come from LPMSIM.dbo.LPM_OTS_Output.Wk (the same fiscal week
/// numbering the rest of the app uses), not derived independently. For the
/// current (or a future) month, saving force-deletes
/// any existing row for that exact (Country, Warehouse, Division, Month, Year)
/// before inserting, so re-uploading for the same period replaces rather
/// than duplicates -- keyed on Month/Year, not WeekId, since WeekId is only a
/// "which fiscal week happened to be latest when this was saved" label:
/// GetFiscalWeekAsync's answer for a given month drifts forward as the OTS
/// feed loads more of that month's weeks, so two uploads for the same
/// calendar month can land under two different WeekIds. For a month before
/// the current one, that period is frozen instead: a Warehouse/Division combo
/// that already has a row for that Month/Year is rejected outright (SaveAsync
/// returns Ok=false), though a combo never recorded for that past period can
/// still be added. CopyMonthAsync (used by the "copy this month's rows to
/// another month" UI action) follows the exact same Month/Year-keyed
/// replace-and-freeze rules. Clearing a whole country's or warehouse's rows
/// outright is a separate, explicit action (DeleteAllForCountryAsync /
/// DeleteForWarehouseAsync).
///
/// Valid Warehouse values are restricted per country: for UAE, only
/// 'TECHNO' or 'JAFZA'; for every other (export) country, whichever
/// BFLDATA.dbo.DataSettings.ShopName rows have ExportActive='Y' and
/// ExportWH='Y' for that country. A row naming any other Warehouse is
/// rejected before it reaches the table.
/// </summary>
public class MaxCapService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 60;
    private static readonly string[] UaeWarehouses = ["TECHNO", "JAFZA"];

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    /// <summary>Fiscal week for a given (Year, Month) — read from LPMSIM.dbo.LPM_OTS_Output.Wk (the same
    /// fiscal week numbering the rest of the app uses), not derived independently: takes the Wk of that
    /// month's most recent OTSDate. Filters by MONTH(OTSDate), NOT the stored month1 column — month1
    /// has confirmed bad boundary rows (e.g. an OTSDate of 2026-07-31 stored with month1=9), which would
    /// otherwise leak a stale prior-month week (30, from July) into a month that has no real data yet
    /// (September). Returns null if that (Year, Month) has no OTS data yet (e.g. a future month).</summary>
    public async Task<(int Week, string WeekId)?> GetFiscalWeekAsync(int year, int month, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var week = await c.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(@"
            SELECT TOP (1) Wk
              FROM LPMSIM.dbo.LPM_OTS_Output WITH (NOLOCK)
             WHERE year1 = @year AND MONTH(OTSDate) = @month AND Wk IS NOT NULL
             ORDER BY OTSDate DESC",
            new { year, month }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return week is null ? null : (week.Value, $"{year}-W{week.Value}");
    }

    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT Country
              FROM bfldata.dbo.DataSettings WITH (NOLOCK)
             WHERE Country IS NOT NULL AND LTRIM(RTRIM(Country)) <> ''
               AND Country NOT LIKE '%MALTA%'
               AND Country NOT LIKE '%ECOM%'
               AND Country NOT LIKE '%EX2%'
               AND Country NOT LIKE '%SINGAPORE%'
             ORDER BY Country",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>UAE is a fixed pair (TECHNO/JAFZA); every other country comes from DataSettings' export-warehouse flags.</summary>
    public async Task<List<string>> GetValidWarehousesAsync(string country, CancellationToken ct = default)
    {
        if (string.Equals(country, "UAE", StringComparison.OrdinalIgnoreCase))
            return UaeWarehouses.ToList();

        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT ShopName
              FROM BFLDATA.dbo.DataSettings WITH (NOLOCK)
             WHERE ExportActive = 'Y' AND ExportWH = 'Y' AND Country = @country",
            new { country }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<WhMaxMinCapRow>> GetRowsAsync(string country, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(country)) return new();
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<WhMaxMinCapRow>(new CommandDefinition(@"
            SELECT CREATETS AS CreateTs, WEEKID AS WeekId, COUNTRY AS Country, WAREHOUSE AS Warehouse,
                   DIVISION AS Division, MAX_CAP_WEEK AS MaxCapWeek, MIN_CAP_WEEK AS MinCapWeek,
                   WEEK AS Week, MONTH AS Month, YEAR AS Year, CREATEDUSER AS CreatedUser
              FROM LPMSIM.dbo.WMS_WH_MAXMIN_CAP WITH (NOLOCK)
             WHERE COUNTRY = @country
             ORDER BY WAREHOUSE, DIVISION",
            new { country }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Force-deletes any existing row for (Country, Warehouse, Division, WeekId) — that exact
    /// warehouse/division within the chosen month's fiscal week — then inserts the fresh upload rows,
    /// so re-uploading for the same period always replaces rather than duplicates.</summary>
    public async Task<MaxCapSaveResult> SaveAsync(
        string country, List<WhMaxMinCapUploadRow> rows, int year, int month, string createdUser, CancellationToken ct = default)
    {
        country = (country ?? "").Trim();
        if (string.IsNullOrEmpty(country)) return new(false, "Country is required.", 0);
        if (rows.Count == 0)               return new(false, "No rows to save.", 0);

        var fiscalWeek = await GetFiscalWeekAsync(year, month, ct);
        if (fiscalWeek is null)
            return new(false, $"No data found for {year}-{month:D2} yet — pick a different month.", 0);
        var (week, weekId) = fiscalWeek.Value;
        var nowGst = DateTime.UtcNow.AddHours(4);
        var isPastMonth = year < nowGst.Year || (year == nowGst.Year && month < nowGst.Month);

        await using var c = OpenOnPremBackup();

        // Previous months are frozen once data exists: uploading can add a Warehouse/Division
        // combo that was never recorded for that period, but can't touch one that already has a
        // row — the current month (and any future month) can still be freely replaced above.
        if (isPastMonth)
        {
            var existingKeys = (await c.QueryAsync<(string Warehouse, string Division)>(new CommandDefinition(
                "SELECT WAREHOUSE, DIVISION FROM LPMSIM.dbo.WMS_WH_MAXMIN_CAP WHERE COUNTRY = @country AND MONTH = @month AND YEAR = @year",
                new { country, month, year }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .Select(k => (k.Warehouse.Trim().ToUpperInvariant(), k.Division.Trim().ToUpperInvariant()))
                .ToHashSet();
            var conflicts = rows
                .Where(r => existingKeys.Contains((r.Warehouse.Trim().ToUpperInvariant(), r.Division.Trim().ToUpperInvariant())))
                .Select(r => $"{r.Warehouse}/{r.Division}")
                .ToList();
            if (conflicts.Count > 0)
            {
                var monthName = System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month);
                return new(false,
                    $"Already exists for {monthName} {year} (a previous month) — can't change now: {string.Join(", ", conflicts)}.", 0);
            }
        }

        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            foreach (var r in rows)
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM LPMSIM.dbo.WMS_WH_MAXMIN_CAP WHERE COUNTRY = @country AND WAREHOUSE = @warehouse AND DIVISION = @division AND MONTH = @month AND YEAR = @year",
                    new { country, warehouse = r.Warehouse, division = r.Division, month, year },
                    transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

                await c.ExecuteAsync(new CommandDefinition(@"
                    INSERT INTO LPMSIM.dbo.WMS_WH_MAXMIN_CAP (CREATETS, WEEKID, COUNTRY, WAREHOUSE, DIVISION, MAX_CAP_WEEK, MIN_CAP_WEEK, WEEK, MONTH, YEAR, CREATEDUSER)
                    VALUES (@nowGst, @weekId, @country, @warehouse, @division, @maxCapWeek, @minCapWeek, @week, @month, @year, @createdUser);",
                    new
                    {
                        country, warehouse = r.Warehouse, division = r.Division, weekId,
                        minCapWeek = r.MinCapWeek, maxCapWeek = r.MaxCapWeek, nowGst, week, month, year, createdUser
                    },
                    transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            }

            await tx.CommitAsync(ct);
            return new(true, null, rows.Count);
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            return new(false, $"Save failed: {ex.Message}", 0);
        }
    }

    /// <summary>Manual single-row add — force-deletes any existing row for (Country, Warehouse, Division,
    /// WeekId) for the chosen month's fiscal week, then inserts, so it replaces rather than duplicates.</summary>
    public async Task<MaxCapSaveResult> AddRowAsync(
        string country, string warehouse, string division, double minCapWeek, double maxCapWeek,
        int year, int month, string createdUser, CancellationToken ct = default)
    {
        country   = (country   ?? "").Trim();
        warehouse = (warehouse ?? "").Trim();
        division  = (division  ?? "").Trim();
        if (string.IsNullOrEmpty(country))   return new(false, "Country is required.", 0);
        if (string.IsNullOrEmpty(warehouse)) return new(false, "Warehouse is required.", 0);
        if (string.IsNullOrEmpty(division))  return new(false, "Division is required.", 0);

        var fiscalWeek = await GetFiscalWeekAsync(year, month, ct);
        if (fiscalWeek is null)
            return new(false, $"No data found for {year}-{month:D2} yet — pick a different month.", 0);
        var (week, weekId) = fiscalWeek.Value;
        var nowGst = DateTime.UtcNow.AddHours(4);

        await using var c = OpenOnPremBackup();
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LPMSIM.dbo.WMS_WH_MAXMIN_CAP WHERE COUNTRY = @country AND WAREHOUSE = @warehouse AND DIVISION = @division AND MONTH = @month AND YEAR = @year",
            new { country, warehouse, division, month, year }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        await c.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO LPMSIM.dbo.WMS_WH_MAXMIN_CAP (CREATETS, WEEKID, COUNTRY, WAREHOUSE, DIVISION, MAX_CAP_WEEK, MIN_CAP_WEEK, WEEK, MONTH, YEAR, CREATEDUSER)
            VALUES (@nowGst, @weekId, @country, @warehouse, @division, @maxCapWeek, @minCapWeek, @week, @month, @year, @createdUser);",
            new { country, warehouse, division, minCapWeek, maxCapWeek, nowGst, weekId, week, month, year, createdUser },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return new(true, null, 1);
    }

    /// <summary>Copies every row for (Country[, Warehouse]) from one month to another, re-stamped
    /// under the target month's own fiscal Week/WeekId — so, e.g., "copy August to September" makes
    /// September read exactly like August until someone uploads a real file for September (which
    /// replaces these copied rows the same way any upload replaces an existing month, since both key
    /// off Month/Year, not WeekId). Same past-month-frozen guard as SaveAsync: copying into a month
    /// already in the past can't overwrite a Warehouse/Division combo that already has data there.</summary>
    public async Task<MaxCapSaveResult> CopyMonthAsync(
        string country, string? warehouse, int fromYear, int fromMonth, int toYear, int toMonth, string createdUser, CancellationToken ct = default)
    {
        country = (country ?? "").Trim();
        if (string.IsNullOrEmpty(country)) return new(false, "Country is required.", 0);
        if (fromYear == toYear && fromMonth == toMonth) return new(false, "Source and target month must be different.", 0);

        await using var c = OpenOnPremBackup();

        var sourceRows = (await c.QueryAsync<WhMaxMinCapRow>(new CommandDefinition(@"
            SELECT CREATETS AS CreateTs, WEEKID AS WeekId, COUNTRY AS Country, WAREHOUSE AS Warehouse,
                   DIVISION AS Division, MAX_CAP_WEEK AS MaxCapWeek, MIN_CAP_WEEK AS MinCapWeek,
                   WEEK AS Week, MONTH AS Month, YEAR AS Year, CREATEDUSER AS CreatedUser
              FROM LPMSIM.dbo.WMS_WH_MAXMIN_CAP WITH (NOLOCK)
             WHERE COUNTRY = @country AND MONTH = @fromMonth AND YEAR = @fromYear",
            new { country, fromMonth, fromYear }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        if (!string.IsNullOrWhiteSpace(warehouse))
            sourceRows = sourceRows.Where(r => string.Equals(r.Warehouse, warehouse, StringComparison.OrdinalIgnoreCase)).ToList();

        var fromName = System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(fromMonth);
        if (sourceRows.Count == 0)
            return new(false, $"No rows found for {fromName} {fromYear}{(string.IsNullOrWhiteSpace(warehouse) ? "" : $" / {warehouse}")} to copy.", 0);

        var fiscalWeek = await GetFiscalWeekAsync(toYear, toMonth, ct);
        if (fiscalWeek is null)
        {
            var toName = System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(toMonth);
            return new(false, $"No data found for {toName} {toYear} yet — pick a different target month.", 0);
        }
        var (week, weekId) = fiscalWeek.Value;
        var nowGst = DateTime.UtcNow.AddHours(4);
        var isPastMonth = toYear < nowGst.Year || (toYear == nowGst.Year && toMonth < nowGst.Month);

        if (isPastMonth)
        {
            var existingKeys = (await c.QueryAsync<(string Warehouse, string Division)>(new CommandDefinition(
                "SELECT WAREHOUSE, DIVISION FROM LPMSIM.dbo.WMS_WH_MAXMIN_CAP WHERE COUNTRY = @country AND MONTH = @toMonth AND YEAR = @toYear",
                new { country, toMonth, toYear }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .Select(k => (k.Warehouse.Trim().ToUpperInvariant(), k.Division.Trim().ToUpperInvariant()))
                .ToHashSet();
            var conflicts = sourceRows
                .Where(r => existingKeys.Contains((r.Warehouse.Trim().ToUpperInvariant(), r.Division.Trim().ToUpperInvariant())))
                .Select(r => $"{r.Warehouse}/{r.Division}")
                .ToList();
            if (conflicts.Count > 0)
            {
                var toName = System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(toMonth);
                return new(false,
                    $"Already exists for {toName} {toYear} (a previous month) — can't change now: {string.Join(", ", conflicts)}.", 0);
            }
        }

        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            foreach (var r in sourceRows)
            {
                await c.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM LPMSIM.dbo.WMS_WH_MAXMIN_CAP WHERE COUNTRY = @country AND WAREHOUSE = @warehouse AND DIVISION = @division AND MONTH = @toMonth AND YEAR = @toYear",
                    new { country, warehouse = r.Warehouse, division = r.Division, toMonth, toYear },
                    transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

                await c.ExecuteAsync(new CommandDefinition(@"
                    INSERT INTO LPMSIM.dbo.WMS_WH_MAXMIN_CAP (CREATETS, WEEKID, COUNTRY, WAREHOUSE, DIVISION, MAX_CAP_WEEK, MIN_CAP_WEEK, WEEK, MONTH, YEAR, CREATEDUSER)
                    VALUES (@nowGst, @weekId, @country, @warehouse, @division, @maxCapWeek, @minCapWeek, @week, @toMonth, @toYear, @createdUser);",
                    new
                    {
                        country, warehouse = r.Warehouse, division = r.Division, weekId,
                        minCapWeek = r.MinCapWeek, maxCapWeek = r.MaxCapWeek, nowGst, week, toMonth, toYear, createdUser
                    },
                    transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            }

            await tx.CommitAsync(ct);
            return new(true, null, sourceRows.Count);
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(ct); } catch { }
            return new(false, $"Copy failed: {ex.Message}", 0);
        }
    }

    public async Task DeleteRowAsync(string country, string warehouse, string division, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LPMSIM.dbo.WMS_WH_MAXMIN_CAP WHERE COUNTRY = @country AND WAREHOUSE = @warehouse AND DIVISION = @division",
            new { country, warehouse, division }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    public async Task DeleteForWarehouseAsync(string country, string warehouse, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LPMSIM.dbo.WMS_WH_MAXMIN_CAP WHERE COUNTRY = @country AND WAREHOUSE = @warehouse",
            new { country, warehouse }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    public async Task DeleteAllForCountryAsync(string country, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM LPMSIM.dbo.WMS_WH_MAXMIN_CAP WHERE COUNTRY = @country",
            new { country }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }
}
