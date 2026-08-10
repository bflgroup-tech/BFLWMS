using System.Globalization;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

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
}
