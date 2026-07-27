using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>Data service for the Pending Purchase (GRN) Status report.</summary>
public class CountingReportsService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 180;

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    /// <summary>
    /// Pending Purchase (GRN) Status — containers that have completed counting
    /// (bfldata.BuildingCompletion) on/after 2026-01-01 but whose GRN row has
    /// not yet landed in usa.usapurchase. Ageing = days since Trndate (GST).
    /// Divisions is the distinct, comma-joined list of Online.dbo.PhotoCheckingResult
    /// divisions for that container, falling back to bfldata.BUILDINGCOMPLETIONSumm.division
    /// when the container hasn't been photo-checked yet.
    /// </summary>
    public async Task<List<PendingPurchaseRow>> GetPendingPurchaseAsync(CancellationToken ct = default)
    {
        await using var opb = OpenOnPremBackup();
        var rows = await opb.QueryAsync<PendingPurchaseRow>(new CommandDefinition(@"
            SELECT bc.ContNo,
                   CAST(bc.Trndate AS DATE) AS CountingDate,
                   CONVERT(VARCHAR(8), bc.TrnTime, 108) AS TrnTime,
                   ISNULL(bc.BuildingQty, 0) AS CountedQty,
                   DATEDIFF(day, bc.Trndate,
                            CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS DATE)) AS AgeingDays,
                   Divisions = ISNULL(NULLIF(STUFF((
                       SELECT ', ' + d.v
                         FROM (SELECT DISTINCT pcr.Division AS v
                                 FROM Online.dbo.PhotoCheckingResult pcr WITH (NOLOCK)
                                WHERE pcr.ContNo = bc.ContNo
                                  AND ISNULL(pcr.Division, '') <> '') d
                        ORDER BY d.v
                          FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, ''), ''),
                       (SELECT TOP 1 bcs.division
                          FROM bfldata.dbo.BUILDINGCOMPLETIONSumm bcs WITH (NOLOCK)
                         WHERE bcs.ContNo = bc.ContNo
                           AND ISNULL(bcs.division, '') <> ''))
              FROM bfldata.dbo.BuildingCompletion bc WITH (NOLOCK)
             WHERE bc.Trndate >= '2026-01-01'
               AND NOT EXISTS (
                   SELECT 1
                     FROM usa.dbo.usapurchase up WITH (NOLOCK)
                    WHERE up.ContNo = bc.ContNo
               )
             ORDER BY AgeingDays DESC, bc.ContNo",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}
