using System.Data;
using System.Globalization;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

public record ProfitMarginRow(string ItemCode, string? ItemDesc, int ItemQty, decimal CostRate, decimal SalesRate);

/// <summary>
/// Writes parsed TCM Excel rows into #tmptcmitemslab, a local temp table on the on-prem
/// OnPremBackup connection. Local temp tables only live for the SQL connection/session that
/// created them, so this service holds ONE connection open for its whole lifetime (registered
/// AddScoped — in Blazor Server InteractiveServer mode that's the user's browser circuit) instead
/// of opening a fresh connection per call, letting #tmptcmitemslab survive across the Import Data
/// / Calculate / grid-read button clicks on the TCM Lab page. Re-importing the same Container No
/// + Ref No replaces its prior rows.
/// </summary>
public class TcmImportService(IOnPremConnectionResolver resolver) : IAsyncDisposable
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 300;

    private SqlConnection? _conn;
    private readonly SemaphoreSlim _connLock = new(1, 1);

    // Held open for the lifetime of this scoped service so the #tmptcmitemslab / #tmpitemtypelib
    // local temp tables it creates stay visible across separate InsertRowsAsync /
    // CalculateProfitMarginAsync / GetProfitMarginRowsAsync calls on the same page session.
    private async Task<SqlConnection> GetSharedConnectionAsync(CancellationToken ct)
    {
        if (_conn is { State: ConnectionState.Open }) return _conn;

        await _connLock.WaitAsync(ct);
        try
        {
            if (_conn is { State: ConnectionState.Open }) return _conn;
            _conn?.Dispose();

            var b = new SqlConnectionStringBuilder(resolver.GetOnPremBackupConnectionString()) { ConnectTimeout = ConnectTimeoutSeconds };
            var c = new SqlConnection(b.ConnectionString);
            await c.OpenAsync(ct);
            _conn = c;
            return _conn;
        }
        finally { _connLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn is not null) await _conn.DisposeAsync();
        _connLock.Dispose();
    }

    // Created once per session (guarded by OBJECT_ID so a second Import Data click in the same
    // circuit doesn't try to re-create it) — matches the real LPMSIM.dbo.tmptcmitemslab schema.
    private const string CreateTmpTcmItemsLabSql = @"
        IF OBJECT_ID('tempdb..#tmptcmitemslab') IS NULL
        BEGIN
            CREATE TABLE #tmptcmitemslab (
                ContNo       VARCHAR(10)    NULL,
                PalletNo     VARCHAR(50)    NULL,
                TrnDate      SMALLDATETIME  NULL,
                ItemCode     VARCHAR(20)    NULL,
                ItemDesc     VARCHAR(100)   NULL,
                ItemQty      INT            NULL,
                SalesRate    REAL           NULL,
                CostRate     REAL           NULL,
                UserName     VARCHAR(25)    NULL,
                Status       CHAR(1)        NULL,
                IdNo         INT            IDENTITY(1,1) NOT NULL,
                OrgSalesRate NUMERIC(18, 4) NULL,
                EuroRate     NUMERIC(18, 4) NULL,
                RefNo        VARCHAR(10)    NULL,
                Cont1        VARCHAR(15)    NULL,
                Rate1        NUMERIC(18, 2) NULL,
                Diff1        NUMERIC(18, 2) NULL,
                Cont2        VARCHAR(15)    NULL,
                Rate2        NUMERIC(18, 2) NULL,
                Diff2        NUMERIC(18, 2) NULL,
                Cont3        VARCHAR(15)    NULL,
                Rate3        NUMERIC(18, 2) NULL,
                Diff3        NUMERIC(18, 2) NULL,
                Cont4        VARCHAR(15)    NULL,
                Rate4        NUMERIC(18, 2) NULL,
                Diff4        NUMERIC(18, 2) NULL,
                EnglishName  VARCHAR(200)   NULL,
                ITEMTYPE     CHAR(1)        NULL,
                ItemNew      CHAR(1)        NULL,
                FreeQty      INT            NOT NULL,
                Disc         NUMERIC(18, 4) NOT NULL,
                Report       VARCHAR(10)    NULL,
                Grosswt      NUMERIC(18, 4) NULL,
                NetWt        NUMERIC(18, 4) NULL,
                GermPrice    NUMERIC(18, 2) NULL,
                ProjectCode  VARCHAR(15)    NULL,
                BestleNo     VARCHAR(15)    NULL,
                suppcost1    FLOAT          NULL,
                COSTTYPE1    VARCHAR(1)     NULL,
                PalletType   VARCHAR(10)    NULL
            );
        END;";

    private const string InsertSql = @"
        INSERT INTO #tmptcmitemslab
            (ContNo, PalletNo, TrnDate, ItemCode, ItemDesc, ItemQty, SalesRate, CostRate, UserName, Status,
             OrgSalesRate, EuroRate, RefNo, Cont1, Rate1, Diff1, Cont2, Rate2, Diff2, Cont3, Rate3, Diff3,
             Cont4, Rate4, Diff4, EnglishName, ITEMTYPE, ItemNew, FreeQty, Disc, Report, Grosswt, NetWt,
             GermPrice, ProjectCode, BestleNo, suppcost1, COSTTYPE1, PalletType)
        VALUES
            (@ContNo, @PalletNo, @TrnDate, @ItemCode, @ItemDesc, @ItemQty, 0, 0, @UserName, NULL,
             0, 0, @RefNo, NULL, 0, NULL, NULL, NULL, NULL, @Cont3, NULL, NULL,
             @Cont4, NULL, NULL, @EnglishName, NULL, 'N', 0, 0, NULL, @Grosswt, @NetWt,
             @GermPrice, @ProjectCode, @BesteNo, 0, NULL, @PalletType);";

    // Recreated fresh on every import (drop-if-exists, since a session may import more than
    // once) — no permanent footprint on LPMSIM.
    private const string CreateTmpItemTypeLibSql = @"
        IF OBJECT_ID('tempdb..#tmpitemtypelib') IS NOT NULL DROP TABLE #tmpitemtypelib;
        CREATE TABLE #tmpitemtypelib (
            Itemcode       NVARCHAR(255)  NULL,
            ENGLISHNAME    NVARCHAR(255)  NULL,
            ItemType       NVARCHAR(255)  NULL,
            Refno          VARCHAR(3)     NULL,
            Category       VARCHAR(50)    NULL,
            Remarks        VARCHAR(250)   NULL,
            Reference      VARCHAR(100)   NULL,
            TrnDate        SMALLDATETIME  NULL,
            GrossWt        NUMERIC(18, 4) NULL,
            NetWt          NUMERIC(18, 4) NULL,
            WtRefno        VARCHAR(25)    NULL,
            MitchRef       VARCHAR(25)    NULL,
            Electric       VARCHAR(5)     NULL,
            Groups         VARCHAR(15)    NULL,
            Season         VARCHAR(1)     NULL,
            GermPrice      NUMERIC(18, 2) NULL,
            BeirutType     VARCHAR(25)    NULL,
            HSCode         VARCHAR(15)    NULL,
            ItemDetails    VARCHAR(500)   NULL,
            Origin         VARCHAR(15)    NULL,
            FinalHscode    VARCHAR(15)    NULL,
            ProjectCode    VARCHAR(25)    NULL,
            EuroRate       NUMERIC(18, 2) NULL,
            SalesPrice     INT            NULL,
            ProjectCodeF   VARCHAR(25)    NULL,
            ExpQty         INT            NULL,
            InTrMixQty     INT            NULL,
            InTrPHQty      INT            NULL,
            InGrMixQty     INT            NULL,
            InGrPHQty      INT            NULL,
            BestleNo       VARCHAR(15)    NULL,
            DiscGermPrice  NUMERIC(18, 2) NULL,
            ShopQty        INT            NULL,
            ResQty         INT            NULL,
            Photo          VARCHAR(1)     NULL,
            Euro05Below    NUMERIC(18, 2) NULL,
            Euro06         NUMERIC(18, 2) NULL,
            Euro07         NUMERIC(18, 2) NULL,
            Euro08         NUMERIC(18, 2) NULL,
            Euro09         NUMERIC(18, 2) NULL,
            Euro10         NUMERIC(18, 2) NULL,
            Euro11         NUMERIC(18, 2) NULL,
            Euro12         NUMERIC(18, 2) NULL,
            Euro13         NUMERIC(18, 2) NULL,
            Euro14         NUMERIC(18, 2) NULL,
            ItemCat        VARCHAR(120)   NULL,
            LatestDt       SMALLDATETIME  NULL,
            SoldQty        INT            NULL,
            BeyQty         INT            NULL,
            HOQTY          INT            NULL,
            CostRate       NUMERIC(18, 2) NULL,
            EuroChange     VARCHAR(1)     NULL
        );";

    /// <param name="rows">Each row is the Details-tab parsed dictionary, keyed by the field
    /// names produced in TcmLaboratory.razor's ParseXlsx (Pallet, Itemcode, Qty, Origin, ...).</param>
    public async Task<int> InsertRowsAsync(
        string contNo, string refNo, string labType, string userName,
        IReadOnlyList<Dictionary<string, string>> rows, CancellationToken ct = default)
    {
        var conn = await GetSharedConnectionAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                CreateTmpTcmItemsLabSql, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM #tmptcmitemslab WHERE ContNo = @contNo AND RefNo = @refNo;",
                new { contNo, refNo }, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            var trnDate = DateTime.Now;
            var parameters = rows
                .Where(r => !string.IsNullOrEmpty(Get(r, "Itemcode")))
                .Select(r =>
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

            await conn.ExecuteAsync(new CommandDefinition(
                CreateTmpItemTypeLibSql, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO #tmpitemtypelib
                SELECT * FROM abudata.dbo.ItemTypeLib
                 WHERE itemcode IN (SELECT itemcode FROM #tmptcmitemslab);",
                transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

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
        var conn = await GetSharedConnectionAsync(ct);
        var rows = await conn.QueryAsync<ProfitMarginRow>(new CommandDefinition(@"
            SELECT ItemCode, MAX(ItemDesc) AS ItemDesc, SUM(ItemQty) AS ItemQty,
                   CAST(MAX(CostRate) AS DECIMAL(18,4)) AS CostRate,
                   CAST(MAX(SalesRate) AS DECIMAL(18,4)) AS SalesRate
              FROM #tmptcmitemslab
             WHERE ContNo = @contNo AND RefNo = @refNo
             GROUP BY ItemCode
             ORDER BY ItemCode;",
            new { contNo, refNo }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ====================== Profit Margin "Calculate" (ported from the legacy VB6 lab tool) ======================
    // Operates on the whole #tmptcmitemslab staging table, same as the VB6 source — it isn't
    // scoped to one Container/Ref batch there either. abudata/itemtypelib/tcmnewprice are
    // referenced unqualified in the VB6 source (its connection's default catalog is abudata);
    // qualified here as abudata.dbo.* since our connection's default catalog is LPMSIM. Verify
    // abudata.dbo.itemtypelib / abudata.dbo.tcmnewprice resolve from the OnPremBackup connection
    // the same way bfldata/usa/racks/hodata/datareporting do.
    public async Task CalculateProfitMarginAsync(
        decimal supplierCost, decimal fcRateForCost, decimal additionalExpensesPct, bool useMrp, CancellationToken ct = default)
    {
        var conn = await GetSharedConnectionAsync(ct);
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

            await Exec("reset", "UPDATE #tmptcmitemslab SET SalesRate = 0, CostRate = 0;");

            // Non-bulky items: latest price change for that item.
            await Exec("non-bulky price lookup", @"
                UPDATE a
                   SET SalesRate = (
                       SELECT TOP 1 newprice FROM bfldata.dbo.pricechange
                        WHERE itemcode = a.itemcode ORDER BY trndate DESC, time1 DESC)
                  FROM #tmptcmitemslab a
                 WHERE itemcode NOT IN (SELECT itemcode FROM abudata.dbo.BulkyNewPrice);");

            // Bulky items: same, but only price changes from 2016-01-27 onward.
            await Exec("bulky price lookup", @"
                UPDATE a
                   SET SalesRate = (
                       SELECT TOP 1 newprice FROM bfldata.dbo.pricechange
                        WHERE TRY_CONVERT(date, trndate) >= '2016-01-27' AND itemcode = a.itemcode
                        ORDER BY trndate DESC, time1 DESC)
                  FROM #tmptcmitemslab a
                 WHERE itemcode IN (SELECT itemcode FROM abudata.dbo.BulkyNewPrice);");

            // Still-unpriced rows: fall back to the item's project group's own sales price
            // (set-based equivalent of the VB6 recordset loop over the same query).
            await Exec("project-group fallback price", @"
                UPDATE a
                   SET SalesRate = sub.SalesRate
                  FROM #tmptcmitemslab a
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
                  FROM #tmptcmitemslab a
                 WHERE ISNULL(salesrate, 0) = 0 AND itemcode IN (SELECT itemcode FROM abudata.dbo.BulkyNewPrice);");

            // Explicit new-price overrides.
            await Exec("new-price override", @"
                UPDATE a
                   SET SalesRate = (
                       SELECT newprice FROM abudata.dbo.tcmnewprice
                        WHERE itemcode = a.itemcode AND ISNULL(newprice, 0) > 0)
                  FROM #tmptcmitemslab a
                 WHERE itemcode IN (SELECT itemcode FROM abudata.dbo.tcmnewprice WHERE ISNULL(newprice, 0) > 0);");

            // Items in the new-price list still unpriced: germprice * 0.34 * 5.
            await Exec("new-price list germprice fallback", @"
                UPDATE #tmptcmitemslab
                   SET SalesRate = ROUND(germprice * 0.34 * 5, 0)
                 WHERE salesrate IS NULL AND itemcode IN (SELECT itemcode FROM abudata.dbo.tcmnewprice);");

            // Everything else still unpriced: germprice * 0.44 * 5 * 1.05.
            await Exec("default germprice fallback", @"
                UPDATE #tmptcmitemslab
                   SET SalesRate = ROUND(germprice * 0.44 * 5 * 1.05, 0)
                 WHERE salesrate IS NULL;");

            await Exec("floor sales rate at 5", "UPDATE #tmptcmitemslab SET SalesRate = 5 WHERE SalesRate < 5;");
            await Exec("snapshot orig sales rate", "UPDATE #tmptcmitemslab SET OrgSalesRate = SalesRate;");

            var mcost = fcRateForCost * additionalExpensesPct * supplierCost;
            if (useMrp)
                await Exec("cost rate (MRP)",
                    "UPDATE #tmptcmitemslab SET CostRate = ROUND(@mcost * germprice, 2), Rate3 = @suppCost;",
                    new { mcost, suppCost = supplierCost });
            else
                await Exec("cost rate (non-MRP)",
                    "UPDATE #tmptcmitemslab SET CostRate = ROUND(@mcost * EuroRate, 2);",
                    new { mcost });

            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }
}
