using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>One line on the Pending for Counting worklist — a container's
/// receipt, split out per (PO, Division, LPM) combination.</summary>
public record PendingForCountingRow(
    string    ContNo,
    DateTime? ReceiptDt,
    int?      AgeingDays,
    string?   Warehouse,
    string?   AllocationCountry,
    DateTime? ProcessedDt,
    string?   PONo,
    string?   Division,
    int       Qty,
    string?   LPM);

/// <summary>
/// "Pending for Counting" — containers received at the warehouse that have
/// neither been purchased-in nor opened for counting yet.
///
/// A container is pending when it has a bfldata.Contreceipt row on/after the
/// cut-off date AND:
///   - its ContType is not 'Non-Trade' (those never get counted), and
///   - its RefNo has no row in usa.USAPurchase   (goods receipt not posted), and
///   - its ContNo has no row in usa.OpenUSACont  (not opened for counting).
///
/// Note the two exclusions key off DIFFERENT columns of Contreceipt — RefNo for
/// USAPurchase, ContNo for OpenUSACont. That mirrors the query this page was
/// specified from; they are not interchangeable.
///
/// PO detail (PONo / Qty / LPM) comes from usa.usaorgfile_LPM keyed on RefNo.
/// Division is NOT a column on that table — it resolves through
/// datareporting.vupc_subclass by itemcode, the same route the Container
/// Allocation page's "Load PO Data" grid uses.
///
/// Everything lives on the on-prem backup connection (usa / bfldata /
/// datareporting via 3-part naming).
/// </summary>
public class PendingForCountingService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 300;

    /// <summary>Cut-off the page opens on — receipts before this are out of scope.</summary>
    public static readonly DateTime DefaultReceiptDtFrom = new(2026, 1, 1);

    private SqlConnection OpenOnPremBackup()
    {
        var b = new SqlConnectionStringBuilder(resolver.GetOnPremBackupConnectionString())
        {
            ConnectTimeout = ConnectTimeoutSeconds
        };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    public async Task<List<PendingForCountingRow>> GetPendingAsync(
        DateTime receiptDtFrom, CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();

        // NOT EXISTS rather than NOT IN on purpose: a single NULL contno anywhere
        // in USAPurchase or OpenUSACont makes NOT IN evaluate to UNKNOWN for every
        // row and the whole report silently returns nothing.
        //
        // The `pending` CTE collapses Contreceipt to one row per container first
        // (MAX(ReceiptDt)) so a container with several receipt lines doesn't get
        // duplicated once the PO detail is joined on.
        var rows = await c.QueryAsync<PendingForCountingRow>(new CommandDefinition(@"
            WITH pending AS (
                SELECT cr.RefNo,
                       ReceiptDt = MAX(cr.ReceiptDt),
                       -- Warehouse off the very Contreceipt rows that put this
                       -- container on the list. MAX() because the CTE collapses to
                       -- one row per RefNo; a container's receipt lines share a
                       -- warehouse, so this is the value, not an arbitrary pick.
                       Warehouse = MAX(cr.Warehouse)
                  FROM bfldata.dbo.Contreceipt cr WITH (NOLOCK)
                 WHERE cr.ReceiptDt >= @receiptDtFrom
                   AND ISNULL(cr.ContType, '') <> 'Non-Trade'
                   AND NOT EXISTS (
                           SELECT 1 FROM usa.dbo.USAPurchase p WITH (NOLOCK)
                            WHERE p.ContNo = cr.RefNo AND p.ContNo <> '')
                   AND NOT EXISTS (
                           SELECT 1 FROM usa.dbo.OpenUSACont o WITH (NOLOCK)
                            WHERE o.ContNo = cr.ContNo)
                   -- Counting finished: BuildingCompletion is the completion record,
                   -- distinct from OpenUSACont above (opened) — a container can be
                   -- opened and completed, and only the latter takes it off this list.
                   AND NOT EXISTS (
                           SELECT 1 FROM bfldata.dbo.BuildingCompletion bc WITH (NOLOCK)
                            WHERE bc.ContNo = cr.RefNo)
                 GROUP BY cr.RefNo
            )
            SELECT
                ContNo            = p.RefNo,
                ReceiptDt         = p.ReceiptDt,
                AgeingDays        = DATEDIFF(day, p.ReceiptDt, CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS date)),
                Warehouse         = p.Warehouse,
                AllocationCountry = ac.AllocationCountry,
                ProcessedDt       = pr.ProcessedDt,
                PONo              = u.OraPONo,
                Division          = sub.Division,
                Qty               = CAST(ISNULL(SUM(u.orgqty), 0) AS INT),
                LPM               = u.LPM
            FROM pending p
            LEFT JOIN usa.dbo.usaorgfile_LPM u WITH (NOLOCK)
                   ON u.ContNo = p.RefNo
            LEFT JOIN datareporting.dbo.vupc_subclass sub WITH (NOLOCK)
                   ON sub.itemcode = u.ItemCode
            -- vUSAOrder is keyed by refno (NOT its own Contno column) and carries
            -- several order rows per container, so the distinct AllocationCountry
            -- values are folded into one cell. DISTINCT sits in a derived table
            -- because STRING_AGG(DISTINCT ...) is not valid T-SQL.
            OUTER APPLY (
                SELECT AllocationCountry = STRING_AGG(x.AllocationCountry, ', ')
                  FROM (SELECT DISTINCT o.AllocationCountry
                          FROM hodata.dbo.vUSAOrder o WITH (NOLOCK)
                         WHERE o.refno = p.RefNo
                           AND NULLIF(LTRIM(RTRIM(o.AllocationCountry)), '') IS NOT NULL) x
            ) ac
            -- When this container's allocation was pushed to WMS-Prod-DB. A container
            -- can be processed and still be pending for counting — that is the gap
            -- this column makes visible. MAX because the push writes one row per piece.
            OUTER APPLY (
                SELECT ProcessedDt = MAX(CAST(pcr.Trndate AS date))
                  FROM Online.dbo.PhotoCheckingResult pcr WITH (NOLOCK)
                 WHERE pcr.ContNo = p.RefNo
            ) pr
            GROUP BY p.RefNo, p.ReceiptDt, p.Warehouse, ac.AllocationCountry, pr.ProcessedDt,
                     u.OraPONo, sub.Division, u.LPM
            ORDER BY p.ReceiptDt, p.RefNo, u.OraPONo, u.LPM",
            new { receiptDtFrom },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return rows.AsList();
    }
}
