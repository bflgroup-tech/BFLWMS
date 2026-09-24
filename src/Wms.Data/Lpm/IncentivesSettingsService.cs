using System.Data;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

public record IncEmployeeRow(
    string EmpCode, string? EmpName, string? Country, string? Warehouse, string? Category,
    string? UploadedUser, DateTime? CreateTS);

public record IncEmployeeUploadRow(string EmpCode, string? EmpName, string? Country, string? Warehouse, string? Category);

public record IncAttendanceRow(
    string EmpCode, DateTime AttendanceDate, TimeSpan? ShiftTimingFrom, TimeSpan? ShiftTimingTo,
    string? DiscrepancyType, int? DiscrepancyDuration, string? UploadedUser, DateTime? CreateTS);

public record IncAttendanceUploadRow(
    string EmpCode, DateTime AttendanceDate, TimeSpan? ShiftTimingFrom, TimeSpan? ShiftTimingTo, string DiscrepancyType,
    int? DiscrepancyDuration);  // minutes; required 1..MaxDiscrepancyMinutes for Earlygoing/LateComing, blank for Absent

public record IncUploadResult(bool Ok, int RowsSaved, string? Error, List<string> Problems);

/// <summary>
/// Settings tab of Warehouse Incentives (Admin / Payroll only): Excel uploads into
/// DATAREPORTING.dbo.INC_EmployeeMaster and DATAREPORTING.dbo.INC_EmployeeAttendance,
/// stamped with the uploading user (UploadedUser) and UAE time (CreateTS).
///
/// Both uploads are append-only and all-or-nothing: if any row clashes with what is
/// already stored (an EmpCode already in the master; an EmpCode + AttendanceDate
/// already in attendance), nothing is saved and the clashing rows are listed so the
/// user can drop them from the file and upload again. Attendance is only accepted for
/// EmpCodes already in INC_EmployeeMaster.
///
/// Uses the WmsProductionDb connection (not OnPremBackupDB): the OnPremBackupDB login
/// could read DATAREPORTING but was denied INSERT on the INC_ tables in production,
/// same as it was denied UPDATE on BFLDATA for GIN Trailer Update. WmsProductionDb is
/// the connection the other on-prem-writing features use (GinTrailerUpdateService,
/// GenerateEan13Service, TechnoBuildingService, ...).
/// </summary>
public class IncentivesSettingsService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 60;
    private const int ConnectTimeoutSeconds = 15;
    private const int ChunkSize = 1000;

    /// <summary>The only DiscrepancyType values accepted, in their stored spelling.</summary>
    public static readonly IReadOnlyList<string> DiscrepancyTypes = ["Earlygoing", "LateComing", "Absent"];

    /// <summary>Upper limit for DiscrepancyDuration (minutes) — a 9-hour shift.</summary>
    public const int MaxDiscrepancyMinutes = 9 * 60;

    private SqlConnection Open()
    {
        var b = new SqlConnectionStringBuilder(resolver.GetWmsProductionDbConnectionString()) { ConnectTimeout = ConnectTimeoutSeconds };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    private static DateTime NowUae() => DateTime.UtcNow.AddHours(4);

    // ===================== Employee master =====================

    public async Task<List<IncEmployeeRow>> GetEmployeesAsync(string? empCode, CancellationToken ct = default)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<IncEmployeeRow>(new CommandDefinition(@"
            SELECT EmpCode, EmpName, Country, Warehouse, Category, UploadedUser, CreateTS
              FROM DATAREPORTING.dbo.INC_EmployeeMaster WITH (NOLOCK)
             WHERE (@empCode IS NULL OR EmpCode LIKE '%' + @empCode + '%')
             ORDER BY EmpCode",
            new { empCode = Blank(empCode) }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IncUploadResult> UploadEmployeesAsync(
        IReadOnlyList<IncEmployeeUploadRow> rows, string uploadedUser, CancellationToken ct = default)
    {
        if (rows.Count == 0) return new(false, 0, "No rows to upload.", []);

        await using var c = Open();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            var existing = new List<string>();
            foreach (var chunk in rows.Select(r => r.EmpCode).Chunk(ChunkSize))
                existing.AddRange(await c.QueryAsync<string>(new CommandDefinition(@"
                    SELECT EmpCode FROM DATAREPORTING.dbo.INC_EmployeeMaster WITH (UPDLOCK, HOLDLOCK)
                     WHERE EmpCode IN @codes",
                    new { codes = chunk }, tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)));
            if (existing.Count > 0)
            {
                await tx.RollbackAsync(ct);
                var list = existing.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
                return new(false, 0,
                    $"{list.Count} employee(s) already exist in the master — nothing was saved. Remove them from the file and upload again.",
                    list.Select(e => $"EmpCode {e} already exists").ToList());
            }

            var now = NowUae();
            var table = new DataTable();
            foreach (var col in new[] { "EmpCode", "EmpName", "Country", "Warehouse", "Category", "UploadedUser" })
                table.Columns.Add(col, typeof(string));
            table.Columns.Add("CreateTS", typeof(DateTime));
            foreach (var r in rows)
                table.Rows.Add(r.EmpCode, (object?)r.EmpName ?? DBNull.Value, (object?)r.Country ?? DBNull.Value,
                               (object?)r.Warehouse ?? DBNull.Value, (object?)r.Category ?? DBNull.Value, uploadedUser, now);

            await BulkInsertAsync(c, tx, "DATAREPORTING.dbo.INC_EmployeeMaster", table, ct);
            await tx.CommitAsync(ct);
            return new(true, rows.Count, null, []);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            return new(false, 0, ex.Message, []);
        }
    }

    // ===================== Attendance discrepancies =====================

    public async Task<List<IncAttendanceRow>> GetAttendanceAsync(
        DateTime from, DateTime to, string? empCode, CancellationToken ct = default)
    {
        await using var c = Open();
        var rows = await c.QueryAsync<IncAttendanceRow>(new CommandDefinition(@"
            SELECT EmpCode, AttendanceDate, ShiftTimingFrom, ShiftTimingTo, DiscrepancyType, DiscrepancyDuration, UploadedUser, CreateTS
              FROM DATAREPORTING.dbo.INC_EmployeeAttendance WITH (NOLOCK)
             WHERE AttendanceDate >= @from AND AttendanceDate <= @to
               AND (@empCode IS NULL OR EmpCode LIKE '%' + @empCode + '%')
             ORDER BY AttendanceDate, EmpCode",
            new { from = from.Date, to = to.Date, empCode = Blank(empCode) },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    private record EmpDateKey(string EmpCode, DateTime AttendanceDate);

    /// <summary>EmpCodes from <paramref name="codes"/> that are not in INC_EmployeeMaster —
    /// attendance is only accepted for employees already in the master.</summary>
    public async Task<List<string>> GetEmpCodesNotInMasterAsync(IEnumerable<string> codes, CancellationToken ct = default)
    {
        await using var c = Open();
        return await EmpCodesNotInMasterAsync(c, null, codes, ct);
    }

    private static async Task<List<string>> EmpCodesNotInMasterAsync(
        SqlConnection c, SqlTransaction? tx, IEnumerable<string> codes, CancellationToken ct)
    {
        var wanted = codes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in wanted.Chunk(ChunkSize))
            foreach (var code in await c.QueryAsync<string>(new CommandDefinition(@"
                SELECT EmpCode FROM DATAREPORTING.dbo.INC_EmployeeMaster WITH (NOLOCK)
                 WHERE EmpCode IN @codes",
                new { codes = chunk }, tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                found.Add(code.Trim());
        return wanted.Where(w => !found.Contains(w)).OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IncUploadResult> UploadAttendanceAsync(
        IReadOnlyList<IncAttendanceUploadRow> rows, string uploadedUser, CancellationToken ct = default)
    {
        if (rows.Count == 0) return new(false, 0, "No rows to upload.", []);

        var codes = rows.Select(r => r.EmpCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var minDate = rows.Min(r => r.AttendanceDate).Date;
        var maxDate = rows.Max(r => r.AttendanceDate).Date;

        await using var c = Open();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            var notInMaster = await EmpCodesNotInMasterAsync(c, tx, codes, ct);
            if (notInMaster.Count > 0)
            {
                await tx.RollbackAsync(ct);
                return new(false, 0,
                    $"{notInMaster.Count} EmpCode(s) are not in the Employee Master — nothing was saved. Upload them to the Employee Master first, or remove them from the file.",
                    notInMaster.Select(e => $"EmpCode {e} is not in the Employee Master").ToList());
            }

            var stored = new HashSet<(string, DateTime)>();
            foreach (var chunk in codes.Chunk(ChunkSize))
                foreach (var k in await c.QueryAsync<EmpDateKey>(new CommandDefinition(@"
                    SELECT EmpCode, AttendanceDate FROM DATAREPORTING.dbo.INC_EmployeeAttendance WITH (UPDLOCK, HOLDLOCK)
                     WHERE EmpCode IN @codes AND AttendanceDate >= @minDate AND AttendanceDate <= @maxDate",
                    new { codes = chunk, minDate, maxDate }, tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                    stored.Add((k.EmpCode.Trim().ToUpperInvariant(), k.AttendanceDate.Date));

            var clashes = rows.Where(r => stored.Contains((r.EmpCode.ToUpperInvariant(), r.AttendanceDate.Date)))
                              .OrderBy(r => r.AttendanceDate).ThenBy(r => r.EmpCode).ToList();
            if (clashes.Count > 0)
            {
                await tx.RollbackAsync(ct);
                return new(false, 0,
                    $"{clashes.Count} row(s) already exist for the employee on that date — nothing was saved. Remove them from the file and upload again.",
                    clashes.Select(r => $"EmpCode {r.EmpCode} on {r.AttendanceDate:dd-MMM-yyyy} already exists").ToList());
            }

            var now = NowUae();
            var table = new DataTable();
            table.Columns.Add("EmpCode", typeof(string));
            table.Columns.Add("AttendanceDate", typeof(DateTime));
            table.Columns.Add("ShiftTimingFrom", typeof(TimeSpan));
            table.Columns.Add("ShiftTimingTo", typeof(TimeSpan));
            table.Columns.Add("DiscrepancyType", typeof(string));
            table.Columns.Add("DiscrepancyDuration", typeof(int));
            table.Columns.Add("UploadedUser", typeof(string));
            table.Columns.Add("CreateTS", typeof(DateTime));
            foreach (var r in rows)
                table.Rows.Add(r.EmpCode, r.AttendanceDate.Date,
                               r.ShiftTimingFrom.HasValue ? r.ShiftTimingFrom.Value : (object)DBNull.Value,
                               r.ShiftTimingTo.HasValue ? r.ShiftTimingTo.Value : (object)DBNull.Value,
                               r.DiscrepancyType,
                               r.DiscrepancyDuration.HasValue ? r.DiscrepancyDuration.Value : (object)DBNull.Value,
                               uploadedUser, now);

            await BulkInsertAsync(c, tx, "DATAREPORTING.dbo.INC_EmployeeAttendance", table, ct);
            await tx.CommitAsync(ct);
            return new(true, rows.Count, null, []);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            return new(false, 0, ex.Message, []);
        }
    }

    // ===================== helpers =====================

    private static async Task BulkInsertAsync(SqlConnection c, SqlTransaction tx, string table, DataTable data, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.CheckConstraints, tx)
        {
            DestinationTableName = table,
            BulkCopyTimeout = CommandTimeoutSeconds,
        };
        foreach (DataColumn col in data.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(data, ct);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
