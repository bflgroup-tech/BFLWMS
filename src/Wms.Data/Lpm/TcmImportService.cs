using System.Globalization;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

public record ProfitMarginRow(string ItemCode, string? ItemDesc, int ItemQty, decimal CostRate, decimal SalesRate);

/// <summary>
/// Writes parsed TCM Excel rows into the on-prem staging table LPMSIM.dbo.tmptcmitemslab
/// (reachable via 3-part naming from the OnPremBackup connection). Re-importing the same
/// Container No + Ref No replaces its prior rows.
/// </summary>
public class TcmImportService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 300;

    private SqlConnection OpenOnPremBackup()
    {
        var b = new SqlConnectionStringBuilder(resolver.GetOnPremBackupConnectionString()) { ConnectTimeout = ConnectTimeoutSeconds };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    private const string InsertSql = @"
        INSERT INTO LPMSIM.dbo.tmptcmitemslab
            (ContNo, PalletNo, TrnDate, ItemCode, ItemDesc, ItemQty, SalesRate, CostRate, UserName, Status,
             OrgSalesRate, EuroRate, RefNo, Cont1, Rate1, Diff1, Cont2, Rate2, Diff2, Cont3, Rate3, Diff3,
             Cont4, Rate4, Diff4, EnglishName, ITEMTYPE, ItemNew, FreeQty, Disc, Report, Grosswt, NetWt,
             GermPrice, ProjectCode, BestleNo, suppcost1, COSTTYPE1, PalletType)
        VALUES
            (@ContNo, @PalletNo, @TrnDate, @ItemCode, @ItemDesc, @ItemQty, 0, 0, @UserName, NULL,
             0, 0, @RefNo, NULL, 0, NULL, NULL, NULL, NULL, @Cont3, NULL, NULL,
             @Cont4, NULL, NULL, @EnglishName, NULL, 'N', 0, 0, NULL, @Grosswt, @NetWt,
             @GermPrice, @ProjectCode, @BesteNo, 0, NULL, @PalletType);";

    /// <param name="rows">Each row is the Details-tab parsed dictionary, keyed by the field
    /// names produced in TcmLaboratory.razor's ParseXlsx (Pallet, Itemcode, Qty, Origin, ...).</param>
    public async Task<int> InsertRowsAsync(
        string contNo, string refNo, string labType, string userName,
        IReadOnlyList<Dictionary<string, string>> rows, CancellationToken ct = default)
    {
        await using var conn = OpenOnPremBackup();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM LPMSIM.dbo.tmptcmitemslab WHERE ContNo = @contNo AND RefNo = @refNo;",
                new { contNo, refNo }, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var trnDate = DateTime.Now;
            var parameters = rows.Select(r =>
            {
                var desc = Get(r, "DescriptionEnglish");
                return new
                {
                    ContNo = contNo,
                    PalletNo = Get(r, "Pallet"),
                    TrnDate = trnDate,
                    ItemCode = Get(r, "Itemcode"),
                    ItemDesc = desc,
                    ItemQty = ParseNum(Get(r, "Qty")),
                    UserName = userName,
                    RefNo = refNo,
                    Cont3 = Get(r, "Origin"),
                    Cont4 = refNo,
                    EnglishName = desc,
                    Grosswt = ParseNum(Get(r, "GrossWeight")),
                    NetWt = ParseNum(Get(r, "NetWeight")),
                    GermPrice = ParseNum(Get(r, "TchiboRP")),
                    ProjectCode = Get(r, "ProjectNo"),
                    BesteNo = Get(r, "OrderNo"),
                    PalletType = labType,
                };
            }).ToList();

            await conn.ExecuteAsync(new CommandDefinition(
                InsertSql, parameters, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return parameters.Count;
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    private static string Get(Dictionary<string, string> row, string key) => row.GetValueOrDefault(key, "");

    private static decimal ParseNum(string? s) =>
        decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    /// <summary>One row per distinct ItemCode (qty summed across pallets) for the current
    /// Container No + Ref No batch, reflecting whatever CalculateProfitMarginAsync last wrote.</summary>
    public async Task<List<ProfitMarginRow>> GetProfitMarginRowsAsync(string contNo, string refNo, CancellationToken ct = default)
    {
        await using var conn = OpenOnPremBackup();
        var rows = await conn.QueryAsync<ProfitMarginRow>(new CommandDefinition(@"
            SELECT ItemCode, MAX(ItemDesc) AS ItemDesc, SUM(ItemQty) AS ItemQty,
                   MAX(CostRate) AS CostRate, MAX(SalesRate) AS SalesRate
              FROM LPMSIM.dbo.tmptcmitemslab
             WHERE ContNo = @contNo AND RefNo = @refNo
             GROUP BY ItemCode
             ORDER BY ItemCode;",
            new { contNo, refNo }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ====================== Profit Margin "Calculate" (ported from the legacy VB6 lab tool) ======================
    // Operates on the whole LPMSIM.dbo.tmptcmitemslab staging table, same as the VB6 source —
    // it isn't scoped to one Container/Ref batch there either. abudata/itemtypelib/tcmnewprice
    // are referenced unqualified in the VB6 source (its connection's default catalog is
    // abudata); qualified here as abudata.dbo.* since our connection's default catalog is
    // LPMSIM. Verify abudata.dbo.itemtypelib / abudata.dbo.tcmnewprice resolve from the
    // OnPremBackup connection the same way bfldata/usa/racks/hodata/datareporting do.
    public async Task CalculateProfitMarginAsync(
        decimal supplierCost, decimal fcRateForCost, decimal additionalExpensesPct, bool useMrp, CancellationToken ct = default)
    {
        await using var conn = OpenOnPremBackup();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            async Task Exec(string step, string sql, object? param = null)
            {
                try
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        sql, param, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                }
                catch (Exception ex) { throw new InvalidOperationException($"Step '{step}' failed: {ex.Message}", ex); }
            }

            await Exec("reset", "UPDATE LPMSIM.dbo.tmptcmitemslab SET SalesRate = 0, CostRate = 0;");

            // Non-bulky items: latest price change for that item.
            await Exec("non-bulky price lookup", @"
                UPDATE a
                   SET SalesRate = (
                       SELECT TOP 1 newprice FROM bfldata.dbo.pricechange
                        WHERE itemcode = a.itemcode ORDER BY trndate DESC, time1 DESC)
                  FROM LPMSIM.dbo.tmptcmitemslab a
                 WHERE itemcode NOT IN (SELECT itemcode FROM abudata.dbo.BulkyNewPrice);");

            // Bulky items: same, but only price changes from 2016-01-27 onward.
            await Exec("bulky price lookup", @"
                UPDATE a
                   SET SalesRate = (
                       SELECT TOP 1 newprice FROM bfldata.dbo.pricechange
                        WHERE TRY_CONVERT(date, trndate) >= '2016-01-27' AND itemcode = a.itemcode
                        ORDER BY trndate DESC, time1 DESC)
                  FROM LPMSIM.dbo.tmptcmitemslab a
                 WHERE itemcode IN (SELECT itemcode FROM abudata.dbo.BulkyNewPrice);");

            // Still-unpriced rows: fall back to the item's project group's own sales price
            // (set-based equivalent of the VB6 recordset loop over the same query).
            await Exec("project-group fallback price", @"
                UPDATE a
                   SET SalesRate = sub.SalesRate
                  FROM LPMSIM.dbo.tmptcmitemslab a
                  INNER JOIN abudata.dbo.itemtypelib b ON b.itemcode = a.itemcode
                  CROSS APPLY (
                      SELECT TOP 1 salesprice AS SalesRate
                        FROM abudata.dbo.itemtypelib
                       WHERE projectcodef = b.projectcodef AND ISNULL(salesprice, 0) <> 0
                  ) sub
                 WHERE a.salesrate IS NULL AND ISNULL(sub.SalesRate, 0) <> 0;");

            // Still-unpriced bulky items: germprice * 0.35 * 5.
            await Exec("bulky germprice fallback", @"
                UPDATE a
                   SET SalesRate = ROUND(germprice * 0.35 * 5, 0)
                  FROM LPMSIM.dbo.tmptcmitemslab a
                 WHERE ISNULL(salesrate, 0) = 0 AND itemcode IN (SELECT itemcode FROM abudata.dbo.BulkyNewPrice);");

            // Explicit new-price overrides.
            await Exec("new-price override", @"
                UPDATE a
                   SET SalesRate = (
                       SELECT newprice FROM abudata.dbo.tcmnewprice
                        WHERE itemcode = a.itemcode AND ISNULL(newprice, 0) > 0)
                  FROM LPMSIM.dbo.tmptcmitemslab a
                 WHERE itemcode IN (SELECT itemcode FROM abudata.dbo.tcmnewprice WHERE ISNULL(newprice, 0) > 0);");

            // Items in the new-price list still unpriced: germprice * 0.34 * 5.
            await Exec("new-price list germprice fallback", @"
                UPDATE LPMSIM.dbo.tmptcmitemslab
                   SET SalesRate = ROUND(germprice * 0.34 * 5, 0)
                 WHERE salesrate IS NULL AND itemcode IN (SELECT itemcode FROM abudata.dbo.tcmnewprice);");

            // Everything else still unpriced: germprice * 0.44 * 5 * 1.05.
            await Exec("default germprice fallback", @"
                UPDATE LPMSIM.dbo.tmptcmitemslab
                   SET SalesRate = ROUND(germprice * 0.44 * 5 * 1.05, 0)
                 WHERE salesrate IS NULL;");

            await Exec("floor sales rate at 5", "UPDATE LPMSIM.dbo.tmptcmitemslab SET SalesRate = 5 WHERE SalesRate < 5;");
            await Exec("snapshot orig sales rate", "UPDATE LPMSIM.dbo.tmptcmitemslab SET OrgSalesRate = SalesRate;");

            var mcost = fcRateForCost * additionalExpensesPct * supplierCost;
            if (useMrp)
                await Exec("cost rate (MRP)",
                    "UPDATE LPMSIM.dbo.tmptcmitemslab SET CostRate = ROUND(@mcost * germprice, 2), Rate3 = @suppCost;",
                    new { mcost, suppCost = supplierCost });
            else
                await Exec("cost rate (non-MRP)",
                    "UPDATE LPMSIM.dbo.tmptcmitemslab SET CostRate = ROUND(@mcost * EuroRate, 2);",
                    new { mcost });

            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }
}
