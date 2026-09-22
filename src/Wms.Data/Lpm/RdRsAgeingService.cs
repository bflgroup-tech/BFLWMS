using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>One line of the RD/RS ageing summary — the email's table row.</summary>
public record RdRsAgeingRow(
    string  PalletType,
    string? TypeName,
    string  Division,
    long    TotalQty,
    decimal AvgAgeDays);

/// <summary>
/// The weekly RD / RS ageing figures: open warehouse stock sitting on
/// return-to-vendor (RD → RTV) and MC-hold (RS → MC HOLD) pallets, grouped by
/// pallet type and division, with how long it has been sitting.
///
/// Source is racks.dbo.whboxitems joined to BFLDATA.dbo.PalletType for TypeName
/// — both on-prem, so this is one query on one server. Division is resolved per
/// item from datareporting.dbo.vupc_subclass (deduped to MIN per itemcode, the
/// same fix the SOH reports use for that view's ~400 duplicate itemcode rows),
/// because the box row's own division is not the item's division.
///
/// Age is measured from the box's created date. That column's NAME is discovered
/// at runtime from sys.columns rather than hard-coded: this table is not owned by
/// this app, and guessing wrong would mean a report that fails only in
/// production. See ResolveAgeColumnAsync.
/// </summary>
public class RdRsAgeingService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 300;

    /// <summary>Pallet type codes this report covers, in the order the mail lists them.</summary>
    public static readonly string[] PalletTypes = ["RD", "RS"];

    /// <summary>
    /// Candidate names for "when was this box created", most likely first. The
    /// first one that actually exists on racks.dbo.whboxitems wins.
    /// </summary>
    private static readonly string[] AgeColumnCandidates =
        ["CreateTS", "CreateTs", "CreatedTS", "Created_At", "CreateDate", "CreatedDate", "EntryDate", "TrnDate", "BoxDate"];

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    /// <summary>
    /// Which column on racks.dbo.whboxitems carries the box's created date.
    /// Resolved from sys.columns so a schema that names it differently still
    /// works, and so a missing column fails with a message naming what was
    /// looked for instead of "Invalid column name 'CreateTS'".
    /// </summary>
    public async Task<string> ResolveAgeColumnAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        return await ResolveAgeColumnAsync(c, ct);
    }

    private static async Task<string> ResolveAgeColumnAsync(SqlConnection c, CancellationToken ct)
    {
        var cols = (await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT c.name
              FROM racks.sys.columns c
              JOIN racks.sys.objects o ON o.object_id = c.object_id
             WHERE o.name = 'whboxitems' AND o.type = 'U'",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var cand in AgeColumnCandidates)
            if (cols.Contains(cand)) return cand;

        throw new InvalidOperationException(
            "racks.dbo.whboxitems has no recognised box-created date column. Looked for: " +
            string.Join(", ", AgeColumnCandidates) +
            ". Add the real column name to RdRsAgeingService.AgeColumnCandidates.");
    }

    /// <summary>
    /// The summary, one row per (PalletType, TypeName, Division), ordered the way
    /// the mail presents it. Only open boxes count — a closed box has shipped and
    /// is no longer ageing in the warehouse.
    /// </summary>
    public async Task<List<RdRsAgeingRow>> GetAgeingAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var ageCol = await ResolveAgeColumnAsync(c, ct);

        // ageCol comes from sys.columns, never from user input, so interpolating it
        // is safe — it is a real column name on that table by construction.
        var sql = $@"
            ;WITH div AS (
                SELECT itemcode, Division = MIN(Division)
                  FROM datareporting.dbo.vupc_subclass WITH (NOLOCK)
                 WHERE Division IS NOT NULL AND LTRIM(RTRIM(Division)) <> ''
                 GROUP BY itemcode
            )
            SELECT w.PalletType,
                   TypeName   = MAX(pt.TypeName),
                   Division   = ISNULL(d.Division, 'Unknown'),
                   TotalQty   = SUM(CAST(ISNULL(w.Qty, 0) AS bigint)),
                   AvgAgeDays = CAST(AVG(CAST(DATEDIFF(day, w.[{ageCol}], CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS date)) AS decimal(18,4))) AS decimal(18,1))
              FROM racks.dbo.whboxitems w WITH (NOLOCK)
              LEFT JOIN BFLDATA.dbo.PalletType pt WITH (NOLOCK)
                     ON pt.PalletType = w.PalletType
              LEFT JOIN div d ON d.itemcode = w.ItemCode
             WHERE w.PalletType IN @types
               AND w.[{ageCol}] IS NOT NULL
               AND ISNULL(w.Qty, 0) <> 0
             GROUP BY w.PalletType, ISNULL(d.Division, 'Unknown')
            HAVING SUM(CAST(ISNULL(w.Qty, 0) AS bigint)) <> 0
             ORDER BY w.PalletType, ISNULL(d.Division, 'Unknown')";

        var rows = await c.QueryAsync<RdRsAgeingRow>(new CommandDefinition(
            sql, new { types = PalletTypes },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
