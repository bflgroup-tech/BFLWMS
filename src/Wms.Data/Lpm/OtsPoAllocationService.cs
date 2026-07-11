using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Lpm;

/// <summary>
/// Data service for the "OTS for PO Allocation" report.
///
/// Grain: one row per (Country, StoreID, DivCode) filtered by a picked
/// (Month, Year) and optional country filter.
///
/// Sources:
///   - LPMSIM.dbo.LPM_EOM_Output      (base grid: StoreID, Country, DivCode,
///                                     VolumeGroup, PriorityRank, MerchNeedMonth)
///   - LPMSIM.dbo.Divisions           (DivCode -> Division human name)
///   - bfldata.dbo.DataSettings       (StoreID -> PBFullname Store Name)
///   - Racks.dbo.LPM_Locstock         (SUM SOH per StoreID + DivCode)
///   - LPMSIM.dbo.LPM_Ex2ItemSOH      (Ex2SOH + boxsoh + r1whsoh per Country + DivCode)
///   - LPMSIM.dbo.lpm_salestgtwk_stores (Week Sales — SalesTgtWk summed over next N wks)
///   - LPMSIM.dbo.WmsCountryOtsWeeks  (SIM country -> weeks-to-include)
///   - Azure WMS.dbo.WMS_ContAllocationData (Counting WIP — approved not completed)
///   - Azure WMS.dbo.WmsBuildingCompletion (exclude completed contnos from WIP)
/// </summary>
public class OtsPoAllocationService(IOnPremConnectionResolver resolver)
{
    private const int CommandTimeoutSeconds = 300;

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(resolver.GetOnPremBackupConnectionString());
        c.Open();
        return c;
    }

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(resolver.GetWmsAzureConnectionString());
        c.Open();
        return c;
    }

    /// <summary>Distinct (Month, Year) pairs present on LPM_EOM_Output — used to
    /// populate the Month + Year pickers.</summary>
    public async Task<List<OtsMonthYearOption>> GetAvailableMonthYearsAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<OtsMonthYearOption>(new CommandDefinition(@"
            SELECT DISTINCT Month1 AS Month, Year1 AS Year
              FROM dbo.LPM_EOM_Output WITH (NOLOCK)
             WHERE Month1 IS NOT NULL AND Year1 IS NOT NULL
             ORDER BY Year DESC, Month DESC",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Distinct SIM countries present on LPM_EOM_Output — used for the
    /// country dropdown on the report page.</summary>
    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT Country
              FROM dbo.LPM_EOM_Output WITH (NOLOCK)
             WHERE Country IS NOT NULL AND LTRIM(RTRIM(Country)) <> ''
             ORDER BY Country",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Main query — builds all 17 columns for the report.</summary>
    public async Task<List<OtsPoAllocationRow>> GenerateAsync(
        int month, int year, string? country, CancellationToken ct = default)
    {
        // 1) Base rows + on-prem-side joins in one round trip.
        //
        // All the on-prem joins live on OnPremBackup so a single query can pull
        // the whole grid minus the Azure-side Counting-WIP column. Sub-selects
        // per store keep the CTE readable and let SQL choose independent plans
        // for each aggregate.
        //
        // WeekSales: pick the "current wk" from a small subquery (MIN(wk) where
        // start-date <= today) so the same value is used everywhere in the
        // outer query. Then sum SalesTgtWk over [currentWk, currentWk + N - 1].
        var sql = @"
            DECLARE @today DATE = CAST(DATEADD(hour, 4, SYSUTCDATETIME()) AS DATE);

            ;WITH weeks AS (
                SELECT SimCountry, Weeks FROM dbo.WmsCountryOtsWeeks WITH (NOLOCK)
            ),
            storeCount AS (
                SELECT Country, COUNT(DISTINCT StoreID) AS StoreCnt
                  FROM dbo.LPM_EOM_Output WITH (NOLOCK)
                 WHERE Month1 = @month AND Year1 = @year
                 GROUP BY Country
            ),
            currentWk AS (
                -- Best-effort: the smallest week number whose salesTgtWk row is
                -- still valid this week. Falls back to MIN(wk) if no dating cols.
                SELECT MIN(wk) AS Wk FROM dbo.lpm_salestgtwk_stores WITH (NOLOCK)
                 WHERE wk >= (SELECT MIN(wk) FROM dbo.lpm_salestgtwk_stores WITH (NOLOCK))
            ),
            weekSales AS (
                SELECT s.StoreID, s.DivCode,
                       SUM(ISNULL(s.SalesTgtWk, 0)) AS WeekSales
                  FROM dbo.lpm_salestgtwk_stores s WITH (NOLOCK)
                  CROSS JOIN currentWk cw
                  CROSS JOIN dbo.LPM_EOM_Output e
                 WHERE e.Month1 = @month AND e.Year1 = @year
                   AND s.StoreID = e.StoreID AND s.DivCode = e.DivCode
                   AND (@ct IS NULL OR e.Country = @ct)
                   AND s.wk >= cw.Wk
                   AND s.wk <  cw.Wk + (SELECT ISNULL(Weeks, 1) FROM weeks WHERE SimCountry = e.Country)
                 GROUP BY s.StoreID, s.DivCode
            ),
            soh AS (
                SELECT StoreID, DivCode, SUM(ISNULL(SOH, 0)) AS SOHToday
                  FROM Racks.dbo.LPM_Locstock WITH (NOLOCK)
                 GROUP BY StoreID, DivCode
            ),
            ex2 AS (
                SELECT Country, DivCode,
                       SUM(ISNULL(Ex2SOH, 0) + ISNULL(boxsoh, 0)) AS InTransitTotal,
                       SUM(ISNULL(r1whsoh, 0))                     AS Ex2DcTotal
                  FROM dbo.LPM_Ex2ItemSOH WITH (NOLOCK)
                 GROUP BY Country, DivCode
            )
            SELECT
                e.Country,
                e.StoreID,
                d.PBFullname AS StoreName,
                e.DivCode,
                dv.Division AS Division,
                e.VolumeGroup,
                e.PriorityRank,
                e.MerchNeedMonth AS TgtEOM,
                ISNULL(w.Weeks, 1) AS WeeksToInclude,
                ISNULL(soh.SOHToday, 0) AS SOHToday,
                ISNULL(ws.WeekSales, 0)  AS WeekSales,
                CASE
                    WHEN UPPER(e.Country) = 'UAE' THEN 0
                    WHEN sc.StoreCnt > 0 AND ex2.InTransitTotal IS NOT NULL
                        THEN ex2.InTransitTotal / sc.StoreCnt
                    ELSE 0
                END AS InTransit,
                CASE
                    WHEN sc.StoreCnt > 0 AND ex2.Ex2DcTotal IS NOT NULL
                        THEN ex2.Ex2DcTotal / sc.StoreCnt
                    ELSE 0
                END AS Ex2DcSoh
              FROM dbo.LPM_EOM_Output e WITH (NOLOCK)
              LEFT JOIN LPMSIM.dbo.Divisions dv WITH (NOLOCK) ON dv.DivCode = e.DivCode
              LEFT JOIN bfldata.dbo.DataSettings d WITH (NOLOCK) ON d.StoreID = e.StoreID
              LEFT JOIN weeks w  ON w.SimCountry = e.Country
              LEFT JOIN storeCount sc ON sc.Country = e.Country
              LEFT JOIN weekSales ws ON ws.StoreID = e.StoreID AND ws.DivCode = e.DivCode
              LEFT JOIN soh      ON soh.StoreID   = e.StoreID  AND soh.DivCode = e.DivCode
              LEFT JOIN ex2      ON ex2.Country   = e.Country  AND ex2.DivCode = e.DivCode
             WHERE e.Month1 = @month AND e.Year1 = @year
               AND (@ct IS NULL OR e.Country = @ct)
             ORDER BY e.Country, e.StoreID, e.DivCode";

        List<OnPremRow> onPremRows;
        try
        {
            await using var c = OpenOnPremBackup();
            onPremRows = (await c.QueryAsync<OnPremRow>(new CommandDefinition(
                sql, new { month, year, ct = country },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"On-prem OTS query failed. If a column name doesn't match the actual schema (e.g. LPM_Locstock.SOH, LPM_Ex2ItemSOH.Ex2SOH/boxsoh/r1whsoh, lpm_salestgtwk_stores.wk/SalesTgtWk, LPMSIM.dbo.Divisions.Division), the SELECT above needs the real column names. Underlying error: {ex.Message}", ex);
        }

        if (onPremRows.Count == 0) return new();

        // 2) Counting WIP on Azure — SUM(AllocatedQty) per (Country, StoreID, DivCode)
        //    for contnos with an approved LPMSIM header but no WmsBuildingCompletion.
        Dictionary<(string, string, int), int> wipByKey;
        try
        {
            HashSet<string> approvedContnos;
            await using (var opb = OpenOnPremBackup())
            {
                approvedContnos = (await opb.QueryAsync<string>(new CommandDefinition(@"
                    SELECT DISTINCT ContNo
                      FROM dbo.WMS_Cont_Allocation_Header WITH (NOLOCK)
                     WHERE ApprovedDt IS NOT NULL",
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            HashSet<string> completedContnos;
            List<(string Country, string StoreID, int DivCode, int Qty, string ContNo)> allocs;
            await using (var w = OpenWms())
            {
                completedContnos = (await w.QueryAsync<string>(new CommandDefinition(@"
                    SELECT DISTINCT ContNo FROM dbo.WmsBuildingCompletion WITH (NOLOCK)",
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToHashSet(StringComparer.OrdinalIgnoreCase);

                allocs = (await w.QueryAsync<(string Country, string StoreID, int DivCode, int Qty, string ContNo)>(
                    new CommandDefinition(@"
                        SELECT Country, StoreID, DivCode = ISNULL(DivCode, 0),
                               Qty = SUM(ISNULL(AllocatedQty, 0)), ContNo
                          FROM dbo.WMS_ContAllocationData WITH (NOLOCK)
                         WHERE StoreID IS NOT NULL
                         GROUP BY Country, StoreID, DivCode, ContNo",
                    commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
            }

            wipByKey = allocs
                .Where(a => approvedContnos.Contains(a.ContNo) && !completedContnos.Contains(a.ContNo))
                .GroupBy(a => (a.Country ?? "", a.StoreID, a.DivCode))
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));
        }
        catch
        {
            wipByKey = new();
        }

        // 3) Merge + compute OTS Qty / OTS %.
        var eomLabel = new DateTime(year, month, 1).ToString("MMM-yyyy");
        var results = new List<OtsPoAllocationRow>(onPremRows.Count);
        foreach (var r in onPremRows)
        {
            var wip = wipByKey.TryGetValue((r.Country, r.StoreID, r.DivCode), out var v) ? v : 0;
            var otsQty = r.TgtEOM + r.WeekSales - r.SOHToday - r.InTransit - r.Ex2DcSoh - wip;
            var otsPct = r.TgtEOM > 0 ? (double)otsQty / r.TgtEOM * 100.0 : 0.0;
            results.Add(new OtsPoAllocationRow(
                Country:         r.Country,
                StoreID:         r.StoreID,
                StoreName:       r.StoreName,
                DivCode:         r.DivCode,
                Division:        r.Division,
                VolumeGroup:     r.VolumeGroup,
                PriorityRank:    r.PriorityRank,
                EOMMonth:        eomLabel,
                TgtEOM:          r.TgtEOM,
                SOHToday:        r.SOHToday,
                WeeksToInclude:  r.WeeksToInclude,
                WeekSales:       r.WeekSales,
                InTransit:       r.InTransit,
                Ex2DcSoh:        r.Ex2DcSoh,
                CountingWIP:     wip,
                OtsQtyToday:     otsQty,
                OtsPercentToday: otsPct));
        }
        return results;
    }

    private sealed class OnPremRow
    {
        public string   Country        { get; set; } = "";
        public string   StoreID        { get; set; } = "";
        public string?  StoreName      { get; set; }
        public int      DivCode        { get; set; }
        public string?  Division       { get; set; }
        public string?  VolumeGroup    { get; set; }
        public int?     PriorityRank   { get; set; }
        public int      TgtEOM         { get; set; }
        public int      WeeksToInclude { get; set; }
        public int      SOHToday       { get; set; }
        public int      WeekSales      { get; set; }
        public int      InTransit      { get; set; }
        public int      Ex2DcSoh       { get; set; }
    }
}
