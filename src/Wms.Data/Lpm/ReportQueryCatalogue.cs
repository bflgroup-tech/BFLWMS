namespace Wms.Data.Lpm;

/// <summary>
/// Backs the "Report Assistant" chat tool (Admin-only) -- a static, in-memory catalogue
/// mapping (Report, Subsection) to the exact SQL query that produces it, extracted verbatim
/// from the corresponding service method at the time each entry was added. This is
/// documentation, not a live query path -- nothing here executes; it exists purely so an
/// admin can ask "what query backs X" without reading source. Entries are grouped by report
/// in the same order those reports appear under the "Reports" menu group in MenuKeys.cs.
/// Keep entries verbatim against their source method when either changes.
/// </summary>
public static class ReportQueryCatalogue
{
    public sealed record QueryEntry(string Report, string Subsection, string SourceNote, string Sql);

    public static readonly List<QueryEntry> All = new()
    {
        // ============================== Pending Goods Receipt ==============================
        new QueryEntry("Pending Goods Receipt", "Pending Goods Receipt (containers with no GRN)", "bfldata.dbo.BuildingCompletion, usa.dbo.usapurchase, Online.dbo.PhotoCheckingResult, bfldata.dbo.BUILDINGCOMPLETIONSumm -- CountingReportsService.GetPendingPurchaseAsync", @"
            SELECT bc.ContNo,
                   CAST(bc.Trndate AS DATE) AS CountingDate,
                   LEFT(bc.TrnTime, 8) AS CompletionTime,
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
                    WHERE up.Contno = bc.ContNo
               )
             ORDER BY AgeingDays DESC, bc.ContNo"),

        // ============================== Missing / Excess Items from Production ==============================
        new QueryEntry("Missing / Excess Items from Production", "Box Summary by Month", "dbo.WmsRptMissingExcess_BoxSummary -- ReportsService.BoxSummaryByMonthFromSnapshotAsync", @"
            SELECT CONVERT(varchar(7), ClosedDt, 120)  AS [Month],
                   COUNT(DISTINCT BoxNo)                AS BoxCount,
                   SUM(MissQty)                         AS MissQty,
                   SUM(ExcessQty)                       AS ExcessQty
              FROM dbo.WmsRptMissingExcess_BoxSummary
             WHERE Country = @c AND ClosedDt BETWEEN @from AND @to
             GROUP BY CONVERT(varchar(7), ClosedDt, 120)
             ORDER BY [Month]"),

        new QueryEntry("Missing / Excess Items from Production", "Box Summary by Month -- Export to Excel", "dbo.WmsRptMissingExcess_BoxSummary -- ReportsService.BoxSummaryFromSnapshotAsync", @"
            SELECT BoxNo, ClosedDt, ClosedBy, MissQty, ExcessQty
              FROM dbo.WmsRptMissingExcess_BoxSummary
             WHERE Country = @c AND ClosedDt BETWEEN @from AND @to
             ORDER BY ClosedBy DESC, ClosedDt DESC"),

        new QueryEntry("Missing / Excess Items from Production", "Box Detail (Excel only)", "dbo.WmsRptMissingExcess_BoxDetail -- ReportsService.BoxDetailCombinedFromSnapshotAsync", @"
            SELECT BoxNo, PreparedBy, ItemCode, Qty, QtyIssued, MissingQty, ExcessQty
              FROM dbo.WmsRptMissingExcess_BoxDetail
             WHERE Country = @c AND ClosedDt BETWEEN @from AND @to
             ORDER BY BoxNo, ItemCode"),

        new QueryEntry("Missing / Excess Items from Production", "Item Summary by Division x Department", "dbo.WmsRptMissingExcess_ItemSummary -- ReportsService.ItemSummaryByDivDeptFromSnapshotAsync", @"
            ;WITH itemAgg AS (
                SELECT Country, ItemCode, MAX(Division) AS Division, MAX(Department) AS Department,
                       SUM(MissingQty) AS MissingQty, SUM(ExcessQty) AS ExcessQty,
                       MAX(HOStock)    AS HOStock
                  FROM dbo.WmsRptMissingExcess_ItemSummary
                 WHERE Country = @c AND ClosedDt BETWEEN @from AND @to
                 GROUP BY Country, ItemCode
            )
            SELECT Division, Department,
                   SUM(MissingQty) AS MissingQty,
                   SUM(ExcessQty)  AS ExcessQty,
                   SUM(HOStock)    AS HOStock
              FROM itemAgg
             GROUP BY Division, Department
             ORDER BY Division, Department"),

        new QueryEntry("Missing / Excess Items from Production", "Item Summary by Division x Department -- Item Details Export", "dbo.WmsRptMissingExcess_ItemSummary -- ReportsService.ItemSummaryFromSnapshotAsync", @"
            ;WITH itemAgg AS (
                SELECT Country, ItemCode,
                       MAX(ItemName)   AS ItemName,
                       MAX(Division)   AS Division,
                       MAX(Department) AS Department,
                       SUM(MissingQty) AS MissingQty,
                       SUM(ExcessQty)  AS ExcessQty,
                       MAX(HOStock)    AS HOStock
                  FROM dbo.WmsRptMissingExcess_ItemSummary
                 WHERE Country = @c AND ClosedDt BETWEEN @from AND @to
                 GROUP BY Country, ItemCode
            )
            SELECT ItemCode, ItemName, Division, Department,
                   MissingQty, ExcessQty, HOStock
              FROM itemAgg
             ORDER BY ItemCode"),

        // ============================== Non-LPM WH Stock Report ==============================
        new QueryEntry("Non-LPM WH Stock Report", "Division x Country Stock (Summer/Winter)", "#NlItemDiv, racks.dbo.whboxitems / [DataName].dbo.WHBoxItemsExport, Datareporting.dbo.upc_subclass, Datareporting.dbo.subclassmaster -- ReportsService.GetNonLpmWhStockAsync", @"
IF OBJECT_ID('tempdb..#NlItemDiv') IS NOT NULL DROP TABLE #NlItemDiv;
SELECT u.itemcode, Division = MIN(sm.Division)
  INTO #NlItemDiv
  FROM Datareporting.dbo.upc_subclass    u
  INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
 WHERE u.itemcode IS NOT NULL AND u.itemcode <> ''
 GROUP BY u.itemcode;
CREATE CLUSTERED INDEX IX_NlItemDiv ON #NlItemDiv (itemcode);

-- Re-run once per country in the fixed list ('UAE','KSA','Kuwait','Qatar','Bahrain','MALAYSIA'),
-- with the FROM table below swapped per country (string-interpolated -- {whSrc}: UAE uses the
-- compile-time constant racks.dbo.whboxitems; every other country resolves [DataName].dbo.WHBoxItemsExport
-- at runtime via WhBoxItemsSource.ResolveAsync, reading bfldata.dbo.DataSettings, regex-validated
-- before being spliced into the SQL text). Shown here for country = 'UAE'.
SELECT Division = ISNULL(id.Division, '(no division)'),
       Summer = SUM(CASE WHEN UPPER(ISNULL(w.Season,'')) <> 'W'
                         THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END),
       Winter = SUM(CASE WHEN UPPER(ISNULL(w.Season,'')) =  'W'
                         THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END)
  FROM racks.dbo.whboxitems w
  LEFT JOIN #NlItemDiv id ON id.itemcode = w.ItemCode
 WHERE w.LPMDt IS NULL
   AND ISNULL(w.ShopEligible,'') <> 'E'
   AND UPPER(ISNULL(w.PalletCategory,'')) = 'ELIGIBLE'
 GROUP BY ISNULL(id.Division, '(no division)')
HAVING SUM(CAST(ISNULL(w.Qty,0) AS bigint)) <> 0
 ORDER BY Division;"),

        // ============================== LPM WH Stock Report ==============================
        new QueryEntry("LPM WH Stock Report", "LPM Allocation Summary", "dbo.LPM_WhStockSnapshot -- ReportsService.GetLpmWhStockAsync (this same query also backs Season Summary and Month Summary below -- those views are pivoted client-side from this one result set)", @"
SELECT Country,
       Division,
       Season,
       Year1  AS [Year],
       Month1 AS [Month],
       SUM(CAST(ISNULL(Qty, 0) AS bigint)) AS Qty
  FROM dbo.LPM_WhStockSnapshot WITH (NOLOCK)
 WHERE PalletCategory = @pc
   AND (@noCountryFilter = 1 OR Country IN @countries)
 GROUP BY Country, Division, Season, Year1, Month1
HAVING SUM(CAST(ISNULL(Qty, 0) AS bigint)) <> 0"),

        new QueryEntry("LPM WH Stock Report", "LPM Season Summary", "dbo.LPM_WhStockSnapshot -- ReportsService.GetLpmWhStockAsync (same query as LPM Allocation Summary; Season columns pivoted client-side)", @"
SELECT Country,
       Division,
       Season,
       Year1  AS [Year],
       Month1 AS [Month],
       SUM(CAST(ISNULL(Qty, 0) AS bigint)) AS Qty
  FROM dbo.LPM_WhStockSnapshot WITH (NOLOCK)
 WHERE PalletCategory = @pc
   AND (@noCountryFilter = 1 OR Country IN @countries)
 GROUP BY Country, Division, Season, Year1, Month1
HAVING SUM(CAST(ISNULL(Qty, 0) AS bigint)) <> 0"),

        new QueryEntry("LPM WH Stock Report", "LPM Month Summary", "dbo.LPM_WhStockSnapshot -- ReportsService.GetLpmWhStockAsync (same query as LPM Allocation Summary; per-month Summer/Winter columns pivoted client-side)", @"
SELECT Country,
       Division,
       Season,
       Year1  AS [Year],
       Month1 AS [Month],
       SUM(CAST(ISNULL(Qty, 0) AS bigint)) AS Qty
  FROM dbo.LPM_WhStockSnapshot WITH (NOLOCK)
 WHERE PalletCategory = @pc
   AND (@noCountryFilter = 1 OR Country IN @countries)
 GROUP BY Country, Division, Season, Year1, Month1
HAVING SUM(CAST(ISNULL(Qty, 0) AS bigint)) <> 0"),

        new QueryEntry("LPM WH Stock Report", "Pallet Category Filter (dropdown)", "bfldata.dbo.pallettype -- ReportsService.GetPalletCategoriesAsync", @"
SELECT DISTINCT PalletCategory
  FROM bfldata.dbo.pallettype
 WHERE PalletCategory IS NOT NULL AND PalletCategory <> ''
 ORDER BY PalletCategory"),

        new QueryEntry("LPM WH Stock Report", "Country Filter (dropdown)", "bfldata.dbo.datasettings -- ReportsService.GetCountriesAsync", @"
SELECT DISTINCT Simcountry FROM bfldata.dbo.datasettings WHERE Simcountry IS NOT NULL ORDER BY Simcountry"),

        // ============================== Production Summary Report ==============================
        new QueryEntry("Production Summary Report", "By Country", "LPMSIM.dbo.LPM_MfpTerritoryMap, LPMSIM.dbo.BFL_MFP_OUTBOUND_T1 -- ReportsService.GetMerchNeedAsync (via QueryMerchNeedAsync); runs once per selected country against the resolved Territory code, with a previous-week fallback on Sundays when the result is zero", @"
            SELECT MerchNeedMonth = CAST(ROUND(ISNULL(SUM(CASE WHEN Week IN @monthWeeks THEN merch_need ELSE 0 END), 0), 0) AS BIGINT),
                   MerchNeedWeek  = CAST(ROUND(ISNULL(SUM(CASE WHEN Week = @week THEN merch_need ELSE 0 END), 0), 0) AS BIGINT),
                   MerchNeedDay   = CAST(ROUND(ISNULL(SUM(CASE WHEN Week = @week THEN merch_need ELSE 0 END), 0) / @daysInWeek, 0) AS BIGINT)
              FROM LPMSIM.dbo.BFL_MFP_OUTBOUND_T1 WITH (NOLOCK)
             WHERE territory = @territoryCode AND Year = @year AND Week IN @monthWeeks"),

        new QueryEntry("Production Summary Report", "By Country", "LPMSIM.dbo.BFL_MFP_OUTBOUND_T1, LPMSIM.dbo.Division -- ReportsService.GetMerchNeedByDivisionAsync (via QueryMerchNeedByDivisionAsync); same territory resolution and Sunday fallback as GetMerchNeedAsync above", @"
            SELECT DivCode = d.DivCode, Division = d.Division,
                   MerchNeedMonth = CAST(ROUND(ISNULL(SUM(CASE WHEN o.Week IN @monthWeeks THEN o.merch_need ELSE 0 END), 0), 0) AS BIGINT),
                   MerchNeedWeek  = CAST(ROUND(ISNULL(SUM(CASE WHEN o.Week = @week THEN o.merch_need ELSE 0 END), 0), 0) AS BIGINT),
                   MerchNeedDay   = CAST(ROUND(ISNULL(SUM(CASE WHEN o.Week = @week THEN o.merch_need ELSE 0 END), 0) / @daysInWeek, 0) AS BIGINT)
              FROM LPMSIM.dbo.BFL_MFP_OUTBOUND_T1 o WITH (NOLOCK)
              JOIN LPMSIM.dbo.Division d WITH (NOLOCK) ON d.DivCode = o.division
             WHERE o.territory = @territoryCode AND o.Year = @year AND o.Week IN @monthWeeks
             GROUP BY d.DivCode, d.Division"),

        new QueryEntry("Production Summary Report", "By Country", "bfldata.dbo.DailyCountCategoryTrf -- ReportsService.GetTransferQtyAsync (uaeOnly:true), called from inside GetProductionCheckingAsync and cached per country as result.TransferQty; non-UAE countries instead use the vTransferDetail scalar query below", @"
            SELECT ISNULL(SUM(
                     ISNULL(HR0A,0)+ISNULL(HR1A,0)+ISNULL(HR2A,0)+ISNULL(HR3A,0)+ISNULL(HR4A,0)+
                     ISNULL(HR5A,0)+ISNULL(HR6A,0)+ISNULL(HR7A,0)+ISNULL(HR8A,0)+ISNULL(HR9A,0)+
                     ISNULL(HR10A,0)+ISNULL(HR11A,0)+ISNULL(HR12A,0)+ISNULL(HR13A,0)+ISNULL(HR14A,0)+
                     ISNULL(HR15A,0)+ISNULL(HR16A,0)+ISNULL(HR17A,0)+ISNULL(HR18A,0)+ISNULL(HR19A,0)+
                     ISNULL(HR20A,0)+ISNULL(HR21A,0)+ISNULL(HR22A,0)), 0) AS TransferQty
              FROM bfldata.dbo.DailyCountCategoryTrf WITH (NOLOCK)
             WHERE Warehouse = 'TECHNO' AND TrnDate BETWEEN @from AND @to;"),

        new QueryEntry("Production Summary Report", "By Country", "dbo.vTransferDetail (on the country's own connection) -- ReportsService.GetExportCountryTransferQtyFromVTransferDetailAsync, called for any non-UAE export country; runs per-country with that country's own TrfNo prefix code", @"
            SELECT ISNULL(SUM(Quantity), 0)
              FROM dbo.vTransferDetail WITH (NOLOCK)
             WHERE TrfDate >= @from AND TrfDate < @toExclusive AND TrfNo LIKE @trfNoPattern;"),

        new QueryEntry("Production Summary Report", "Production Checking detail/summary rows", "usa.dbo.amechecking, LPMSIM.dbo.LPMSIM_Batch (Sources -> Kind), Datareporting.dbo.upc_subclass/subclassmaster (Division), bfldata.dbo.DailyCountCategoryTrf + bfldata.dbo.DataSettings (Transfer Qty by country/date) -- ReportsService.GetProductionCheckingAsync (UAE path); one multi-result-set batch sharing #Scans/#BatchKind/#ItemDiv temp tables. Non-UAE countries run the same enrichment SQL (CountryEnrichmentQuery/CountryScanQuery) after bulk-copying scans in from that country's own connection.", @"
SET NOCOUNT ON;

IF OBJECT_ID('tempdb..#Scans')     IS NOT NULL DROP TABLE #Scans;
IF OBJECT_ID('tempdb..#BatchKind') IS NOT NULL DROP TABLE #BatchKind;
IF OBJECT_ID('tempdb..#ItemDiv')   IS NOT NULL DROP TABLE #ItemDiv;

-- 1) Materialize the amechecking slice ONCE.
SELECT
    BatchNo = CASE
                  WHEN CHARINDEX('BP(', c.CmpName) > 0
                  THEN TRY_CAST(SUBSTRING(c.CmpName,
                                          CHARINDEX('BP(', c.CmpName) + 3,
                                          CHARINDEX(')',  c.CmpName, CHARINDEX('BP(', c.CmpName))
                                          - CHARINDEX('BP(', c.CmpName) - 3) AS bigint)
                  ELSE NULL
              END,
    Itemcode = ISNULL(c.Itemcode, ''),
    ShopName = ISNULL(c.ShopName, ''),
    Contno   = ISNULL(c.Contno,   ''),
    Result   = ISNULL(c.Result, -1),
    ProductionDay = CAST(CASE
                             WHEN TRY_CAST(c.Time1 AS time) >= '06:00:00'
                                 THEN c.TrnDate
                             ELSE DATEADD(day, -1, c.TrnDate)
                         END AS date)
  INTO #Scans
  FROM usa.dbo.amechecking c
 WHERE c.TrnDate >= @fromDateOnly
   AND c.TrnDate <  @toDateExclusiveOnly
   AND CAST(c.TrnDate AS datetime) + CAST(c.Time1 AS datetime) >= @from
   AND CAST(c.TrnDate AS datetime) + CAST(c.Time1 AS datetime) <  @toExclusive;

CREATE CLUSTERED INDEX IX_Scans ON #Scans (BatchNo, Itemcode);

-- 2) Country gate. Delete wrong-country batches; keep orphans & nulls as Unknown.
DELETE s
  FROM #Scans s
  INNER JOIN LPMSIM.dbo.LPMSIM_Batch b ON b.LPMBatchNo = s.BatchNo
 WHERE b.Country <> @country;

-- 3) Per-BATCH Kind from LPMSIM_Batch.Sources.
SELECT
    b.LPMBatchNo,
    Kind = CASE
               WHEN b.Sources LIKE '%Non-LPM%'
                AND REPLACE(b.Sources, 'Non-LPM', '') LIKE '%LPM%' THEN 'Mixed'
               WHEN b.Sources LIKE '%Non-LPM%' THEN 'Non-LPM'
               WHEN b.Sources LIKE '%LPM%'     THEN 'LPM'
               ELSE 'Unknown'
           END
  INTO #BatchKind
  FROM LPMSIM.dbo.LPMSIM_Batch b
 WHERE b.LPMBatchNo IN (SELECT DISTINCT BatchNo FROM #Scans WHERE BatchNo IS NOT NULL);

CREATE CLUSTERED INDEX IX_BatchKind ON #BatchKind (LPMBatchNo);

-- 4) Division lookup.
SELECT u.itemcode,
       Division = MIN(sm.Division)
  INTO #ItemDiv
  FROM (SELECT DISTINCT Itemcode FROM #Scans WHERE Itemcode <> '') si
  INNER JOIN Datareporting.dbo.upc_subclass    u  ON u.itemcode = si.Itemcode
  INNER JOIN Datareporting.dbo.subclassmaster  sm ON sm.MH4ID   = u.MH4ID
 GROUP BY u.itemcode;

CREATE CLUSTERED INDEX IX_ItemDiv ON #ItemDiv (itemcode);

-- 5) Detailed result set.
SELECT
    s.ProductionDay,
    s.BatchNo,
    Kind     = ISNULL(bk.Kind, 'Unknown'),
    Division = ISNULL(NULLIF(idv.Division, ''), 'Unknown'),
    TotalScanned = COUNT_BIG(*),
    StoreQty     = SUM(CASE WHEN s.Result IN (0, 13) THEN 1 ELSE 0 END)
  FROM #Scans s
  LEFT JOIN #BatchKind bk ON bk.LPMBatchNo = s.BatchNo
  LEFT JOIN #ItemDiv   idv ON idv.itemcode  = s.Itemcode
 GROUP BY s.ProductionDay, s.BatchNo, ISNULL(bk.Kind, 'Unknown'), ISNULL(NULLIF(idv.Division, ''), 'Unknown')
 ORDER BY s.ProductionDay DESC,
          ISNULL(s.BatchNo, -1) DESC,
          CASE ISNULL(bk.Kind, 'Unknown') WHEN 'LPM' THEN 0 WHEN 'Non-LPM' THEN 1 WHEN 'Mixed' THEN 2 ELSE 3 END,
          ISNULL(NULLIF(idv.Division, ''), 'Unknown');

-- 6) Summary result set.
SELECT
    s.ProductionDay,
    Kind     = ISNULL(bk.Kind, 'Unknown'),
    Division = ISNULL(NULLIF(idv.Division, ''), 'Unknown'),
    TotalScanned = COUNT_BIG(*),
    StoreQty     = SUM(CASE WHEN s.Result IN (0, 13) THEN 1 ELSE 0 END),
    UaeStoreQty  = SUM(CASE WHEN s.Result IN (0, 13) AND ds.SIMCountry = 'UAE'          THEN 1 ELSE 0 END),
    OmanStoreQty = SUM(CASE WHEN s.Result IN (0, 13) AND ds.SIMCountry = 'Oman'         THEN 1 ELSE 0 END),
    Ex2StoreQty  = SUM(CASE WHEN s.Result IN (0, 13) AND ds.SIMCountry = 'Ex2Locations' THEN 1 ELSE 0 END),
    Ex2TotalScanned = COUNT_BIG(CASE WHEN ds.SIMCountry = 'Ex2Locations' THEN 1 END)
  FROM #Scans s
  LEFT JOIN #BatchKind         bk  ON bk.LPMBatchNo = s.BatchNo
  LEFT JOIN #ItemDiv           idv ON idv.itemcode  = s.Itemcode
  LEFT JOIN bfldata.dbo.DataSettings ds ON ds.ShopName = s.ShopName AND s.ShopName <> ''
 GROUP BY s.ProductionDay, ISNULL(bk.Kind, 'Unknown'), ISNULL(NULLIF(idv.Division, ''), 'Unknown')
 ORDER BY s.ProductionDay DESC,
          CASE ISNULL(bk.Kind, 'Unknown') WHEN 'LPM' THEN 0 WHEN 'Non-LPM' THEN 1 WHEN 'Mixed' THEN 2 ELSE 3 END,
          ISNULL(NULLIF(idv.Division, ''), 'Unknown');

-- 7) Overall Store Qty scalar.
SELECT OverallStoreQty = SUM(CASE WHEN Result IN (0, 13) THEN 1 ELSE 0 END) FROM #Scans;

-- 8) Per-(country, date) Transfer Qty, from bfldata.dbo.DailyCountCategoryTrf -- Ex2Locations shops
-- (KSA/QATAR/BAHRAIN/KUWAIT/MALAYSIA) ship via Warehouse='JAFZA'; UAE/Oman ship via Warehouse='TECHNO'
-- (the same TECHNO total as the overall Transfer Qty scalar above, just split by country/date).
SELECT ds.Country,
       TrfDate = CAST(d.TrnDate AS DATE),
       TransferQty = ISNULL(SUM(
                       ISNULL(d.HR0A,0)+ISNULL(d.HR1A,0)+ISNULL(d.HR2A,0)+ISNULL(d.HR3A,0)+ISNULL(d.HR4A,0)+
                       ISNULL(d.HR5A,0)+ISNULL(d.HR6A,0)+ISNULL(d.HR7A,0)+ISNULL(d.HR8A,0)+ISNULL(d.HR9A,0)+
                       ISNULL(d.HR10A,0)+ISNULL(d.HR11A,0)+ISNULL(d.HR12A,0)+ISNULL(d.HR13A,0)+ISNULL(d.HR14A,0)+
                       ISNULL(d.HR15A,0)+ISNULL(d.HR16A,0)+ISNULL(d.HR17A,0)+ISNULL(d.HR18A,0)+ISNULL(d.HR19A,0)+
                       ISNULL(d.HR20A,0)+ISNULL(d.HR21A,0)+ISNULL(d.HR22A,0)), 0)
  FROM bfldata.dbo.DailyCountCategoryTrf d WITH (NOLOCK)
  INNER JOIN bfldata.dbo.DataSettings ds ON ds.ShopName = d.ShopName AND ds.SIMCountry = 'Ex2Locations'
 WHERE d.Warehouse = 'JAFZA' AND d.TrnDate BETWEEN @transferFrom AND @transferTo
 GROUP BY ds.Country, CAST(d.TrnDate AS DATE)

UNION ALL

SELECT ds.Country,
       TrfDate = CAST(d.TrnDate AS DATE),
       TransferQty = ISNULL(SUM(
                       ISNULL(d.HR0A,0)+ISNULL(d.HR1A,0)+ISNULL(d.HR2A,0)+ISNULL(d.HR3A,0)+ISNULL(d.HR4A,0)+
                       ISNULL(d.HR5A,0)+ISNULL(d.HR6A,0)+ISNULL(d.HR7A,0)+ISNULL(d.HR8A,0)+ISNULL(d.HR9A,0)+
                       ISNULL(d.HR10A,0)+ISNULL(d.HR11A,0)+ISNULL(d.HR12A,0)+ISNULL(d.HR13A,0)+ISNULL(d.HR14A,0)+
                       ISNULL(d.HR15A,0)+ISNULL(d.HR16A,0)+ISNULL(d.HR17A,0)+ISNULL(d.HR18A,0)+ISNULL(d.HR19A,0)+
                       ISNULL(d.HR20A,0)+ISNULL(d.HR21A,0)+ISNULL(d.HR22A,0)), 0)
  FROM bfldata.dbo.DailyCountCategoryTrf d WITH (NOLOCK)
  INNER JOIN bfldata.dbo.DataSettings ds ON ds.ShopName = d.ShopName
 WHERE d.Warehouse = 'TECHNO' AND d.TrnDate BETWEEN @transferFrom AND @transferTo
   AND ds.Country IN ('UAE', 'OMAN')
 GROUP BY ds.Country, CAST(d.TrnDate AS DATE);

DROP TABLE #Scans, #BatchKind, #ItemDiv;"),

        new QueryEntry("Production Summary Report", "Daily Transfer Qty by Warehouse", "BFLDATA.dbo.DataSettings -- ReportsService.GetDailyTransferByWarehouseAsync; defines the export-warehouse columns beyond UAE/TECHNO (Oman excluded -- it ships via the same TECHNO column as UAE)", @"
            SELECT Country, ShopName, Dataname, ExportCountryCode
              FROM BFLDATA.dbo.DataSettings WITH (NOLOCK)
             WHERE ExportActive = 'Y' AND ExportWH = 'Y' AND Country <> 'OMAN'"),

        new QueryEntry("Production Summary Report", "Daily Transfer Qty by Warehouse", "bfldata.dbo.DailyCountCategoryTrf -- ReportsService.GetDailyTransferForWarehouseAsync, warehouseFilter='TECHNO' for the UAE column", @"
            SELECT TrnDate,
                   TransferQty = CAST(ISNULL(SUM(
                     ISNULL(HR0A,0)+ISNULL(HR1A,0)+ISNULL(HR2A,0)+ISNULL(HR3A,0)+ISNULL(HR4A,0)+
                     ISNULL(HR5A,0)+ISNULL(HR6A,0)+ISNULL(HR7A,0)+ISNULL(HR8A,0)+ISNULL(HR9A,0)+
                     ISNULL(HR10A,0)+ISNULL(HR11A,0)+ISNULL(HR12A,0)+ISNULL(HR13A,0)+ISNULL(HR14A,0)+
                     ISNULL(HR15A,0)+ISNULL(HR16A,0)+ISNULL(HR17A,0)+ISNULL(HR18A,0)+ISNULL(HR19A,0)+
                     ISNULL(HR20A,0)+ISNULL(HR21A,0)+ISNULL(HR22A,0)), 0) AS BIGINT)
              FROM bfldata.dbo.DailyCountCategoryTrf WITH (NOLOCK)
             WHERE Warehouse = @warehouseFilter AND TrnDate BETWEEN @weekStart AND @weekEnd
             GROUP BY TrnDate"),

        new QueryEntry("Production Summary Report", "Daily Transfer Qty by Warehouse", "dbo.vTransferDetail (on each export country's own connection) -- ReportsService.GetDailyExportCountryTransferFromVTransferDetailAsync; runs once per export country (Bahrain/KSA/Kuwait/Malaysia/Qatar) with that country's own TrfNo prefix code", @"
            SELECT TrfDate, Quantity = CAST(ISNULL(SUM(Quantity), 0) AS BIGINT)
              FROM dbo.vTransferDetail WITH (NOLOCK)
             WHERE TrfDate >= @weekStart AND TrfDate < @weekEndExclusive AND TrfNo LIKE @trfNoPattern
             GROUP BY TrfDate"),

        new QueryEntry("Production Summary Report", "Daily Transfer Qty by Warehouse", "LPMSIM.dbo.BFL_MFP_OUTBOUND_T1 -- ReportsService.GetDailyTransferByWarehouseAsync; Merch Target row, matched to each column via its TerritoryCode", @"
            SELECT TerritoryCode = territory, MerchNeed = CAST(SUM(merch_need) AS BIGINT)
              FROM LPMSIM.dbo.BFL_MFP_OUTBOUND_T1 WITH (NOLOCK)
             WHERE Year = @year AND Week = @week
             GROUP BY territory"),

        new QueryEntry("Production Summary Report", "Week-wise Division Comparison (Actual vs Target)", "LPMSIM.dbo.LPM_MfpTerritoryMap -- ReportsService.GetDivisionCapVsTargetAsync; Country -> Territory resolution, gates the whole section (null result = section shows \"Data not available\")", @"
        SELECT TOP 1 Territory
          FROM LPMSIM.dbo.LPM_MfpTerritoryMap WITH (NOLOCK)
         WHERE SIMCountry = @country AND IsActive = 1"),

        new QueryEntry("Production Summary Report", "Week-wise Division Comparison (Actual vs Target)", "LPMSIM.dbo.WMS_WH_MAXMIN_CAP -- ReportsService.GetDivisionCapVsTargetAsync; Max Capacity/Week column, most recently uploaded (Year, Week) row per (Warehouse, Division), summed across warehouses", @"
            ;WITH latest AS (
                SELECT Warehouse, DIVISION, MAX_CAP_WEEK,
                       ROW_NUMBER() OVER (PARTITION BY Warehouse, DIVISION ORDER BY Year DESC, Week DESC) AS rn
                  FROM LPMSIM.dbo.WMS_WH_MAXMIN_CAP WITH (NOLOCK)
                 WHERE Country = @country
            )
            SELECT Division = DIVISION, MaxCapWeek = CAST(ISNULL(SUM(MAX_CAP_WEEK), 0) AS DECIMAL(18,2))
              FROM latest WHERE rn = 1
             GROUP BY DIVISION
             ORDER BY DIVISION"),

        new QueryEntry("Production Summary Report", "Week-wise Division Comparison (Actual vs Target)", "LPMSIM.dbo.BFL_MFP_OUTBOUND_T1, LPMSIM.dbo.Division -- ReportsService.GetDivisionCapVsTargetAsync; Weekly Target column, for the exact (territory, year, week)", @"
            SELECT d.Division, Target = CAST(SUM(o.merch_need) AS DECIMAL(18,2))
              FROM LPMSIM.dbo.BFL_MFP_OUTBOUND_T1 o WITH (NOLOCK)
              JOIN LPMSIM.dbo.Division d WITH (NOLOCK) ON d.DivCode = o.division
             WHERE o.territory = @territoryCode AND o.year = @year AND o.week = @week
             GROUP BY d.Division"),

        new QueryEntry("Production Summary Report", "Week-wise Division Comparison (Actual vs Target)", "bfldata.dbo.DailyCountCategoryTrf -- ReportsService.GetDivisionCapVsTargetAsync, UAE branch (Warehouse='TECHNO'); Daily Actual per Division", @"
                SELECT Division, Day = TrnDate,
                       Quantity = CAST(ISNULL(SUM(
                     ISNULL(HR0A,0)+ISNULL(HR1A,0)+ISNULL(HR2A,0)+ISNULL(HR3A,0)+ISNULL(HR4A,0)+
                     ISNULL(HR5A,0)+ISNULL(HR6A,0)+ISNULL(HR7A,0)+ISNULL(HR8A,0)+ISNULL(HR9A,0)+
                     ISNULL(HR10A,0)+ISNULL(HR11A,0)+ISNULL(HR12A,0)+ISNULL(HR13A,0)+ISNULL(HR14A,0)+
                     ISNULL(HR15A,0)+ISNULL(HR16A,0)+ISNULL(HR17A,0)+ISNULL(HR18A,0)+ISNULL(HR19A,0)+
                     ISNULL(HR20A,0)+ISNULL(HR21A,0)+ISNULL(HR22A,0)), 0) AS BIGINT)
                  FROM bfldata.dbo.DailyCountCategoryTrf WITH (NOLOCK)
                 WHERE Warehouse = 'TECHNO' AND TrnDate >= @weekStart AND TrnDate < @weekEndExclusive
                 GROUP BY Division, TrnDate"),

        new QueryEntry("Production Summary Report", "Week-wise Division Comparison (Actual vs Target)", "BFLDATA.dbo.DataSettings -- ReportsService.GetDivisionCapVsTargetAsync, non-UAE branch; export-country lookup (Dataname/TrfNo code)", @"
                SELECT Dataname, ExportCountryCode
                  FROM BFLDATA.dbo.DataSettings WITH (NOLOCK)
                 WHERE Country = @country AND ExportActive = 'Y' AND ExportWH = 'Y'"),

        new QueryEntry("Production Summary Report", "Week-wise Division Comparison (Actual vs Target)", "dbo.vTransferDetail (on the export country's own connection) -- ReportsService.GetDivisionCapVsTargetAsync, non-UAE branch; Daily Actual per Division", @"
                        SELECT Division, Day = TrfDate,
                               Quantity = CAST(ISNULL(SUM(Quantity), 0) AS BIGINT)
                          FROM dbo.vTransferDetail WITH (NOLOCK)
                         WHERE TrfDate >= @weekStart AND TrfDate < @weekEndExclusive AND TrfNo LIKE @trfNoPattern
                         GROUP BY Division, TrfDate"),

        // ============================== Warehouse Boxes ==============================
        new QueryEntry("Warehouse Boxes", "Box Detail", "whboxitems / WHBoxItemsExport -- WarehouseBoxesService.GetBoxesAsync ({src} resolves to racks.dbo.whboxitems for UAE or [DataName].dbo.WHBoxItemsExport per country; {whereExtra}/{havingExtra} are dynamically appended filter fragments)", @"
            SELECT TOP (@top)
                   @country AS Country,
                   w.Warehouse,
                   w.PalletNo,
                   w.BoxNo,
                   w.PalletType,
                   pt.TypeName,
                   pt.PalletCategory,
                   SUM(CAST(w.Qty AS bigint))                                              AS Qty,
                   MAX(w.LPM)                                                              AS LPM,
                   MAX(scm.Division)                                                       AS Division,
                   MAX(scm.Department)                                                     AS Department,
                   MAX(w.Brand)                                                            AS Brand,
                   MAX(w.Rack)                                                             AS Rack,
                   MAX(CASE WHEN w.PurDate IS NOT NULL OR up.Contno IS NOT NULL THEN 'Y' ELSE NULL END) AS Purchased,
                   MAX(COALESCE(w.PurDate, up.Trndate))                                    AS PurchaseDate,
                   MAX(w.ContNo)                                                           AS ContNo,
                   MAX(w.TrnDate)                                                          AS TrnDate,
                   MAX(w.CurrDate)                                                         AS CurrDate,
                   SUM(CASE WHEN UPPER(ISNULL(w.Season,'')) = 'W' THEN 0 ELSE CAST(w.Qty AS bigint) END) AS SummerQty,
                   SUM(CASE WHEN UPPER(ISNULL(w.Season,'')) = 'W' THEN CAST(w.Qty AS bigint) ELSE 0 END) AS WinterQty,
                   MAX(CAST(w.OraPoNo AS varchar(100)))                                    AS OraPoNo
              FROM {src} w
              LEFT JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
              OUTER APPLY (
                  SELECT TOP 1 sm.Division, sm.Department
                    FROM Datareporting.dbo.upc_subclass    u
                    INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
                   WHERE u.itemcode = w.ItemCode
                   ORDER BY sm.Division
              ) scm
              LEFT JOIN (
                  -- USAPurchase fallback: whboxitems.PurDate lags same-day purchases,
                  -- so recent (last 7 days) purchase records fill that gap.
                  SELECT Contno, GroupCode, MAX(Trndate) AS Trndate
                    FROM USA.dbo.USAPurchase
                   WHERE Trndate >= DATEADD(day, -7, SYSUTCDATETIME())
                   GROUP BY Contno, GroupCode
              ) up ON up.Contno = w.ContNo AND up.GroupCode = w.GroupCode
             WHERE 1 = 1
               {whereExtra}
               AND (@lpmStatus = 0
                    OR (@lpmStatus = 1 AND w.LPMDt IS NOT NULL)
                    OR (@lpmStatus = 2 AND w.LPMDt IS NULL))
               AND (@search IS NULL OR w.PalletNo LIKE @searchLike OR w.BoxNo LIKE @searchLike)
               AND (@includeNonPurchased = 1 OR w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
             GROUP BY w.Warehouse, w.PalletNo, w.BoxNo, w.PalletType, pt.TypeName, pt.PalletCategory
            HAVING 1 = 1 {havingExtra}
               AND (@mixedSeasonOnly = 0
                    OR (SUM(CASE WHEN UPPER(ISNULL(w.Season,'')) = 'W' THEN 0 ELSE CAST(w.Qty AS bigint) END) > 0
                    AND SUM(CASE WHEN UPPER(ISNULL(w.Season,'')) = 'W' THEN CAST(w.Qty AS bigint) ELSE 0 END) > 0))
             ORDER BY w.Warehouse, w.PalletNo, w.BoxNo"),

        new QueryEntry("Warehouse Boxes", "Division Summary", "whboxitems / WHBoxItemsExport, pallettype, upc_subclass, subclassmaster -- WarehouseBoxesService.GetDivisionSummaryAsync (via SummarySelect)", @"
            DECLARE @nextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(DATEADD(hour, 4, SYSUTCDATETIME())), MONTH(DATEADD(hour, 4, SYSUTCDATETIME())), 1));
            SELECT sm.Division,
                   SUM(CASE WHEN w.LPMDt IS NOT NULL AND w.LPMDt <  @nextMonthStart THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS LPMCurrentQty,
                   SUM(CASE WHEN w.LPMDt IS NOT NULL AND w.LPMDt >= @nextMonthStart THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS LPMFutureQty,
                   SUM(CASE WHEN w.LPMDt IS NULL                                    THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS NonLPMQty
              FROM {src} w
              INNER JOIN bfldata.dbo.pallettype          pt ON pt.PalletType = w.PalletType
              INNER JOIN Datareporting.dbo.upc_subclass    u ON u.itemcode    = w.ItemCode
              INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID      = u.MH4ID
             WHERE 1 = 1
               {whereExtra}
               AND (@lpmStatus = 0
                    OR (@lpmStatus = 1 AND w.LPMDt IS NOT NULL)
                    OR (@lpmStatus = 2 AND w.LPMDt IS NULL))
               AND (@search IS NULL OR w.PalletNo LIKE @searchLike OR w.BoxNo LIKE @searchLike)
               AND (@includeNonPurchased = 1 OR w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
             GROUP BY sm.Division
             ORDER BY sm.Division"),

        new QueryEntry("Warehouse Boxes", "Department Summary", "whboxitems / WHBoxItemsExport, pallettype, upc_subclass, subclassmaster -- WarehouseBoxesService.GetDepartmentSummaryAsync (via SummarySelect)", @"
            DECLARE @nextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(DATEADD(hour, 4, SYSUTCDATETIME())), MONTH(DATEADD(hour, 4, SYSUTCDATETIME())), 1));
            SELECT sm.Division, sm.Department,
                   SUM(CASE WHEN w.LPMDt IS NOT NULL AND w.LPMDt <  @nextMonthStart THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS LPMCurrentQty,
                   SUM(CASE WHEN w.LPMDt IS NOT NULL AND w.LPMDt >= @nextMonthStart THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS LPMFutureQty,
                   SUM(CASE WHEN w.LPMDt IS NULL                                    THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS NonLPMQty
              FROM {src} w
              INNER JOIN bfldata.dbo.pallettype          pt ON pt.PalletType = w.PalletType
              INNER JOIN Datareporting.dbo.upc_subclass    u ON u.itemcode    = w.ItemCode
              INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID      = u.MH4ID
             WHERE 1 = 1
               {whereExtra}
               AND (@lpmStatus = 0
                    OR (@lpmStatus = 1 AND w.LPMDt IS NOT NULL)
                    OR (@lpmStatus = 2 AND w.LPMDt IS NULL))
               AND (@search IS NULL OR w.PalletNo LIKE @searchLike OR w.BoxNo LIKE @searchLike)
               AND (@includeNonPurchased = 1 OR w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
             GROUP BY sm.Division, sm.Department
             ORDER BY sm.Division, sm.Department"),

        new QueryEntry("Warehouse Boxes", "Brand Summary", "whboxitems / WHBoxItemsExport, pallettype, upc_subclass, subclassmaster -- WarehouseBoxesService.GetBrandSummaryAsync (via SummarySelect)", @"
            DECLARE @nextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(DATEADD(hour, 4, SYSUTCDATETIME())), MONTH(DATEADD(hour, 4, SYSUTCDATETIME())), 1));
            SELECT sm.Division, sm.Department, w.Brand AS Brand,
                   SUM(CASE WHEN w.LPMDt IS NOT NULL AND w.LPMDt <  @nextMonthStart THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS LPMCurrentQty,
                   SUM(CASE WHEN w.LPMDt IS NOT NULL AND w.LPMDt >= @nextMonthStart THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS LPMFutureQty,
                   SUM(CASE WHEN w.LPMDt IS NULL                                    THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS NonLPMQty
              FROM {src} w
              INNER JOIN bfldata.dbo.pallettype          pt ON pt.PalletType = w.PalletType
              INNER JOIN Datareporting.dbo.upc_subclass    u ON u.itemcode    = w.ItemCode
              INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID      = u.MH4ID
             WHERE 1 = 1
               {whereExtra}
               AND (@lpmStatus = 0
                    OR (@lpmStatus = 1 AND w.LPMDt IS NOT NULL)
                    OR (@lpmStatus = 2 AND w.LPMDt IS NULL))
               AND (@search IS NULL OR w.PalletNo LIKE @searchLike OR w.BoxNo LIKE @searchLike)
               AND (@includeNonPurchased = 1 OR w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
             GROUP BY sm.Division, sm.Department, w.Brand
             ORDER BY sm.Division, sm.Department, w.Brand"),

        new QueryEntry("Warehouse Boxes", "Country Summary", "whboxitems / WHBoxItemsExport -- WarehouseBoxesService.GetCountrySummaryAsync (run once per country in bfldata.dbo.DataSettings; {SRC} = resolved source table per country)", @"
            DECLARE @nextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(DATEADD(hour, 4, SYSUTCDATETIME())), MONTH(DATEADD(hour, 4, SYSUTCDATETIME())), 1));
            SELECT
                CASE WHEN UPPER(ISNULL(w.Season,'')) = 'W' THEN 'Winter' ELSE 'Summer' END                                        AS Season,
                SUM(CASE WHEN w.LPMDt IS NOT NULL AND w.LPMDt <  @nextMonthStart THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS LPMCurrentQty,
                SUM(CASE WHEN w.LPMDt IS NOT NULL AND w.LPMDt >= @nextMonthStart THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS LPMFutureQty,
                SUM(CASE WHEN w.LPMDt IS NULL                                    THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS NonLPMQty,
                SUM(CAST(ISNULL(w.Qty,0) AS bigint))                                                                              AS TotalQty
              FROM {SRC} w
             WHERE w.PalletCategory = 'ELIGIBLE'
               AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
             GROUP BY CASE WHEN UPPER(ISNULL(w.Season,'')) = 'W' THEN 'Winter' ELSE 'Summer' END"),

        // ============================== Transfer/GIN/GRN History ==============================
        new QueryEntry("Transfer/GIN/GRN History", "Detail Rows", "transferheader, vTransferDetail, vGoodsIssue(plt), GRNHeaderRF, TransferReverse -- TransferGinGrnService.BuildSqlOnPrem (OnPremBackup, UAE/historical) + BuildSqlCountry (regional, today-only, non-UAE)", @"
-- Variant A: BuildSqlOnPrem (OnPremBackup path -- UAE always, other countries' historical/before-today portion)
SELECT ROW_NUMBER() OVER (ORDER BY a.TrfDate, a.TrfNo, c.SrNo) SrNo,
       @shopName ShopName,
       a.TrfNo,
       a.TrfDate,
       Quantity  = CAST(ISNULL((SELECT SUM(Quantity) FROM [{s.DataName}]..vTransferDetail WHERE TrfNo = a.TrfNo), 0) AS INT),
       PalletNo  = (SELECT TOP 1 PalletNo FROM {buildTable} WHERE TrfNo = a.TrfNo AND EntryDate >= a.TrfDate ORDER BY PalletNo DESC),
       b.EntryDate BuildDate,
       CAST(c.SrNo AS nvarchar(50)) GINNo,
       c.EntryDate GINDate,
       CAST(d.EntryNo AS nvarchar(50)) GRNNo,
       d.EntryDate  GRNDate,
       ISNULL(f.Remarks, '') Remarks
  FROM [{s.DataName}]..transferheader           a
  LEFT JOIN (
      SELECT TrfNo, PalletNo, EntryDate,
             ROW_NUMBER() OVER (PARTITION BY TrfNo ORDER BY PalletNo DESC) rn
        FROM {buildTable}
  )                                            b  ON b.TrfNo = a.TrfNo AND b.rn = 1 AND b.EntryDate >= a.TrfDate
  LEFT JOIN {ginTable}                         c  ON c.TrfNo = a.TrfNo AND c.EntryDate >= a.TrfDate
  LEFT JOIN [{s.DataName}]..GRNHeaderRF          d  ON d.TrfNo = a.TrfNo AND d.EntryDate >= a.TrfDate
  LEFT JOIN [{s.DataName}]..TransferReverse      f  ON f.TrfNo = a.TrfNo
 WHERE a.TrfNo NOT LIKE 'FN%'{dateFilter}
   AND a.CostCodeTo = @costCodeTo AND a.LocCodeTo = @locCodeTo
   -- + AppendCommonFilters (WithoutPallet / WithoutGin / WithoutGrn / Search), then:
 ORDER BY a.TrfDate, a.TrfNo;

-- Variant B: BuildSqlCountry (regional-server path -- non-UAE country, TODAY's slice only)
SELECT ROW_NUMBER() OVER (ORDER BY a.TrfNo, c.SrNo) SrNo,
       e.ShopName,
       a.TrfNo,
       a.TrfDate,
       Quantity  = CAST(ISNULL((SELECT SUM(Quantity) FROM vTransferDetail WHERE TrfNo = a.TrfNo), 0) AS INT),
       PalletNo  = (SELECT TOP 1 PalletNo FROM vGoodsIssue WHERE TrfNo = a.TrfNo AND EntryDate >= a.TrfDate ORDER BY PalletNo DESC),
       b.EntryDate BuildDate,
       CAST(c.SrNo AS nvarchar(50)) GINNo,
       c.EntryDate GINDate,
       CAST(d.EntryNo AS nvarchar(50)) GRNNo,
       d.EntryDate  GRNDate,
       ISNULL(f.Remarks, '') Remarks
  FROM transferheader              a
  LEFT JOIN (
      SELECT TrfNo, PalletNo, EntryDate,
             ROW_NUMBER() OVER (PARTITION BY TrfNo ORDER BY PalletNo DESC) rn
        FROM vGoodsIssue
  )                                b  ON b.TrfNo = a.TrfNo AND b.rn = 1 AND b.EntryDate >= a.TrfDate
  LEFT JOIN vGoodsIssueplt          c  ON c.TrfNo = a.TrfNo AND c.EntryDate >= a.TrfDate
  LEFT JOIN GRNHeaderRF             d  ON d.TrfNo = a.TrfNo AND d.EntryDate >= a.TrfDate
  JOIN  BFLDATA..DataSettings       e  ON a.CostCodeTo = e.CostCodeTo
  LEFT JOIN TransferReverse         f  ON f.TrfNo = a.TrfNo
 WHERE a.TrfNo NOT LIKE 'FN%'{dateFilter}
   AND e.ShopName NOT IN (
       SELECT ShopName FROM BFLDATA..DataSettings WHERE Concept = 'Warehouse'
   )
   -- + AppendCommonFilters (Store / WithoutPallet / WithoutGin / WithoutGrn / Search), then:
 ORDER BY a.TrfDate, a.TrfNo;"),

        new QueryEntry("Transfer/GIN/GRN History", "Summary Cards (Totals)", "vTransferDetail, vGoodsIssueplt, transferheader -- TransferGinGrnService.GetCountrySummaryOnPremAsync / GetCountrySummaryRegionalAsync (TransferSummarySql + GIN query)", @"
-- Transfer stats (TransferSummarySql; {transferDetailTable} = [{dn}]..vTransferDetail on OnPremBackup, or plain vTransferDetail on the regional server for today)
SELECT COUNT(DISTINCT TrfNo) AS TransferCount, ISNULL(SUM(Quantity),0) AS TransferQty
  FROM {transferDetailTable} WITH (NOLOCK)
 WHERE TrfDate >= @from AND TrfDate <= @to
   AND (@whCostCodeTo IS NULL OR CostCodeTo <> @whCostCodeTo)
   AND (@whLocCodeTo  IS NULL OR LocCodeTo  <> @whLocCodeTo);

-- GIN stats, OnPremBackup historical path (looped per DataName; {ginTable} = BFLDATA.dbo.vGoodsIssueplt for UAE, else [{dn}]..vGoodsIssueplt)
SELECT COUNT(DISTINCT c.SrNo) AS GinCount, ISNULL(SUM(c.Qty),0) AS GinQty
  FROM {ginTable} c WITH (NOLOCK)
 WHERE c.TrfNo IN (
     SELECT TrfNo FROM [{dn}]..transferheader WITH (NOLOCK)
      WHERE TrfDate >= @from AND TrfDate <= @to
 );

-- GIN stats, regional today-only path (GetCountrySummaryRegionalAsync; non-UAE)
SELECT COUNT(DISTINCT SrNo) AS GinCount, ISNULL(SUM(Qty),0) AS GinQty
  FROM vgoodsissueplt WITH (NOLOCK)
 WHERE TrfNo IN (
     SELECT TrfNo FROM transferheader WITH (NOLOCK)
      WHERE TrfDate >= @from AND TrfDate <= @to
 );"),

        new QueryEntry("Transfer/GIN/GRN History", "By Country Breakdown", "vTransferDetail, vGoodsIssueplt, transferheader -- TransferGinGrnService.GetTransferSummaryAsync -> GetSummaryForCountryAsync (per-country loop of the same Summary Cards query pair, kept as separate rows instead of summed into one total)", @"
SELECT COUNT(DISTINCT TrfNo) AS TransferCount, ISNULL(SUM(Quantity),0) AS TransferQty
  FROM {transferDetailTable} WITH (NOLOCK)
 WHERE TrfDate >= @from AND TrfDate <= @to
   AND (@whCostCodeTo IS NULL OR CostCodeTo <> @whCostCodeTo)
   AND (@whLocCodeTo  IS NULL OR LocCodeTo  <> @whLocCodeTo);

SELECT COUNT(DISTINCT c.SrNo) AS GinCount, ISNULL(SUM(c.Qty),0) AS GinQty
  FROM {ginTable} c WITH (NOLOCK)
 WHERE c.TrfNo IN (
     SELECT TrfNo FROM [{dn}]..transferheader WITH (NOLOCK)
      WHERE TrfDate >= @from AND TrfDate <= @to
 );"),

        new QueryEntry("Transfer/GIN/GRN History", "By Store Breakdown", "vTransferDetail, vGoodsIssueplt, transferheader -- TransferGinGrnService.GetStoreSummariesAsync / GetStoreSummaryAsync -> GetOneStoreSummaryAsync (StoreTransferSummarySql + GIN query)", @"
-- Transfer stats (StoreTransferSummarySql; {transferDetailTable} = [{s.DataName}]..vTransferDetail on OnPremBackup, or plain vTransferDetail on the regional server for today)
SELECT COUNT(DISTINCT TrfNo) AS TransferCount, ISNULL(SUM(Quantity),0) AS TransferQty
  FROM {transferDetailTable} WITH (NOLOCK)
 WHERE TrfDate >= @from AND TrfDate <= @to
   AND CostCodeTo = @costCodeTo AND LocCodeTo = @locCodeTo;

-- GIN stats, scoped by the underlying transfer's own date + store (not the GIN's own EntryDate)
SELECT COUNT(DISTINCT c.SrNo) AS GinCount, ISNULL(SUM(c.Qty),0) AS GinQty
  FROM {ginTable} c WITH (NOLOCK)
 WHERE c.TrfNo IN (
     SELECT TrfNo FROM {transferHeaderTable} WITH (NOLOCK)
      WHERE TrfDate >= @from AND TrfDate <= @to
        AND CostCodeTo = @costCodeTo AND LocCodeTo = @locCodeTo
 );"),

        // ============================== Counting Completion Report ==============================
        new QueryEntry("Counting Completion Report", "Summary", "BFLDATA.dbo.BuildingCompletionSumm/Det, USA.dbo.UsaPurchase, Online.dbo.Photochecking -- ReportsService.GetCountingCompletionSummaryAsync", @"
            SET NOCOUNT ON;
            IF OBJECT_ID('tempdb..#CCBase')     IS NOT NULL DROP TABLE #CCBase;
            IF OBJECT_ID('tempdb..#CCDet')      IS NOT NULL DROP TABLE #CCDet;
            IF OBJECT_ID('tempdb..#CCPurchase') IS NOT NULL DROP TABLE #CCPurchase;
            IF OBJECT_ID('tempdb..#CCLpm')      IS NOT NULL DROP TABLE #CCLpm;
            IF OBJECT_ID('tempdb..#CCDiv')      IS NOT NULL DROP TABLE #CCDiv;
            IF OBJECT_ID('tempdb..#CCBrand')    IS NOT NULL DROP TABLE #CCBrand;
            IF OBJECT_ID('tempdb..#CCWh')       IS NOT NULL DROP TABLE #CCWh;

            SELECT s.Country,
                   s.ContNo,
                   s.Trndate           AS CountingCompletionDate,
                   s.POnumber          AS PONo,
                   s.CountingStartDate,
                   s.TotalCheckedQty   AS CountedQty,
                   s.Division
              INTO #CCBase
              FROM BFLDATA.dbo.BuildingCompletionSumm s WITH (NOLOCK)
             WHERE (@contNoFilter IS NOT NULL OR (s.Trndate >= @from AND s.Trndate < @toExclusive))
               AND (@noCountryFilter = 1 OR s.Country IN @countries)
               AND (@contNoFilter IS NULL OR s.ContNo = @contNoFilter);

            CREATE CLUSTERED INDEX IX_CCBase ON #CCBase (Country, ContNo);

            SELECT det.ContNo, det.LPMDT, det.Brand
              INTO #CCDet
              FROM BFLDATA.dbo.BuildingCompletionDet det WITH (NOLOCK)
             WHERE det.ContNo IN (SELECT DISTINCT ContNo FROM #CCBase);

            CREATE CLUSTERED INDEX IX_CCDet ON #CCDet (ContNo);

            SELECT up.ContNo, PurchaseDate = MIN(up.Trndate)
              INTO #CCPurchase
              FROM USA.dbo.UsaPurchase up WITH (NOLOCK)
             WHERE up.ContNo IN (SELECT DISTINCT ContNo FROM #CCBase)
             GROUP BY up.ContNo;

            CREATE CLUSTERED INDEX IX_CCPurchase ON #CCPurchase (ContNo);

            SELECT ContNo, LpmMonths = STRING_AGG(Lbl, ', ') WITHIN GROUP (ORDER BY SortKey)
              INTO #CCLpm
              FROM (SELECT DISTINCT ContNo,
                           Lbl = FORMAT(LPMDT, 'MMM-yyyy'),
                           SortKey = DATEFROMPARTS(YEAR(LPMDT), MONTH(LPMDT), 1)
                      FROM #CCDet WHERE LPMDT IS NOT NULL) x
             GROUP BY ContNo;
            CREATE CLUSTERED INDEX IX_CCLpm ON #CCLpm (ContNo);

            SELECT Country, ContNo, Divisions = STRING_AGG(Division, ', ') WITHIN GROUP (ORDER BY Division)
              INTO #CCDiv
              FROM (SELECT DISTINCT Country, ContNo, Division
                      FROM #CCBase WHERE Division IS NOT NULL AND Division <> '') x
             GROUP BY Country, ContNo;
            CREATE CLUSTERED INDEX IX_CCDiv ON #CCDiv (Country, ContNo);

            SELECT ContNo, Brands = STRING_AGG(Brand, ', ') WITHIN GROUP (ORDER BY Brand)
              INTO #CCBrand
              FROM (SELECT DISTINCT ContNo, Brand
                      FROM #CCDet WHERE Brand IS NOT NULL AND Brand <> '') x
             GROUP BY ContNo;
            CREATE CLUSTERED INDEX IX_CCBrand ON #CCBrand (ContNo);

            SELECT b.ContNo, wh.Warehouse
              INTO #CCWh
              FROM (SELECT DISTINCT ContNo FROM #CCBase) b
              OUTER APPLY (
                  SELECT TOP 1 p.Warehouse
                    FROM Online.dbo.Photochecking p WITH (NOLOCK, FORCESEEK)
                   WHERE p.ContNo = b.ContNo AND p.Warehouse IS NOT NULL AND p.Warehouse <> ''
              ) wh;
            CREATE CLUSTERED INDEX IX_CCWh ON #CCWh (ContNo);

            SELECT
                b.Country,
                b.ContNo,
                CountingCompletionDate = MAX(b.CountingCompletionDate),
                PurchaseDate           = MAX(p.PurchaseDate),
                PONo                   = MAX(b.PONo),
                CountingStartDate      = MIN(b.CountingStartDate),
                CountedQty             = SUM(ISNULL(b.CountedQty, 0)),
                LpmMonths              = MAX(lm.LpmMonths),
                Divisions              = MAX(dv.Divisions),
                Brands                 = MAX(br.Brands),
                Warehouse              = CASE WHEN b.Country = 'UAE' THEN ISNULL(MAX(wh.Warehouse), 'JAFZA') ELSE MAX(wh.Warehouse) END
              FROM #CCBase b
              LEFT JOIN #CCPurchase p  ON p.ContNo = b.ContNo
              LEFT JOIN #CCLpm      lm ON lm.ContNo = b.ContNo
              LEFT JOIN #CCDiv      dv ON dv.Country = b.Country AND dv.ContNo = b.ContNo
              LEFT JOIN #CCBrand    br ON br.ContNo = b.ContNo
              LEFT JOIN #CCWh       wh ON wh.ContNo = b.ContNo
             WHERE (@warehouseFilter IS NULL OR b.Country <> 'UAE' OR wh.Warehouse = @warehouseFilter)
             GROUP BY b.Country, b.ContNo
             ORDER BY b.Country, CountingCompletionDate;

            DROP TABLE #CCBase, #CCDet, #CCPurchase, #CCLpm, #CCDiv, #CCBrand, #CCWh;"),

        new QueryEntry("Counting Completion Report", "Allocation-wise Summary", "BFLDATA.dbo.BuildingCompletionSumm/Det, USA.dbo.UPCBoxHead/UPCBoxDet/UPCBarCodes/UsaPurchase, BFLDATA.dbo.PalletType, Online.dbo.Photochecking -- ReportsService.GetCountingAllocationAsync", @"
            SET NOCOUNT ON;
            IF OBJECT_ID('tempdb..#CABase')      IS NOT NULL DROP TABLE #CABase;
            IF OBJECT_ID('tempdb..#CAConts')     IS NOT NULL DROP TABLE #CAConts;
            IF OBJECT_ID('tempdb..#CADateBoxes') IS NOT NULL DROP TABLE #CADateBoxes;
            IF OBJECT_ID('tempdb..#CARaw')       IS NOT NULL DROP TABLE #CARaw;
            IF OBJECT_ID('tempdb..#CAUpcs')      IS NOT NULL DROP TABLE #CAUpcs;
            IF OBJECT_ID('tempdb..#CADetUae')    IS NOT NULL DROP TABLE #CADetUae;
            IF OBJECT_ID('tempdb..#CADetOther')  IS NOT NULL DROP TABLE #CADetOther;
            IF OBJECT_ID('tempdb..#CADet')       IS NOT NULL DROP TABLE #CADet;
            IF OBJECT_ID('tempdb..#CAPurchase')  IS NOT NULL DROP TABLE #CAPurchase;
            IF OBJECT_ID('tempdb..#CALpm')       IS NOT NULL DROP TABLE #CALpm;
            IF OBJECT_ID('tempdb..#CADiv')       IS NOT NULL DROP TABLE #CADiv;
            IF OBJECT_ID('tempdb..#CABrand')     IS NOT NULL DROP TABLE #CABrand;
            IF OBJECT_ID('tempdb..#CAWh')        IS NOT NULL DROP TABLE #CAWh;

            SELECT s.Country,
                   s.ContNo,
                   s.Trndate           AS CountingCompletionDate,
                   s.POnumber          AS PONo,
                   s.CountingStartDate,
                   s.Division
              INTO #CABase
              FROM BFLDATA.dbo.BuildingCompletionSumm s WITH (NOLOCK)
             WHERE (@contNoFilter IS NOT NULL OR (s.Trndate >= @from AND s.Trndate < @toExclusive))
               AND (@noCountryFilter = 1 OR s.Country IN @countries)
               AND (@contNoFilter IS NULL OR s.ContNo = @contNoFilter);

            CREATE CLUSTERED INDEX IX_CABase ON #CABase (Country, ContNo);

            SELECT DISTINCT ContNo INTO #CAConts FROM #CABase WHERE Country = 'UAE';
            CREATE UNIQUE CLUSTERED INDEX IX_CAConts ON #CAConts (ContNo);

            SELECT h.BoxNo, h.PalletType, h.LPMDt, h.OraPoNo,
                   ContNo = LEFT(h.BoxNo, CHARINDEX('-', h.BoxNo) - 1)
              INTO #CADateBoxes
              FROM USA.dbo.UPCBoxHead h WITH (NOLOCK)
             WHERE ((@contNoFilter IS NULL AND h.TrnDate >= @from AND h.TrnDate < @toExclusive)
                    OR (@contNoFilter IS NOT NULL AND h.BoxNo LIKE @contNoFilter + '-%'))
               AND CHARINDEX('-', h.BoxNo) > 0;
            CREATE CLUSTERED INDEX IX_CADateBoxes ON #CADateBoxes (BoxNo);
            CREATE NONCLUSTERED INDEX IX_CADateBoxes_ContNo ON #CADateBoxes (ContNo);

            SELECT dfb.ContNo, dfb.PalletType AS Pallettype, v.Qty AS CheckedQty,
                   dfb.LPMDt AS LPMDT, v.UPC
              INTO #CARaw
              FROM #CAConts b
              JOIN #CADateBoxes dfb ON dfb.ContNo = b.ContNo
              JOIN USA.dbo.UPCBoxDet v WITH (NOLOCK) ON v.BoxNo = dfb.BoxNo;

            SELECT DISTINCT UPC INTO #CAUpcs FROM #CARaw WHERE UPC IS NOT NULL AND UPC <> '';
            CREATE UNIQUE CLUSTERED INDEX IX_CAUpcs ON #CAUpcs (UPC);

            SELECT r.ContNo, r.Pallettype, r.CheckedQty, r.LPMDT, bc2.Vendor AS Brand
              INTO #CADetUae
              FROM #CARaw r
              LEFT JOIN #CAUpcs u ON u.UPC = r.UPC
              LEFT JOIN USA.dbo.UPCBarCodes bc2 WITH (NOLOCK) ON bc2.UPC = u.UPC;

            SELECT det.ContNo, det.Pallettype, det.CheckedQty, det.LPMDT, det.Brand
              INTO #CADetOther
              FROM BFLDATA.dbo.BuildingCompletionDet det WITH (NOLOCK)
             WHERE det.ContNo IN (SELECT DISTINCT ContNo FROM #CABase WHERE Country <> 'UAE');

            SELECT * INTO #CADet FROM (
                SELECT * FROM #CADetUae
                UNION ALL
                SELECT * FROM #CADetOther
            ) x;
            CREATE CLUSTERED INDEX IX_CADet ON #CADet (ContNo, Pallettype);

            SELECT up.ContNo, PurchaseDate = MIN(up.Trndate)
              INTO #CAPurchase
              FROM USA.dbo.UsaPurchase up WITH (NOLOCK)
             WHERE up.ContNo IN (SELECT DISTINCT ContNo FROM #CABase)
             GROUP BY up.ContNo;

            CREATE CLUSTERED INDEX IX_CAPurchase ON #CAPurchase (ContNo);

            SELECT ContNo, PalletType = ISNULL(Pallettype, '(none)'),
                   LpmMonths = STRING_AGG(Lbl, ', ') WITHIN GROUP (ORDER BY SortKey)
              INTO #CALpm
              FROM (SELECT DISTINCT ContNo, Pallettype,
                           Lbl = FORMAT(LPMDT, 'MMM-yyyy'),
                           SortKey = DATEFROMPARTS(YEAR(LPMDT), MONTH(LPMDT), 1)
                      FROM #CADet WHERE LPMDT IS NOT NULL) x
             GROUP BY ContNo, ISNULL(Pallettype, '(none)');
            CREATE CLUSTERED INDEX IX_CALpm ON #CALpm (ContNo, PalletType);

            SELECT Country, ContNo, Divisions = STRING_AGG(Division, ', ') WITHIN GROUP (ORDER BY Division)
              INTO #CADiv
              FROM (SELECT DISTINCT Country, ContNo, Division
                      FROM #CABase WHERE Division IS NOT NULL AND Division <> '') x
             GROUP BY Country, ContNo;
            CREATE CLUSTERED INDEX IX_CADiv ON #CADiv (Country, ContNo);

            SELECT ContNo, PalletType = ISNULL(Pallettype, '(none)'),
                   Brands = STRING_AGG(Brand, ', ') WITHIN GROUP (ORDER BY Brand)
              INTO #CABrand
              FROM (SELECT DISTINCT ContNo, Pallettype, Brand
                      FROM #CADet WHERE Brand IS NOT NULL AND Brand <> '') x
             GROUP BY ContNo, ISNULL(Pallettype, '(none)');
            CREATE CLUSTERED INDEX IX_CABrand ON #CABrand (ContNo, PalletType);

            SELECT b.ContNo, wh.Warehouse
              INTO #CAWh
              FROM (SELECT DISTINCT ContNo FROM #CABase) b
              OUTER APPLY (
                  SELECT TOP 1 p.Warehouse
                    FROM Online.dbo.Photochecking p WITH (NOLOCK, FORCESEEK)
                   WHERE p.ContNo = b.ContNo AND p.Warehouse IS NOT NULL AND p.Warehouse <> ''
              ) wh;
            CREATE CLUSTERED INDEX IX_CAWh ON #CAWh (ContNo);

            SELECT
                b.Country,
                b.ContNo,
                CountingCompletionDate = MAX(b.CountingCompletionDate),
                PurchaseDate           = MAX(p.PurchaseDate),
                PONo                   = MAX(b.PONo),
                CountingStartDate      = MIN(b.CountingStartDate),
                PalletType             = ISNULL(d.Pallettype, '(none)'),
                TypeName               = MAX(pt.TypeName),
                BuildQty               = SUM(ISNULL(d.CheckedQty, 0)),
                LpmMonths              = MAX(lm.LpmMonths),
                Divisions              = MAX(dv.Divisions),
                Brands                 = MAX(br.Brands),
                Warehouse              = CASE WHEN b.Country = 'UAE' THEN ISNULL(MAX(wh.Warehouse), 'JAFZA') ELSE MAX(wh.Warehouse) END
              FROM #CABase b
              JOIN #CADet d ON d.ContNo = b.ContNo
              LEFT JOIN #CAPurchase p  ON p.ContNo = b.ContNo
              LEFT JOIN BFLDATA.dbo.PalletType pt WITH (NOLOCK) ON pt.PalletType = d.Pallettype
              LEFT JOIN #CALpm      lm ON lm.ContNo = b.ContNo AND lm.PalletType = ISNULL(d.Pallettype, '(none)')
              LEFT JOIN #CADiv      dv ON dv.Country = b.Country AND dv.ContNo = b.ContNo
              LEFT JOIN #CABrand    br ON br.ContNo = b.ContNo AND br.PalletType = ISNULL(d.Pallettype, '(none)')
              LEFT JOIN #CAWh       wh ON wh.ContNo = b.ContNo
             WHERE (@warehouseFilter IS NULL OR b.Country <> 'UAE' OR wh.Warehouse = @warehouseFilter)
             GROUP BY b.Country, b.ContNo, ISNULL(d.Pallettype, '(none)')
            HAVING SUM(ISNULL(d.CheckedQty, 0)) > 0
             ORDER BY b.Country, b.ContNo;

            DROP TABLE #CABase, #CAConts, #CADateBoxes, #CARaw, #CAUpcs, #CADetUae, #CADetOther, #CADet, #CAPurchase, #CALpm, #CADiv, #CABrand, #CAWh;"),

        new QueryEntry("Counting Completion Report", "Detailed", "BFLDATA.dbo.BuildingCompletionSumm/Det, USA.dbo.UsaPurchase, Datareporting.dbo.vUPC_SUBCLASS, BFLDATA.dbo.PalletType, Online.dbo.Photochecking -- ReportsService.GetCountingDetailAsync", @"
            SET NOCOUNT ON;
            IF OBJECT_ID('tempdb..#CDBase')     IS NOT NULL DROP TABLE #CDBase;
            IF OBJECT_ID('tempdb..#CDPurchase') IS NOT NULL DROP TABLE #CDPurchase;
            IF OBJECT_ID('tempdb..#CDWh')       IS NOT NULL DROP TABLE #CDWh;

            SELECT DISTINCT s.Country, s.ContNo
              INTO #CDBase
              FROM BFLDATA.dbo.BuildingCompletionSumm s WITH (NOLOCK)
             WHERE (@contNoFilter IS NOT NULL OR (s.Trndate >= @from AND s.Trndate < @toExclusive))
               AND (@noCountryFilter = 1 OR s.Country IN @countries)
               AND (@contNoFilter IS NULL OR s.ContNo = @contNoFilter);

            CREATE CLUSTERED INDEX IX_CDBase ON #CDBase (ContNo);

            SELECT up.ContNo, PurchaseDate = MIN(up.Trndate)
              INTO #CDPurchase
              FROM USA.dbo.UsaPurchase up WITH (NOLOCK)
             WHERE up.ContNo IN (SELECT DISTINCT ContNo FROM #CDBase)
             GROUP BY up.ContNo;

            CREATE CLUSTERED INDEX IX_CDPurchase ON #CDPurchase (ContNo);

            SELECT b.ContNo, wh.Warehouse
              INTO #CDWh
              FROM (SELECT DISTINCT ContNo FROM #CDBase) b
              OUTER APPLY (
                  SELECT TOP 1 p.Warehouse
                    FROM Online.dbo.Photochecking p WITH (NOLOCK, FORCESEEK)
                   WHERE p.ContNo = b.ContNo AND p.Warehouse IS NOT NULL AND p.Warehouse <> ''
              ) wh;
            CREATE CLUSTERED INDEX IX_CDWh ON #CDWh (ContNo);

            SELECT
                b.Country,
                d.ContNo,
                PurchaseDate = MAX(p.PurchaseDate),
                PalletType   = ISNULL(d.Pallettype, '(none)'),
                TypeName     = MAX(pt.TypeName),
                ItemCode     = d.upc,
                ItemName     = MAX(d.itemname),
                Qty          = SUM(ISNULL(d.CheckedQty, 0)),
                LpmMonths    = FORMAT(MAX(d.LPMDT), 'MMM-yyyy'),
                Division     = MAX(sub.Division),
                Brand        = MAX(d.Brand),
                Warehouse    = CASE WHEN b.Country = 'UAE' THEN ISNULL(MAX(wh.Warehouse), 'JAFZA') ELSE MAX(wh.Warehouse) END
              FROM BFLDATA.dbo.BuildingCompletionDet d WITH (NOLOCK)
              JOIN #CDBase b ON b.ContNo = d.ContNo
              LEFT JOIN #CDPurchase p ON p.ContNo = d.ContNo
              LEFT JOIN Datareporting.dbo.vUPC_SUBCLASS sub WITH (NOLOCK) ON sub.itemcode = d.upc
              LEFT JOIN BFLDATA.dbo.PalletType pt WITH (NOLOCK) ON pt.PalletType = d.Pallettype
              LEFT JOIN #CDWh wh ON wh.ContNo = d.ContNo
             WHERE d.ContNo IN (SELECT DISTINCT ContNo FROM #CDBase)
               AND (@warehouseFilter IS NULL OR b.Country <> 'UAE' OR wh.Warehouse = @warehouseFilter)
             GROUP BY b.Country, d.ContNo, d.upc, ISNULL(d.Pallettype, '(none)')
            HAVING SUM(ISNULL(d.CheckedQty, 0)) > 0
             ORDER BY b.Country, d.ContNo;

            DROP TABLE #CDBase, #CDPurchase, #CDWh;"),

        new QueryEntry("Counting Completion Report", "Today - Summary / Allocation-wise / Detailed", "BFLDATA.dbo.BuildingCompletion, USA.dbo.UPCBoxDet/UPCBoxHead, hodata.dbo.vUSAOrder, USA.dbo.USAPriority -- CountingCompletionTodayService.RawQuerySql (shared raw fetch); the Summary/Allocation-wise/Detailed shapes are produced afterward by C# LINQ grouping (GetSummaryAsync/GetAllocationAsync/GetDetailAsync), not by three separate SQL statements", @"
        IF OBJECT_ID('tempdb..#TodayConts') IS NOT NULL DROP TABLE #TodayConts;
        IF OBJECT_ID('tempdb..#TodayDivs')  IS NOT NULL DROP TABLE #TodayDivs;

        SELECT DISTINCT bc.ContNo
          INTO #TodayConts
          FROM BFLDATA.dbo.BuildingCompletion bc WITH (NOLOCK)
         WHERE CAST(bc.Trndate AS DATE) = @today;
        CREATE UNIQUE CLUSTERED INDEX IX_TodayConts ON #TodayConts(ContNo);

        SELECT ContNo, Divisions = STRING_AGG(DivisionY, ', ')
          INTO #TodayDivs
          FROM (SELECT DISTINCT o.Refno AS ContNo, p.DivisionY
                  FROM hodata.dbo.vUSAOrder o WITH (NOLOCK)
                  JOIN #TodayConts tc ON tc.ContNo = o.Refno
                  JOIN USA.dbo.USAPriority p WITH (NOLOCK) ON p.groupCode = o.GroupCode
                 WHERE p.DivisionY IS NOT NULL AND p.DivisionY <> '') x
         GROUP BY ContNo;
        CREATE CLUSTERED INDEX IX_TodayDivs ON #TodayDivs(ContNo);

        SELECT bc.ContNo,
               ISNULL(v.UPC, '') AS UPC,
               CAST(NULL AS VARCHAR(500)) AS ItemName,
               td.Divisions AS Division,
               h.PalletType AS ResultType,
               ISNULL(v.Qty, 0) AS QtyIssue,
               h.LPMDt AS LPMDt,
               h.OraPoNo AS ORAPONo
          FROM #TodayConts bc
          LEFT JOIN USA.dbo.UPCBoxDet v WITH (NOLOCK) ON v.BoxNo LIKE bc.ContNo + '-%'
          LEFT JOIN USA.dbo.UPCBoxHead h WITH (NOLOCK) ON h.BoxNo = v.BoxNo
          LEFT JOIN #TodayDivs td ON td.ContNo = bc.ContNo;

        DROP TABLE #TodayConts, #TodayDivs;"),

        // ============================== PO Counting Report ==============================
        new QueryEntry("PO Counting Report", "Summary", "BFLDATA.dbo.BuildingCompletionSumm, BFLDATA.dbo.BuildingCompletionDet_OraPONo, HODATA.dbo.Vusaorder -- ReportsService.GetPoCountingAsync", @"
            SET NOCOUNT ON;
            IF OBJECT_ID('tempdb..#PCBase') IS NOT NULL DROP TABLE #PCBase;
            IF OBJECT_ID('tempdb..#PCDet')  IS NOT NULL DROP TABLE #PCDet;

            SELECT s.Country, s.ContNo, s.Trndate AS CountingCompletionDate,
                   s.Division, s.suppname AS Supplier,
                   s.Purchasetype AS PurchaseType, s.remarks AS Remarks,
                   s.status AS Status, s.buyer AS Buyer
              INTO #PCBase
              FROM BFLDATA.dbo.BuildingCompletionSumm s WITH (NOLOCK)
             WHERE (@skipDateFilter = 1 OR (s.Trndate >= @from AND s.Trndate < @toExclusive))
               AND (@noCountryFilter = 1 OR s.Country IN @countries)
               AND (@contNoFilter IS NULL OR s.ContNo = @contNoFilter);

            CREATE CLUSTERED INDEX IX_PCBase ON #PCBase (ContNo);

            SELECT d.ContNo, d.OraPONo,
                   OrderSheetQty   = SUM(ISNULL(d.Qty, 0)),
                   GRNQty          = SUM(ISNULL(d.CheckedQty, 0)),
                   MissingQty      = SUM(ISNULL(d.MissingQty, 0)),
                   ExcessQty       = SUM(ISNULL(d.ExcessQty, 0)),
                   ReturnToSuppQty = SUM(ISNULL(d.ReturnToSuppQty, 0)),
                   ReturnToBuyQty  = SUM(ISNULL(d.ReturnToBuyQty, 0))
              INTO #PCDet
              FROM BFLDATA.dbo.BuildingCompletionDet_OraPONo d WITH (NOLOCK)
             WHERE d.ContNo IN (SELECT ContNo FROM #PCBase)
               AND (@poFilter IS NULL OR d.OraPONo = @poFilter)
             GROUP BY d.ContNo, d.OraPONo;

            CREATE CLUSTERED INDEX IX_PCDet ON #PCDet (ContNo, OraPONo);

            SELECT vo.refno AS ContNo, vo.ORAPONo, OrderQty = SUM(ISNULL(vo.Qty, 0))
              INTO #PCOrder
              FROM HODATA.dbo.Vusaorder vo WITH (NOLOCK)
             WHERE vo.refno IN (SELECT DISTINCT ContNo FROM #PCBase)
               AND vo.ORAPONo IN (SELECT DISTINCT OraPONo FROM #PCDet)
             GROUP BY vo.refno, vo.ORAPONo;

            CREATE CLUSTERED INDEX IX_PCOrder ON #PCOrder (ContNo, ORAPONo);

            SELECT
                b.Country,
                b.ContNo,
                PONumber                = det.OraPONo,
                CountingCompletionDate  = b.CountingCompletionDate,
                b.Division,
                b.Supplier,
                OrderSheetQty           = det.OrderSheetQty,
                OrderQty                = ISNULL(ord.OrderQty, 0),
                GRNQty                  = det.GRNQty,
                ContainerFillRate       = CASE WHEN det.OrderSheetQty = 0 THEN 0 ELSE ROUND(det.GRNQty * 100.0 / det.OrderSheetQty, 2) END,
                MissingQty              = det.MissingQty,
                PctMissing              = CASE WHEN det.OrderSheetQty = 0 THEN 0 ELSE ROUND(det.MissingQty * 100.0 / det.OrderSheetQty, 2) END,
                ExcessQty               = det.ExcessQty,
                PctExcess               = CASE WHEN det.OrderSheetQty = 0 THEN 0 ELSE ROUND(det.ExcessQty  * 100.0 / det.OrderSheetQty, 2) END,
                ReturnToSuppQty         = det.ReturnToSuppQty,
                PctMissingReturn        = CASE WHEN det.OrderSheetQty = 0 THEN 0 ELSE ROUND(det.ReturnToSuppQty * 100.0 / det.OrderSheetQty, 2) END,
                ReturnToBuyQty          = det.ReturnToBuyQty,
                ErrorUnits              = CAST(det.MissingQty + det.ExcessQty AS DECIMAL(18,2)),
                ErrorRate               = CASE WHEN det.OrderSheetQty = 0 THEN 0
                                          ELSE ROUND((det.MissingQty + det.ExcessQty) * 100.0 / det.OrderSheetQty, 2) END,
                b.PurchaseType,
                b.Remarks,
                b.Status,
                b.Buyer
              FROM #PCBase b
              JOIN #PCDet det ON det.ContNo = b.ContNo
              LEFT JOIN #PCOrder ord ON ord.ContNo = det.ContNo AND ord.ORAPONo = det.OraPONo
             ORDER BY b.Country, b.ContNo, det.OraPONo;

            DROP TABLE #PCBase, #PCDet, #PCOrder;"),

        new QueryEntry("PO Counting Report", "Detailed", "BFLDATA.dbo.BuildingCompletionSumm, BFLDATA.dbo.BuildingCompletionDet_OraPONo -- ReportsService.GetPoCountingDetailAsync", @"
            SET NOCOUNT ON;
            IF OBJECT_ID('tempdb..#PDBase') IS NOT NULL DROP TABLE #PDBase;

            SELECT DISTINCT s.ContNo
              INTO #PDBase
              FROM BFLDATA.dbo.BuildingCompletionSumm s WITH (NOLOCK)
             WHERE (@skipDateFilter = 1 OR (s.Trndate >= @from AND s.Trndate < @toExclusive))
               AND (@noCountryFilter = 1 OR s.Country IN @countries)
               AND (@contNoFilter IS NULL OR s.ContNo = @contNoFilter);

            CREATE UNIQUE CLUSTERED INDEX IX_PDBase ON #PDBase (ContNo);

            SELECT d.ContNo,
                   PONumber   = d.OraPONo,
                   ItemCode   = d.upc,
                   ItemName   = d.itemname,
                   Style      = d.style,
                   PalletType = d.Pallettype,
                   Brand      = d.Brand,
                   LpmDt      = d.LPMDt,
                   Qty              = ISNULL(d.Qty, 0),
                   CheckedQty       = ISNULL(d.CheckedQty, 0),
                   MissingQty       = ISNULL(d.MissingQty, 0),
                   ExcessQty        = ISNULL(d.ExcessQty, 0),
                   ReturnToSuppQty  = ISNULL(d.ReturnToSuppQty, 0),
                   ReturnToBuyQty   = ISNULL(d.ReturnToBuyQty, 0)
              FROM BFLDATA.dbo.BuildingCompletionDet_OraPONo d WITH (NOLOCK)
             WHERE d.ContNo IN (SELECT ContNo FROM #PDBase)
               AND (@poFilter IS NULL OR d.OraPONo = @poFilter)
             ORDER BY d.ContNo, d.OraPONo, d.upc;

            DROP TABLE #PDBase;"),

        // ============================== JAFZA Production Report ==============================
        new QueryEntry("JAFZA Production Report", "Manual - Summary (Division-wise)", "Online.dbo.PhotoChecking, USA.dbo.USAPriority -- JafzaDivisionProductionService.GetSummaryAsync", @"
        SET NOCOUNT ON;
        IF OBJECT_ID('tempdb..#JafzaBase') IS NOT NULL DROP TABLE #JafzaBase;
        IF OBJECT_ID('tempdb..#JafzaDiv')  IS NOT NULL DROP TABLE #JafzaDiv;

        SELECT TrnDate, Time1, UPC, CheckedQty = COUNT(UPC), CheckedBy, GroupCode, LPMDT = LPMdt, OraPONo
          INTO #JafzaBase
          FROM Online.dbo.PhotoChecking WITH (NOLOCK)
         WHERE Warehouse = 'JAFZA'
           AND TrnDate >= @from AND TrnDate <= @to
           AND Time1 > '03:00:00'
           AND (@username IS NULL OR CheckedBy = @username)
         GROUP BY TrnDate, UPC, CheckedBy, GroupCode, Time1, LPMdt, OraPONo;

        INSERT INTO #JafzaBase (TrnDate, Time1, UPC, CheckedQty, CheckedBy, GroupCode, LPMDT, OraPONo)
        SELECT DATEADD(day, -1, TrnDate), Time1, UPC, COUNT(UPC), CheckedBy, GroupCode, LPMdt, OraPONo
          FROM Online.dbo.PhotoChecking WITH (NOLOCK)
         WHERE Warehouse = 'JAFZA'
           AND TrnDate > @from AND TrnDate <= DATEADD(day, 1, @to)
           AND Time1 <= '03:00:00'
           AND (@username IS NULL OR CheckedBy = @username)
         GROUP BY TrnDate, UPC, CheckedBy, GroupCode, Time1, LPMdt, OraPONo;

        CREATE CLUSTERED INDEX IX_JafzaBase ON #JafzaBase (UPC);

        SELECT DISTINCT GroupCode, DivisionY
          INTO #JafzaDiv
          FROM USA.dbo.USAPriority WITH (NOLOCK)
         WHERE GroupCode IN (SELECT DISTINCT GroupCode FROM #JafzaBase);

        CREATE CLUSTERED INDEX IX_JafzaDiv ON #JafzaDiv (GroupCode);

            SELECT
                b.TrnDate,
                Division   = d.DivisionY,
                Username   = b.CheckedBy,
                CheckedQty = SUM(b.CheckedQty)
              FROM #JafzaBase b
              JOIN #JafzaDiv d ON d.GroupCode = b.GroupCode
             WHERE ISNULL(d.DivisionY, '') <> ''
             GROUP BY b.TrnDate, d.DivisionY, b.CheckedBy
             ORDER BY b.TrnDate, Division, Username;

            DROP TABLE #JafzaBase, #JafzaDiv;"),

        new QueryEntry("JAFZA Production Report", "Manual - Detailed (Item-wise)", "Online.dbo.PhotoChecking, USA.dbo.USAPriority -- JafzaDivisionProductionService.GetDetailAsync", @"
        SET NOCOUNT ON;
        IF OBJECT_ID('tempdb..#JafzaBase') IS NOT NULL DROP TABLE #JafzaBase;
        IF OBJECT_ID('tempdb..#JafzaDiv')  IS NOT NULL DROP TABLE #JafzaDiv;

        SELECT TrnDate, Time1, UPC, CheckedQty = COUNT(UPC), CheckedBy, GroupCode, LPMDT = LPMdt, OraPONo
          INTO #JafzaBase
          FROM Online.dbo.PhotoChecking WITH (NOLOCK)
         WHERE Warehouse = 'JAFZA'
           AND TrnDate >= @from AND TrnDate <= @to
           AND Time1 > '03:00:00'
           AND (@username IS NULL OR CheckedBy = @username)
         GROUP BY TrnDate, UPC, CheckedBy, GroupCode, Time1, LPMdt, OraPONo;

        INSERT INTO #JafzaBase (TrnDate, Time1, UPC, CheckedQty, CheckedBy, GroupCode, LPMDT, OraPONo)
        SELECT DATEADD(day, -1, TrnDate), Time1, UPC, COUNT(UPC), CheckedBy, GroupCode, LPMdt, OraPONo
          FROM Online.dbo.PhotoChecking WITH (NOLOCK)
         WHERE Warehouse = 'JAFZA'
           AND TrnDate > @from AND TrnDate <= DATEADD(day, 1, @to)
           AND Time1 <= '03:00:00'
           AND (@username IS NULL OR CheckedBy = @username)
         GROUP BY TrnDate, UPC, CheckedBy, GroupCode, Time1, LPMdt, OraPONo;

        CREATE CLUSTERED INDEX IX_JafzaBase ON #JafzaBase (UPC);

        SELECT DISTINCT GroupCode, DivisionY
          INTO #JafzaDiv
          FROM USA.dbo.USAPriority WITH (NOLOCK)
         WHERE GroupCode IN (SELECT DISTINCT GroupCode FROM #JafzaBase);

        CREATE CLUSTERED INDEX IX_JafzaDiv ON #JafzaDiv (GroupCode);

            SELECT
                b.TrnDate,
                b.UPC,
                Username   = b.CheckedBy,
                b.GroupCode,
                Division   = d.DivisionY,
                CheckedQty = SUM(b.CheckedQty),
                Lpmdt      = b.LPMDT,
                OraPoNo    = b.OraPONo
              FROM #JafzaBase b
              JOIN #JafzaDiv d ON d.GroupCode = b.GroupCode
             WHERE ISNULL(d.DivisionY, '') <> ''
             GROUP BY b.TrnDate, b.UPC, b.CheckedBy, b.GroupCode, d.DivisionY, b.LPMDT, b.OraPONo
             ORDER BY b.TrnDate, b.UPC;

            DROP TABLE #JafzaBase, #JafzaDiv;"),

        new QueryEntry("JAFZA Production Report", "Robo - Summary (Division-wise)", "ROBOTICS.dbo.PairingConformationDetail (raw fetch) -- JafzaRoboProductionService.FetchRawAsync / RawQuerySql (feeds GetSummaryAsync); the Division/GroupName rollup and Summary-vs-Detailed grouping happen in C#, using this raw fetch plus the two/three enrichment queries below", @"
        ;WITH Shifted AS (
            SELECT
                TrnDate = CASE WHEN TrnTime <= '03:00:00' THEN DATEADD(day, -1, TrnDate) ELSE TrnDate END,
                ItemCode = itemcode,
                EmpCode  = username,
                Qty      = COUNT(*)
              FROM ROBOTICS.dbo.PairingConformationDetail
             WHERE TrnDate BETWEEN @from AND DATEADD(day, 1, @to)
               AND (@username IS NULL OR username = @username)
             GROUP BY CASE WHEN TrnTime <= '03:00:00' THEN DATEADD(day, -1, TrnDate) ELSE TrnDate END,
                      itemcode, username
        )
        SELECT TrnDate, ItemCode, EmpCode, Qty
          FROM Shifted
         WHERE TrnDate BETWEEN @from AND @to"),

        new QueryEntry("JAFZA Production Report", "Robo - Enrichment: GroupCode by ItemCode", "USA.dbo.UPCBarCodes -- JafzaRoboProductionService.EnrichAsync (groupRows query); feeds both Robo Summary and Detailed", @"
            SELECT CAST(value AS VARCHAR(50)) AS ItemCode INTO #jrItems FROM STRING_SPLIT(@itemCodesCsv, ',');
            CREATE UNIQUE CLUSTERED INDEX IX_jrItems_tmp ON #jrItems(ItemCode);

            SELECT b.Itemcode, GroupCode = MAX(b.GroupCode)
              FROM USA.dbo.UPCBarCodes b
              INNER JOIN #jrItems i ON i.ItemCode = b.Itemcode
             GROUP BY b.Itemcode;"),

        new QueryEntry("JAFZA Production Report", "Robo - Enrichment: Division by GroupCode", "USA.dbo.USAPriority -- JafzaRoboProductionService.EnrichAsync (divRows query); feeds both Robo Summary and Detailed", @"
                SELECT CAST(value AS VARCHAR(50)) AS GroupCode INTO #jrGroups FROM STRING_SPLIT(@groupCodesCsv, ',');
                CREATE UNIQUE CLUSTERED INDEX IX_jrGroups_tmp ON #jrGroups(GroupCode);

                SELECT DISTINCT p.GroupCode, p.DivisionY
                  FROM USA.dbo.USAPriority p
                  INNER JOIN #jrGroups g ON g.GroupCode = p.GroupCode;"),

        new QueryEntry("JAFZA Production Report", "Robo - Detailed (Item-wise) - Enrichment: GroupName by GroupCode", "hodata.dbo.itemgroup -- JafzaRoboProductionService.EnrichAsync (nameRows query, feeds GetDetailAsync only -- the Detailed view's GroupName column)", @"
                SELECT CAST(value AS VARCHAR(50)) AS GroupCode INTO #jrGroupNames FROM STRING_SPLIT(@groupCodesCsv, ',');
                CREATE UNIQUE CLUSTERED INDEX IX_jrGroupNames_tmp ON #jrGroupNames(GroupCode);

                SELECT ig.GroupCode, ig.Description
                  FROM hodata.dbo.itemgroup ig
                  INNER JOIN #jrGroupNames g ON g.GroupCode = ig.GroupCode;"),

        new QueryEntry("JAFZA Production Report", "Export - Summary (by Shop)", "BFLDATA.dbo.DailyCountCategoryTrf -- JafzaExportProductionService.GetSummaryAsync (only view for this source; no Detailed)", @"
        WITH Bucketed AS (
            SELECT TrnDate, Division, ShopName,
                   EarlyQty = SUM(HR0A + hr1a + hr2a),
                   LateQty  = SUM(hr3a + hr4a + hr5a + hr6a + hr7a + hr8a + hr9a + hr10a + hr11a + hr12a + hr13a + hr14a + hr15a + hr16a + hr17a + hr18a + hr19a + hr20a + hr21a + hr22a)
              FROM BFLDATA.dbo.DailyCountCategoryTrf WITH (NOLOCK)
             WHERE Warehouse = 'JAFZA' AND TrnDate >= @from AND TrnDate <= DATEADD(day, 1, @to)
             GROUP BY TrnDate, Division, ShopName
        )
            SELECT b.TrnDate, b.Division, b.ShopName, Qty = b.LateQty + ISNULL(n.EarlyQty, 0)
              FROM Bucketed b
              LEFT JOIN Bucketed n
                ON n.TrnDate = DATEADD(day, 1, b.TrnDate) AND n.Division = b.Division AND n.ShopName = b.ShopName
             WHERE b.TrnDate >= @from AND b.TrnDate <= @to
             ORDER BY b.TrnDate, b.Division, b.ShopName"),

        new QueryEntry("JAFZA Production Report", "Box GRN - Summary (Division-wise)", "USA.dbo.vUPCBoxDet, HODATA.dbo.ItemMaster, USA.dbo.USAPriority -- JafzaBoxGrnProductionService.FetchRawAsync / RawQuerySql (feeds GetSummaryAsync); Summary and Detailed both run off this same raw query, grouped differently in C#", @"
        SELECT
            TrnDate   = v.TrnDate,
            Time1     = v.Time1,
            BoxNo     = v.BoxNo,
            ItemCode  = v.Itemcode,
            GroupCode = im.GroupCode,
            Division  = p.DivisionY,
            Qty       = v.Qty
          FROM USA.dbo.vUPCBoxDet v WITH (NOLOCK)
          LEFT JOIN HODATA.dbo.ItemMaster im WITH (NOLOCK) ON im.ItemCode = v.Itemcode
          LEFT JOIN USA.dbo.USAPriority p WITH (NOLOCK) ON p.GroupCode = im.GroupCode
         WHERE v.WHouse = 'JAFZA' AND v.Remarks = 'Box GRN'
           AND v.TrnDate >= @from AND v.TrnDate <= @toPlusOne"),

        new QueryEntry("JAFZA Production Report", "Box GRN - Detailed (Item-wise)", "USA.dbo.vUPCBoxDet, HODATA.dbo.ItemMaster, USA.dbo.USAPriority -- JafzaBoxGrnProductionService.FetchRawAsync / RawQuerySql (feeds GetDetailAsync -- identical SQL to Box GRN Summary; grouped by ItemCode/GroupCode/Division in C# instead of just Division)", @"
        SELECT
            TrnDate   = v.TrnDate,
            Time1     = v.Time1,
            BoxNo     = v.BoxNo,
            ItemCode  = v.Itemcode,
            GroupCode = im.GroupCode,
            Division  = p.DivisionY,
            Qty       = v.Qty
          FROM USA.dbo.vUPCBoxDet v WITH (NOLOCK)
          LEFT JOIN HODATA.dbo.ItemMaster im WITH (NOLOCK) ON im.ItemCode = v.Itemcode
          LEFT JOIN USA.dbo.USAPriority p WITH (NOLOCK) ON p.GroupCode = im.GroupCode
         WHERE v.WHouse = 'JAFZA' AND v.Remarks = 'Box GRN'
           AND v.TrnDate >= @from AND v.TrnDate <= @toPlusOne"),

        // ============================== Shipment Status ==============================
        new QueryEntry("Shipment Status", "Country Filter List", "bfldata.dbo.DataSettings -- ShipmentStatusService.GetCountriesAsync", @"SELECT DISTINCT SIMCountry FROM bfldata..DataSettings
              WHERE SIMCountry NOT IN ('', 'ECOM', 'Ex2Locations', 'UAE', 'OMAN')
              ORDER BY SIMCountry"),

        new QueryEntry("Shipment Status", "GIN/BFL Flow - Header", "USA.dbo.ExportPass, bfldata..vGoodsIssueplt, bfldata.dbo.DataSettings, bfldata..contreceiptExport -- ShipmentStatusService.RunHeaderQueryAsync / GinHeaderSql", @"
        SELECT
            ep.GINNo         AS GinNo,
            ep.Trndate       AS ReleasedOn,
            ep.ETADate       AS Eta,
            ep.Shipno        AS ShipNo,
            ep.TotalQty      AS TotalQty,
            ep.TransferCount AS TransferCount,
            MIN(gi.EntryDate) AS EntryDate,
            MAX(gi.Remarks)   AS Remarks,
            cre.ReceiptDt    AS ReceiptDt
        FROM USA.dbo.ExportPass ep WITH (NOLOCK)
        JOIN bfldata..vGoodsIssueplt gi WITH (NOLOCK) ON gi.SrNo = ep.GINNo
        JOIN bfldata.dbo.DataSettings ds WITH (NOLOCK) ON ds.ShopName = gi.ShopIssue
        LEFT JOIN bfldata..contreceiptExport cre WITH (NOLOCK) ON cre.GINNO = ep.GINNo
        WHERE (@country IS NULL OR ds.Country = @country)
          AND (
                (cre.ReceiptDt IS NOT NULL AND cre.ReceiptDt >= @from AND cre.ReceiptDt <= @to)
             OR (cre.ReceiptDt IS NULL AND ep.Trndate <= @to AND ep.Trndate >= @inTransitFloor)
              )
        GROUP BY ep.GINNo, ep.Trndate, ep.ETADate, ep.Shipno, ep.TotalQty, ep.TransferCount, cre.ReceiptDt"),

        new QueryEntry("Shipment Status", "GIN/BFL Flow - Pallet/TrfNo Mapping", "USA.dbo.ExportPass, bfldata..vGoodsIssueplt, bfldata.dbo.DataSettings, bfldata..contreceiptExport -- ShipmentStatusService.RunMappingQueryAsync / GinMappingSql (feeds the Transfer Detail rollup below)", @"
        SELECT
            ep.GINNo      AS GinNo,
            gi.TrfNo      AS TrfNo,
            ds.CostCodeTo AS CostCodeTo,
            ds.LocCodeTo  AS LocCodeTo,
            ds.DataName   AS DataName
        FROM USA.dbo.ExportPass ep WITH (NOLOCK)
        JOIN bfldata..vGoodsIssueplt gi WITH (NOLOCK) ON gi.SrNo = ep.GINNo
        JOIN bfldata.dbo.DataSettings ds WITH (NOLOCK) ON ds.ShopName = gi.ShopIssue
        LEFT JOIN bfldata..contreceiptExport cre WITH (NOLOCK) ON cre.GINNO = ep.GINNo
        WHERE (@country IS NULL OR ds.Country = @country)
          AND (
                (cre.ReceiptDt IS NOT NULL AND cre.ReceiptDt >= @from AND cre.ReceiptDt <= @to)
             OR (cre.ReceiptDt IS NULL AND ep.Trndate <= @to AND ep.Trndate >= @inTransitFloor)
              )"),

        new QueryEntry("Shipment Status", "GIN/BFL Flow - Transfer Detail Division/Department/Brand Rollup", "[DataName].dbo.vTransferDetail, usa.dbo.USAPriority -- ShipmentStatusService.RunTransferDetailChunkAsync (top-5-by-qty, run per shop/chunk of TrfNos; [dataName] resolved per shop's DataSettings.DataName)", @"
                SELECT vtd.TrfNo AS ItemKey, up.DivisionY AS Division, up.Department, up.Brand, SUM(vtd.Quantity) AS Qty
                FROM [{dataName}].dbo.vTransferDetail vtd WITH (NOLOCK)
                LEFT JOIN usa.dbo.USAPriority up WITH (NOLOCK) ON up.groupCode = vtd.groupcode
                WHERE vtd.CostCodeTo = @costCodeTo AND vtd.LocCodeTo = @locCodeTo AND vtd.TrfNo IN ({BuildInClause(trfNos)})
                GROUP BY vtd.TrfNo, up.DivisionY, up.Department, up.Brand"),

        new QueryEntry("Shipment Status", "GIN/BFL Flow - Received Boxes (VerifyGin)", "[DataName].dbo.VerifyGin -- ShipmentStatusService.GetReceivedBoxesByGinAsync ([dataName] resolved via WhBoxItemsSource.ResolveDataNameAsync per country)", @"
            SELECT CAST(GinNo AS VARCHAR(20)) AS GinNo, COUNT(TrfNo) AS ReceivedBoxes
            FROM [{dataName}].dbo.VerifyGin WITH (NOLOCK)
            WHERE Verified = 'Y' AND GinNo IN @ginNos
            GROUP BY GinNo"),

        new QueryEntry("Shipment Status", "LOCAL/International Flow - Base", "bfldata..ContReceipt, bfldata..datasettings -- ShipmentStatusService.GetLocalFlowRowsAsync / LocalBaseSql (containers received directly at destination)", @"
        SELECT
            cr.ContNo    AS ShipNo,
            cr.ReceiptDt AS ReceiptDt
        FROM bfldata..ContReceipt cr WITH (NOLOCK)
        WHERE cr.RecLocation IN (
            SELECT shopname FROM bfldata..datasettings WITH (NOLOCK)
            WHERE concept = 'warehouse' AND country = @country AND shopname LIKE 'BFL%'
        )
        AND cr.ReceiptDt >= @from AND cr.ReceiptDt <= @to"),

        new QueryEntry("Shipment Status", "LOCAL/International Flow - Division/Department/Brand Rollup", "usa..usaorgfile, usa.dbo.USAPriority -- ShipmentStatusService.RunUsaOrgFileChunkAsync (top-5-by-qty, per ContNo chunk)", @"
                SELECT uo.ContNo AS ItemKey, up.DivisionY AS Division, up.Department, up.Brand, CAST(SUM(uo.orgqty) AS DECIMAL(18,2)) AS Qty
                FROM usa..usaorgfile uo WITH (NOLOCK)
                LEFT JOIN usa.dbo.USAPriority up WITH (NOLOCK) ON up.groupCode = uo.GroupCode
                WHERE uo.ContNo IN ({BuildInClause(contNos)})
                GROUP BY uo.ContNo, up.DivisionY, up.Department, up.Brand"),

        // ============================== Warehouse SOH Summary ==============================
        new QueryEntry("Warehouse SOH Summary", "Stock On Hand (UAE — TECHNO/JAFZA/YOTO)", "RACKS.dbo.WHBoxItems -- WarehouseSohSummaryService.GetStockOnHandExcludingBlackboxAsync", @"
            SELECT
                TotalQuantity     = CAST(ISNULL(SUM(qty), 0) AS BIGINT),
                TotalBoxesStock   = CAST(ISNULL(SUM(CASE WHEN BoxNo <> '' THEN qty ELSE 0 END), 0) AS BIGINT),
                NumberOfBoxes     = CAST(COUNT(DISTINCT CASE WHEN BoxNo <> '' THEN BoxNo END) AS BIGINT),
                TotalPalletsStock = CAST(ISNULL(SUM(CASE WHEN PalletNo <> '' THEN qty ELSE 0 END), 0) AS BIGINT),
                NumberOfPallets   = CAST(COUNT(DISTINCT CASE WHEN PalletNo <> '' THEN PalletNo END) AS BIGINT),
                TotalActiveSkus   = CAST(COUNT(DISTINCT ItemCode) AS BIGINT)
              FROM RACKS.dbo.WHBoxItems
             WHERE Warehouse <> 'BLACKBOX'"),

        new QueryEntry("Warehouse SOH Summary", "Stock On Hand (UAE — BlackBOX)", "RACKS.dbo.WHBoxItems -- WarehouseSohSummaryService.GetStockOnHandForBlackboxAsync", @"
            SELECT
                TotalQuantity     = CAST(ISNULL(SUM(qty), 0) AS BIGINT),
                TotalBoxesStock   = CAST(ISNULL(SUM(CASE WHEN BoxNo <> '' THEN qty ELSE 0 END), 0) AS BIGINT),
                NumberOfBoxes     = CAST(COUNT(DISTINCT CASE WHEN BoxNo <> '' THEN BoxNo END) AS BIGINT),
                TotalPalletsStock = CAST(ISNULL(SUM(CASE WHEN PalletNo <> '' THEN qty ELSE 0 END), 0) AS BIGINT),
                NumberOfPallets   = CAST(COUNT(DISTINCT CASE WHEN PalletNo <> '' THEN PalletNo END) AS BIGINT),
                TotalActiveSkus   = CAST(COUNT(DISTINCT ItemCode) AS BIGINT)
              FROM RACKS.dbo.WHBoxItems
             WHERE Warehouse = 'BLACKBOX'"),

        new QueryEntry("Warehouse SOH Summary", "Stock On Hand (non-UAE country)", "{DataName}.dbo.WHBoxItemsExport -- WarehouseSohSummaryService.GetStockOnHandForCountryAsync ({src} resolved dynamically via WhBoxItemsSource.ResolveAsync)", @"
            SELECT
                TotalQuantity     = CAST(ISNULL(SUM(Qty), 0) AS BIGINT),
                TotalBoxesStock   = CAST(ISNULL(SUM(CASE WHEN BoxNo <> '' THEN Qty ELSE 0 END), 0) AS BIGINT),
                NumberOfBoxes     = CAST(COUNT(DISTINCT CASE WHEN BoxNo <> '' THEN BoxNo END) AS BIGINT),
                TotalPalletsStock = CAST(ISNULL(SUM(CASE WHEN PalletNo <> '' THEN Qty ELSE 0 END), 0) AS BIGINT),
                NumberOfPallets   = CAST(COUNT(DISTINCT CASE WHEN PalletNo <> '' THEN PalletNo END) AS BIGINT),
                TotalActiveSkus   = CAST(COUNT(DISTINCT ItemCode) AS BIGINT)
              FROM {src}"),

        new QueryEntry("Warehouse SOH Summary", "Stock On Hand (Online)", "RACKS.dbo.MFCS_LOCSTOCK; BFLDATA.dbo.DataSettings -- WarehouseSohSummaryService.GetStockOnHandForOnlineAsync", @"
            SELECT
                TotalQuantity   = CAST(ISNULL(SUM(SOH_QTY), 0) AS BIGINT),
                TotalActiveSkus = CAST(COUNT(DISTINCT SKU) AS BIGINT)
              FROM RACKS.dbo.MFCS_LOCSTOCK
             WHERE MFCS_STOREID = @storeId"),

        new QueryEntry("Warehouse SOH Summary", "Storage Capacity (UAE — TECHNO/JAFZA/YOTO)", "racks.dbo.BinRackMaster, racks.dbo.tmpwhracks, racks.dbo.WarehouseRacks, racks.dbo.TechnoRacks, racks.dbo.BinRack, racks.dbo.WarehouseRackDet, racks.dbo.TechnoRackDet -- WarehouseSohSummaryService.GetStorageCapacityExcludingBlackboxAsync", @"
        SET NOCOUNT ON;
        IF OBJECT_ID('tempdb..#racklocation') IS NOT NULL DROP TABLE #racklocation;
        CREATE TABLE #racklocation(racktype varchar(15), warehouse varchar(15), capacity int, totalcapacity int, used int);

        INSERT INTO #racklocation SELECT 'BINRACK', warehouse, COUNT(*), 0, 0 FROM racks.dbo.BinRackMaster GROUP BY warehouse;
        INSERT INTO #racklocation SELECT 'TMPWH', 'JAFZA', COUNT(*), 0, 0 FROM racks.dbo.tmpwhracks;
        INSERT INTO #racklocation SELECT 'WAREHOUSE', warehouse, COUNT(*), 0, 0 FROM racks.dbo.WarehouseRacks GROUP BY warehouse;
        INSERT INTO #racklocation SELECT 'TECHNORACK', 'TECHNO', COUNT(*), 0, 0 FROM racks.dbo.TechnoRacks;
        INSERT INTO #racklocation VALUES('BINRACK', 'BLACKBOX', 121847, 121847, 0);

        UPDATE #racklocation SET totalcapacity = capacity WHERE racktype IN ('BINRACK','TMPWH');
        UPDATE #racklocation SET totalcapacity = capacity * 59 WHERE racktype = 'TECHNORACK';
        UPDATE #racklocation SET totalcapacity = capacity * 3 WHERE racktype = 'WAREHOUSE' AND warehouse IN ('YOTO-BU','TECHNO-E','YOTO-SF');
        UPDATE #racklocation SET totalcapacity = capacity * 100 WHERE racktype = 'WAREHOUSE' AND warehouse IN ('YOTO');
        UPDATE #racklocation SET used = (SELECT COUNT(*) FROM racks.dbo.tmpwhracks WHERE PalletNo1 <> '' OR PalletNo2 <> '') WHERE racktype = 'TMPWH';
        UPDATE #racklocation SET used = (SELECT COUNT(*) FROM racks.dbo.BinRack WHERE Warehouse = a.warehouse) FROM #racklocation a WHERE racktype = 'BINRACK';
        UPDATE #racklocation SET used = (SELECT COUNT(*) FROM racks.dbo.WarehouseRackDet WHERE Warehouse = a.warehouse) FROM #racklocation a WHERE racktype = 'WAREHOUSE';
        UPDATE #racklocation SET used = (SELECT COUNT(*) FROM racks.dbo.TechnoRackDet) WHERE racktype = 'TECHNORACK';

            SELECT
                TotalRackLocations      = CAST(ISNULL(SUM(totalcapacity), 0) AS BIGINT),
                FreeBoxRackLocations    = CAST(ISNULL(SUM(CASE WHEN racktype = 'BINRACK' THEN totalcapacity - used ELSE 0 END), 0) AS BIGINT),
                FilledBoxLocations      = CAST(ISNULL(SUM(CASE WHEN racktype = 'BINRACK' THEN used ELSE 0 END), 0) AS BIGINT),
                FreePalletRackLocations = CAST(ISNULL(SUM(CASE WHEN racktype <> 'BINRACK' THEN totalcapacity - used ELSE 0 END), 0) AS BIGINT),
                FilledPalletLocations   = CAST(ISNULL(SUM(CASE WHEN racktype <> 'BINRACK' THEN used ELSE 0 END), 0) AS BIGINT)
              FROM #racklocation
             WHERE warehouse <> 'BLACKBOX';
            DROP TABLE #racklocation;"),

        new QueryEntry("Warehouse SOH Summary", "Storage Capacity (UAE — BlackBOX)", "racks.dbo.BinRackMaster, racks.dbo.tmpwhracks, racks.dbo.WarehouseRacks, racks.dbo.TechnoRacks, racks.dbo.BinRack, racks.dbo.WarehouseRackDet, racks.dbo.TechnoRackDet -- WarehouseSohSummaryService.GetStorageCapacityForBlackboxAsync", @"
        SET NOCOUNT ON;
        IF OBJECT_ID('tempdb..#racklocation') IS NOT NULL DROP TABLE #racklocation;
        CREATE TABLE #racklocation(racktype varchar(15), warehouse varchar(15), capacity int, totalcapacity int, used int);

        INSERT INTO #racklocation SELECT 'BINRACK', warehouse, COUNT(*), 0, 0 FROM racks.dbo.BinRackMaster GROUP BY warehouse;
        INSERT INTO #racklocation SELECT 'TMPWH', 'JAFZA', COUNT(*), 0, 0 FROM racks.dbo.tmpwhracks;
        INSERT INTO #racklocation SELECT 'WAREHOUSE', warehouse, COUNT(*), 0, 0 FROM racks.dbo.WarehouseRacks GROUP BY warehouse;
        INSERT INTO #racklocation SELECT 'TECHNORACK', 'TECHNO', COUNT(*), 0, 0 FROM racks.dbo.TechnoRacks;
        INSERT INTO #racklocation VALUES('BINRACK', 'BLACKBOX', 121847, 121847, 0);

        UPDATE #racklocation SET totalcapacity = capacity WHERE racktype IN ('BINRACK','TMPWH');
        UPDATE #racklocation SET totalcapacity = capacity * 59 WHERE racktype = 'TECHNORACK';
        UPDATE #racklocation SET totalcapacity = capacity * 3 WHERE racktype = 'WAREHOUSE' AND warehouse IN ('YOTO-BU','TECHNO-E','YOTO-SF');
        UPDATE #racklocation SET totalcapacity = capacity * 100 WHERE racktype = 'WAREHOUSE' AND warehouse IN ('YOTO');
        UPDATE #racklocation SET used = (SELECT COUNT(*) FROM racks.dbo.tmpwhracks WHERE PalletNo1 <> '' OR PalletNo2 <> '') WHERE racktype = 'TMPWH';
        UPDATE #racklocation SET used = (SELECT COUNT(*) FROM racks.dbo.BinRack WHERE Warehouse = a.warehouse) FROM #racklocation a WHERE racktype = 'BINRACK';
        UPDATE #racklocation SET used = (SELECT COUNT(*) FROM racks.dbo.WarehouseRackDet WHERE Warehouse = a.warehouse) FROM #racklocation a WHERE racktype = 'WAREHOUSE';
        UPDATE #racklocation SET used = (SELECT COUNT(*) FROM racks.dbo.TechnoRackDet) WHERE racktype = 'TECHNORACK';

            SELECT
                TotalRackLocations   = CAST(ISNULL(SUM(totalcapacity), 0) AS BIGINT),
                FreeBoxRackLocations = CAST(ISNULL(SUM(totalcapacity - used), 0) AS BIGINT),
                FilledBoxLocations   = CAST(ISNULL(SUM(used), 0) AS BIGINT)
              FROM #racklocation
             WHERE warehouse = 'BLACKBOX';
            DROP TABLE #racklocation;"),

        new QueryEntry("Warehouse SOH Summary", "Storage Capacity (non-UAE country)", "{DataName}.dbo.BinRackMaster, {DataName}.dbo.BinRack -- WarehouseSohSummaryService.GetStorageCapacityForCountryAsync", @"
            SELECT
                TotalRackLocations   = CAST(ISNULL((SELECT COUNT(*) FROM [{dataName}].dbo.BinRackMaster), 0) AS BIGINT),
                FreeBoxRackLocations = CAST(ISNULL((SELECT COUNT(*) FROM [{dataName}].dbo.BinRackMaster
                                                      WHERE Barcode NOT IN (SELECT DISTINCT Location FROM [{dataName}].dbo.BinRack)), 0) AS BIGINT),
                FilledBoxLocations   = CAST(ISNULL((SELECT COUNT(*) FROM [{dataName}].dbo.BinRackMaster
                                                      WHERE Barcode IN (SELECT DISTINCT Location FROM [{dataName}].dbo.BinRack)), 0) AS BIGINT)"),

        new QueryEntry("Warehouse SOH Summary", "Warehouse-wise Performance Summary (stock rows, UAE)", "RACKS.dbo.WHBoxItems -- WarehouseSohSummaryService.GetStockOnHandByWarehouseAsync", @"
            SELECT
                Warehouse = CASE WHEN ISNULL(Warehouse, '') = ''  THEN 'TECHNO'
             WHEN Warehouse = 'TECHNO-E'      THEN 'TECHNO'
             WHEN Warehouse = 'YOTO-BU'       THEN 'YOTO'
             ELSE Warehouse
        END,
                TotalQuantity     = CAST(ISNULL(SUM(qty), 0) AS BIGINT),
                TotalBoxesStock   = CAST(ISNULL(SUM(CASE WHEN BoxNo <> '' THEN qty ELSE 0 END), 0) AS BIGINT),
                NumberOfBoxes     = CAST(COUNT(DISTINCT CASE WHEN BoxNo <> '' THEN BoxNo END) AS BIGINT),
                TotalPalletsStock = CAST(ISNULL(SUM(CASE WHEN PalletNo <> '' THEN qty ELSE 0 END), 0) AS BIGINT),
                NumberOfPallets   = CAST(COUNT(DISTINCT CASE WHEN PalletNo <> '' THEN PalletNo END) AS BIGINT),
                TotalActiveSkus   = CAST(COUNT(DISTINCT ItemCode) AS BIGINT)
              FROM RACKS.dbo.WHBoxItems
             GROUP BY CASE WHEN ISNULL(Warehouse, '') = ''  THEN 'TECHNO'
             WHEN Warehouse = 'TECHNO-E'      THEN 'TECHNO'
             WHEN Warehouse = 'YOTO-BU'       THEN 'YOTO'
             ELSE Warehouse
        END
             ORDER BY 1"),

        new QueryEntry("Warehouse SOH Summary", "Warehouse-wise Performance Summary (storage rows, per warehouse)", "racks.dbo.BinRackMaster, racks.dbo.tmpwhracks, racks.dbo.WarehouseRacks, racks.dbo.TechnoRacks, racks.dbo.BinRack, racks.dbo.WarehouseRackDet, racks.dbo.TechnoRackDet -- WarehouseSohSummaryService.GetStorageCapacityForWarehouseAsync (also drives the utilization Gauge)", @"
        SET NOCOUNT ON;
        IF OBJECT_ID('tempdb..#racklocation') IS NOT NULL DROP TABLE #racklocation;
        CREATE TABLE #racklocation(racktype varchar(15), warehouse varchar(15), capacity int, totalcapacity int, used int);

        INSERT INTO #racklocation SELECT 'BINRACK', warehouse, COUNT(*), 0, 0 FROM racks.dbo.BinRackMaster GROUP BY warehouse;
        INSERT INTO #racklocation SELECT 'TMPWH', 'JAFZA', COUNT(*), 0, 0 FROM racks.dbo.tmpwhracks;
        INSERT INTO #racklocation SELECT 'WAREHOUSE', warehouse, COUNT(*), 0, 0 FROM racks.dbo.WarehouseRacks GROUP BY warehouse;
        INSERT INTO #racklocation SELECT 'TECHNORACK', 'TECHNO', COUNT(*), 0, 0 FROM racks.dbo.TechnoRacks;
        INSERT INTO #racklocation VALUES('BINRACK', 'BLACKBOX', 121847, 121847, 0);

        UPDATE #racklocation SET totalcapacity = capacity WHERE racktype IN ('BINRACK','TMPWH');
        UPDATE #racklocation SET totalcapacity = capacity * 59 WHERE racktype = 'TECHNORACK';
        UPDATE #racklocation SET totalcapacity = capacity * 3 WHERE racktype = 'WAREHOUSE' AND warehouse IN ('YOTO-BU','TECHNO-E','YOTO-SF');
        UPDATE #racklocation SET totalcapacity = capacity * 100 WHERE racktype = 'WAREHOUSE' AND warehouse IN ('YOTO');
        UPDATE #racklocation SET used = (SELECT COUNT(*) FROM racks.dbo.tmpwhracks WHERE PalletNo1 <> '' OR PalletNo2 <> '') WHERE racktype = 'TMPWH';
        UPDATE #racklocation SET used = (SELECT COUNT(*) FROM racks.dbo.BinRack WHERE Warehouse = a.warehouse) FROM #racklocation a WHERE racktype = 'BINRACK';
        UPDATE #racklocation SET used = (SELECT COUNT(*) FROM racks.dbo.WarehouseRackDet WHERE Warehouse = a.warehouse) FROM #racklocation a WHERE racktype = 'WAREHOUSE';
        UPDATE #racklocation SET used = (SELECT COUNT(*) FROM racks.dbo.TechnoRackDet) WHERE racktype = 'TECHNORACK';

            SELECT
                TotalRackLocations      = CAST(ISNULL(SUM(totalcapacity), 0) AS BIGINT),
                FreeBoxRackLocations    = CAST(ISNULL(SUM(CASE WHEN racktype = 'BINRACK' THEN totalcapacity - used ELSE 0 END), 0) AS BIGINT),
                FilledBoxLocations      = CAST(ISNULL(SUM(CASE WHEN racktype = 'BINRACK' THEN used ELSE 0 END), 0) AS BIGINT),
                FreePalletRackLocations = CAST(ISNULL(SUM(CASE WHEN racktype <> 'BINRACK' THEN totalcapacity - used ELSE 0 END), 0) AS BIGINT),
                FilledPalletLocations   = CAST(ISNULL(SUM(CASE WHEN racktype <> 'BINRACK' THEN used ELSE 0 END), 0) AS BIGINT)
              FROM #racklocation
             WHERE (CASE WHEN warehouse = 'TECHNO-E' THEN 'TECHNO'
                         WHEN warehouse = 'YOTO-BU'  THEN 'YOTO'
                         ELSE warehouse
                    END) = @warehouse;
            DROP TABLE #racklocation;"),

        new QueryEntry("Warehouse SOH Summary", "SOH Detail — Division Level (UAE)", "RACKS.dbo.WHBoxItems, Datareporting.dbo.vUPC_SUBCLASS -- WarehouseSohSummaryService.GetSohByDivisionAsync", @"
            ;WITH ItemDiv AS (
                SELECT itemcode, Division = MIN(Division)
                  FROM Datareporting.dbo.vUPC_SUBCLASS
                 GROUP BY itemcode
            )
            SELECT
                Division    = ISNULL(v.Division, 'Unknown'),
                SohQty      = CAST(ISNULL(SUM(w.qty), 0) AS BIGINT),
                BoxCount    = CAST(COUNT(DISTINCT CASE WHEN w.BoxNo <> '' THEN w.BoxNo END) AS BIGINT),
                PalletCount = CAST(COUNT(DISTINCT CASE WHEN w.PalletNo <> '' THEN w.PalletNo END) AS BIGINT)
              FROM RACKS.dbo.WHBoxItems w
              LEFT JOIN ItemDiv v ON v.itemcode = w.ItemCode
             GROUP BY ISNULL(v.Division, 'Unknown')
             ORDER BY Division"),

        new QueryEntry("Warehouse SOH Summary", "SOH Detail — Division Level (non-UAE country)", "{DataName}.dbo.WHBoxItemsExport, Datareporting.dbo.vUPC_SUBCLASS -- WarehouseSohSummaryService.GetSohByDivisionForCountryAsync", @"
            ;WITH ItemDiv AS (
                SELECT itemcode, Division = MIN(Division)
                  FROM Datareporting.dbo.vUPC_SUBCLASS
                 GROUP BY itemcode
            )
            SELECT
                Division    = ISNULL(v.Division, 'Unknown'),
                SohQty      = CAST(ISNULL(SUM(w.Qty), 0) AS BIGINT),
                BoxCount    = CAST(COUNT(DISTINCT CASE WHEN w.BoxNo <> '' THEN w.BoxNo END) AS BIGINT),
                PalletCount = CAST(COUNT(DISTINCT CASE WHEN w.PalletNo <> '' THEN w.PalletNo END) AS BIGINT)
              FROM {src} w
              LEFT JOIN ItemDiv v ON v.itemcode = w.ItemCode
             GROUP BY ISNULL(v.Division, 'Unknown')
             ORDER BY Division"),

        new QueryEntry("Warehouse SOH Summary", "SOH Detail — Division Level (Online)", "RACKS.dbo.MFCS_LOCSTOCK, Datareporting.dbo.vUPC_SUBCLASS -- WarehouseSohSummaryService.GetSohByDivisionForOnlineAsync", @"
            ;WITH ItemDiv AS (
                SELECT itemcode, Division = MIN(Division)
                  FROM Datareporting.dbo.vUPC_SUBCLASS
                 GROUP BY itemcode
            )
            SELECT
                Division = ISNULL(v.Division, 'Unknown'),
                SohQty   = CAST(ISNULL(SUM(m.SOH_QTY), 0) AS BIGINT)
              FROM RACKS.dbo.MFCS_LOCSTOCK m
              LEFT JOIN ItemDiv v ON v.itemcode = m.FINALUPC
             WHERE m.MFCS_STOREID = @storeId
             GROUP BY ISNULL(v.Division, 'Unknown')
             ORDER BY Division"),

        new QueryEntry("Warehouse SOH Summary", "SOH Detail — Seasonal Level (UAE)", "RACKS.dbo.WHBoxItems -- WarehouseSohSummaryService.GetSohBySeasonAsync", @"
            SELECT
                Season      = CASE WHEN Season = 'W' THEN 'Winter'
             WHEN Season IN ('S', 'C', 'H') THEN 'Summer'
             ELSE 'Non-seasonal'
        END,
                SohQty      = CAST(ISNULL(SUM(qty), 0) AS BIGINT),
                BoxCount    = CAST(COUNT(DISTINCT CASE WHEN BoxNo <> '' THEN BoxNo END) AS BIGINT),
                PalletCount = CAST(COUNT(DISTINCT CASE WHEN PalletNo <> '' THEN PalletNo END) AS BIGINT)
              FROM RACKS.dbo.WHBoxItems
             GROUP BY CASE WHEN Season = 'W' THEN 'Winter'
             WHEN Season IN ('S', 'C', 'H') THEN 'Summer'
             ELSE 'Non-seasonal'
        END
             ORDER BY Season"),

        new QueryEntry("Warehouse SOH Summary", "SOH Detail — Seasonal Level (non-UAE country)", "{DataName}.dbo.WHBoxItemsExport -- WarehouseSohSummaryService.GetSohBySeasonForCountryAsync", @"
            SELECT
                Season      = CASE WHEN Season = 'W' THEN 'Winter'
             WHEN Season IN ('S', 'C', 'H') THEN 'Summer'
             ELSE 'Non-seasonal'
        END,
                SohQty      = CAST(ISNULL(SUM(Qty), 0) AS BIGINT),
                BoxCount    = CAST(COUNT(DISTINCT CASE WHEN BoxNo <> '' THEN BoxNo END) AS BIGINT),
                PalletCount = CAST(COUNT(DISTINCT CASE WHEN PalletNo <> '' THEN PalletNo END) AS BIGINT)
              FROM {src}
             GROUP BY CASE WHEN Season = 'W' THEN 'Winter'
             WHEN Season IN ('S', 'C', 'H') THEN 'Summer'
             ELSE 'Non-seasonal'
        END
             ORDER BY Season"),

        new QueryEntry("Warehouse SOH Summary", "SOH Detail — Seasonal Level (Online)", "RACKS.dbo.MFCS_LOCSTOCK, USA.dbo.UPCBARCODES -- WarehouseSohSummaryService.GetSohBySeasonForOnlineAsync", @"
            ;WITH ItemSeason AS (
                SELECT UPC, ItemType = MIN(ItemType)
                  FROM USA.dbo.UPCBARCODES
                 GROUP BY UPC
            )
            SELECT
                Season = CASE WHEN u.ItemType = 'W' THEN 'Winter'
                              WHEN u.ItemType IN ('S', 'C', 'H') THEN 'Summer'
                              ELSE 'Non-seasonal'
                         END,
                SohQty = CAST(ISNULL(SUM(m.SOH_QTY), 0) AS BIGINT)
              FROM RACKS.dbo.MFCS_LOCSTOCK m
              LEFT JOIN ItemSeason u ON u.UPC = m.FINALUPC
             WHERE m.MFCS_STOREID = @storeId
             GROUP BY CASE WHEN u.ItemType = 'W' THEN 'Winter'
                           WHEN u.ItemType IN ('S', 'C', 'H') THEN 'Summer'
                           ELSE 'Non-seasonal'
                      END
             ORDER BY Season"),

        new QueryEntry("Warehouse SOH Summary", "Critical Alerts & Aging Inventory / Inventory Age Distribution (UAE)", "RACKS.dbo.WHBoxItems -- WarehouseSohSummaryService.GetAgingUnitsAsync", @"
            SET NOCOUNT ON;
            DECLARE @lpm_1_30 VARCHAR(15), @lpm_30_60 VARCHAR(15), @lpm_60_90 VARCHAR(15), @lpm_above_90 VARCHAR(15);
            SELECT @lpm_1_30 = CONVERT(VARCHAR(10), DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1), 103),
                   @lpm_30_60 = CONVERT(VARCHAR(10), DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)), 103),
                   @lpm_60_90 = CONVERT(VARCHAR(10), DATEADD(MONTH, 2, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)), 103),
                   @lpm_above_90 = CONVERT(VARCHAR(10), DATEADD(MONTH, 3, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)), 103);

            SELECT
                Days1To30   = CAST(ISNULL(SUM(CASE WHEN LPMDt = @lpm_1_30    THEN qty ELSE 0 END), 0) AS BIGINT),
                Days30To60  = CAST(ISNULL(SUM(CASE WHEN LPMDt = @lpm_30_60   THEN qty ELSE 0 END), 0) AS BIGINT),
                Days60To90  = CAST(ISNULL(SUM(CASE WHEN LPMDt = @lpm_60_90   THEN qty ELSE 0 END), 0) AS BIGINT),
                DaysAbove90 = CAST(ISNULL(SUM(CASE WHEN LPMDt = @lpm_above_90 THEN qty ELSE 0 END), 0) AS BIGINT),
                ElapsedLpm  = CAST(ISNULL(SUM(CASE WHEN LPMDt < @lpm_1_30    THEN qty ELSE 0 END), 0) AS BIGINT)
              FROM RACKS.dbo.WHBoxItems"),

        new QueryEntry("Warehouse SOH Summary", "Critical Alerts & Aging Inventory / Inventory Age Distribution (non-UAE country)", "{DataName}.dbo.WHBoxItemsExport -- WarehouseSohSummaryService.GetAgingUnitsForCountryAsync", @"
            SET NOCOUNT ON;
            DECLARE @m0 DATE = DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1);
            DECLARE @m1 DATE = DATEADD(MONTH, 1, @m0);
            DECLARE @m2 DATE = DATEADD(MONTH, 2, @m0);
            DECLARE @m3 DATE = DATEADD(MONTH, 3, @m0);

            SELECT
                Days1To30   = CAST(ISNULL(SUM(CASE WHEN LPMDt = @m0 THEN Qty ELSE 0 END), 0) AS BIGINT),
                Days30To60  = CAST(ISNULL(SUM(CASE WHEN LPMDt = @m1 THEN Qty ELSE 0 END), 0) AS BIGINT),
                Days60To90  = CAST(ISNULL(SUM(CASE WHEN LPMDt = @m2 THEN Qty ELSE 0 END), 0) AS BIGINT),
                DaysAbove90 = CAST(ISNULL(SUM(CASE WHEN LPMDt = @m3 THEN Qty ELSE 0 END), 0) AS BIGINT),
                ElapsedLpm  = CAST(ISNULL(SUM(CASE WHEN LPMDt < @m0 THEN Qty ELSE 0 END), 0) AS BIGINT)
              FROM {src}"),

        // ============================== ECOM Stock Variance Report ==============================
        new QueryEntry("ECOM Stock Variance Report", "Last Refreshed timestamp", "dbo.LPM_ECOM_SOH_COMPARISON -- EcomStockVarianceReportService.GetLastRefreshedAsync", @"SELECT MAX(CreateTS) FROM dbo.LPM_ECOM_SOH_COMPARISON"),

        new QueryEntry("ECOM Stock Variance Report", "Country filter list", "dbo.LPM_ECOM_SOH_COMPARISON -- EcomStockVarianceReportService.GetCountriesAsync", @"SELECT DISTINCT Country FROM dbo.LPM_ECOM_SOH_COMPARISON ORDER BY Country"),

        new QueryEntry("ECOM Stock Variance Report", "Division filter list", "dbo.LPM_ECOM_SOH_COMPARISON -- EcomStockVarianceReportService.GetDivisionsAsync", @"
            SELECT DISTINCT Division FROM dbo.LPM_ECOM_SOH_COMPARISON
             WHERE Division IS NOT NULL AND Division <> '' AND Division NOT IN @excluded
             ORDER BY Division"),

        new QueryEntry("ECOM Stock Variance Report", "Totals row (all filtered rows)", "dbo.LPM_ECOM_SOH_COMPARISON -- EcomStockVarianceReportService.GetTotalsAsync", @"
            SELECT COUNT(*) AS [RowCount],
                   ISNULL(SUM(CAST(IncreffSOH AS BIGINT)), 0) AS IncreffSOH,
                   ISNULL(SUM(CAST(MFCS_SOH AS BIGINT)), 0)   AS MFCS_SOH,
                   ISNULL(SUM(CAST(Variance AS BIGINT)), 0)   AS Variance
              FROM dbo.LPM_ECOM_SOH_COMPARISON
             WHERE (@noCountryFilter = 1 OR Country IN @countries)
               AND (@noDivisionFilter = 1 OR Division IN @divisions)
               AND (@varianceOnly = 0 OR Variance <> 0);"),

        new QueryEntry("ECOM Stock Variance Report", "ECOM Stock Variance grid (paged rows)", "dbo.LPM_ECOM_SOH_COMPARISON -- EcomStockVarianceReportService.GetReportPageAsync", @"
            SELECT Country, Itemcode, IncreffSOH, MFCS_SOH, Variance, CreateTS,
                   CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Division   END AS Division,
                   CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Department END AS Department,
                   CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Class      END AS Class,
                   CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Subclass   END AS Subclass,
                   CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Family     END AS Family
              FROM dbo.LPM_ECOM_SOH_COMPARISON
             WHERE (@noCountryFilter = 1 OR Country IN @countries)
               AND (@noDivisionFilter = 1 OR Division IN @divisions)
               AND (@varianceOnly = 0 OR Variance <> 0)
             ORDER BY Country, Itemcode
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;"),

        new QueryEntry("ECOM Stock Variance Report", "Export Excel (all filtered rows, unpaged)", "dbo.LPM_ECOM_SOH_COMPARISON -- EcomStockVarianceReportService.GetReportAsync", @"
            SELECT Country, Itemcode, IncreffSOH, MFCS_SOH, Variance, CreateTS,
                   CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Division   END AS Division,
                   CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Department END AS Department,
                   CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Class      END AS Class,
                   CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Subclass   END AS Subclass,
                   CASE WHEN Division IN @excludedDivisions THEN NULL ELSE Family     END AS Family
              FROM dbo.LPM_ECOM_SOH_COMPARISON
             WHERE (@noCountryFilter = 1 OR Country IN @countries)
               AND (@noDivisionFilter = 1 OR Division IN @divisions)
               AND (@varianceOnly = 0 OR Variance <> 0)
             ORDER BY Country, Itemcode;"),

        // ============================== SOH Monthly Summary ==============================
        new QueryEntry("SOH Monthly Summary", "Monthly rollup by warehouse group", "dbo.WMS_WHSTOCK_LASTDAY -- WarehouseSohSummaryService.GetSohMonthlySummaryAsync", @"
; WITH Grp AS (
    SELECT *, GroupLabel = CASE WHEN Country = 'UAE' AND (ISNULL(Warehouse, '') = '' OR Warehouse IN ('TECHNO', 'TECHNO-E')) THEN 'TECHNO'
                                 WHEN Country = 'UAE' AND Warehouse IN ('YOTO', 'YOTO-BU')                                    THEN 'YOTO'
                                 WHEN Country = 'UAE' AND Warehouse = 'JAFZA'                                                 THEN 'JAFZA'
                                 WHEN Country = 'KSA'                                                                        THEN 'KSA'
                                 WHEN Country = 'QATAR'                                                                      THEN 'QATAR'
                                 WHEN Country = 'KUWAIT'                                                                     THEN 'KUWAIT'
                                 WHEN Country = 'MALAYSIA'                                                                   THEN 'MYS'
                                 WHEN Country = 'BAHRAIN'                                                                    THEN 'BAHRAIN'
                                 ELSE NULL
                            END
      FROM dbo.WMS_WHSTOCK_LASTDAY
     WHERE YEAR(LastDayOfMonth) = @year
)
SELECT
    GroupLabel,
    LastDayOfMonth,
    Qty         = CAST(ISNULL(SUM(Qty), 0) AS BIGINT),
    BoxCount    = CAST(ISNULL(SUM(BoxCount), 0) AS BIGINT),
    PalletCount = CAST(ISNULL(SUM(PalletCount), 0) AS BIGINT)
  FROM Grp
 WHERE GroupLabel IS NOT NULL
 GROUP BY GroupLabel, LastDayOfMonth
 ORDER BY LastDayOfMonth, GroupLabel"),

        // ============================== YOTO VNA Dashboard ==============================
        new QueryEntry("YOTO VNA Dashboard", "Offloading Shipment Summary — Completed offloading", "usa.dbo.UsaPallets, usa.dbo.KNBBoxes, bfldata.dbo.ContReceipt, hodata.dbo.vUSAOrder -- YotoVnaDashboardService.GetCompletedOffloadingAsync", @"
        WITH OrderAgg AS (
            SELECT refno, SUM(Qty) AS Qty
            FROM hodata.dbo.vUSAOrder WITH (NOLOCK)
            WHERE refno IS NOT NULL
            GROUP BY refno
        ),
            ContainerLevel AS (
                SELECT
                    a.Contno,
                    COUNT(DISTINCT a.PalletNo) AS Pallets,
                    COUNT(DISTINCT b.Boxno)    AS Boxes
                FROM usa.dbo.UsaPallets a WITH (NOLOCK)
                JOIN usa.dbo.KNBBoxes b WITH (NOLOCK)
                    ON a.PalletNo = b.palletno AND a.Contno = b.Contno
                JOIN bfldata.dbo.ContReceipt cr WITH (NOLOCK) ON cr.RefNo = a.Contno
                JOIN OrderAgg oa ON oa.refno = a.Contno
                WHERE a.whouse = @wh
                  AND (a.Contno LIKE 'AEINT%' OR a.Contno LIKE 'AELOC%')
                  AND a.trndate >= @from AND a.trndate < @to
                GROUP BY a.Contno
            )
            SELECT
                CASE WHEN Contno LIKE 'AEINT%' THEN 'AEINT' ELSE 'AELOC' END AS [Group],
                COUNT(*)      AS Containers,
                SUM(Pallets)  AS Pallets,
                SUM(Boxes)    AS Boxes
            FROM ContainerLevel
            GROUP BY CASE WHEN Contno LIKE 'AEINT%' THEN 'AEINT' ELSE 'AELOC' END"),

        new QueryEntry("YOTO VNA Dashboard", "Offloading Shipment Summary — Pending for offloading", "bfldata.dbo.ContReceipt, usa.dbo.UsaPallets, hodata.dbo.vUSAOrder -- YotoVnaDashboardService.GetPendingOffloadingAsync", @"
        WITH OrderAgg AS (
            SELECT refno, SUM(Qty) AS Qty
            FROM hodata.dbo.vUSAOrder WITH (NOLOCK)
            WHERE refno IS NOT NULL
            GROUP BY refno
        )
            SELECT
                CASE WHEN cr.RefNo LIKE 'AEINT%' THEN 'AEINT' ELSE 'AELOC' END AS [Group],
                COUNT(DISTINCT cr.ContNo)      AS Containers,
                SUM(ISNULL(oa.Qty, 0))         AS Qty
            FROM bfldata.dbo.ContReceipt cr WITH (NOLOCK)
            JOIN OrderAgg oa ON oa.refno = cr.RefNo
            WHERE cr.Warehouse = @wh
              AND cr.ReceiptDt >= @floor
              AND (cr.RefNo LIKE 'AEINT%' OR cr.RefNo LIKE 'AELOC%')
              AND NOT EXISTS (
                  SELECT 1 FROM usa.dbo.UsaPallets a WITH (NOLOCK) WHERE a.Contno = cr.RefNo
              )
            GROUP BY CASE WHEN cr.RefNo LIKE 'AEINT%' THEN 'AEINT' ELSE 'AELOC' END"),

        new QueryEntry("YOTO VNA Dashboard", "Total Inbound Summary — Offload Containers (Monthly)", "usa.dbo.UsaPallets, usa.dbo.KNBBoxes, bfldata.dbo.ContReceipt, hodata.dbo.vUSAOrder -- YotoVnaDashboardService.GetMonthlyInboundSummaryAsync", @"
        WITH OrderAgg AS (
            SELECT refno, SUM(Qty) AS Qty
            FROM hodata.dbo.vUSAOrder WITH (NOLOCK)
            WHERE refno IS NOT NULL
            GROUP BY refno
        ),
            ContainerLevel AS (
                SELECT
                    a.Contno,
                    MIN(a.trndate)              AS TrnDate,
                    COUNT(DISTINCT a.PalletNo)  AS Pallets,
                    COUNT(DISTINCT b.Boxno)     AS Boxes,
                    MAX(oa.Qty)                 AS Pcs
                FROM usa.dbo.UsaPallets a WITH (NOLOCK)
                JOIN usa.dbo.KNBBoxes b WITH (NOLOCK)
                    ON a.PalletNo = b.palletno AND a.Contno = b.Contno
                JOIN bfldata.dbo.ContReceipt cr WITH (NOLOCK) ON cr.RefNo = a.Contno
                JOIN OrderAgg oa ON oa.refno = a.Contno
                WHERE a.whouse = @wh
                  AND a.trndate >= @yearStart AND a.trndate < @yearEnd
                GROUP BY a.Contno
            )
            SELECT
                MONTH(TrnDate)  AS Mo,
                COUNT(*)        AS Containers,
                SUM(Pallets)    AS Pallets,
                SUM(Boxes)      AS Boxes,
                SUM(Pcs)        AS Pcs
            FROM ContainerLevel
            GROUP BY MONTH(TrnDate)
            ORDER BY Mo"),

        new QueryEntry("YOTO VNA Dashboard", "Total Inbound Summary — Offload Containers (Weekly/Range)", "usa.dbo.UsaPallets, usa.dbo.KNBBoxes, bfldata.dbo.ContReceipt, hodata.dbo.vUSAOrder -- YotoVnaDashboardService.GetInboundSummaryForRangeAsync", @"
        WITH OrderAgg AS (
            SELECT refno, SUM(Qty) AS Qty
            FROM hodata.dbo.vUSAOrder WITH (NOLOCK)
            WHERE refno IS NOT NULL
            GROUP BY refno
        ),
            ContainerLevel AS (
                SELECT
                    a.Contno,
                    COUNT(DISTINCT a.PalletNo)  AS Pallets,
                    COUNT(DISTINCT b.Boxno)     AS Boxes,
                    MAX(oa.Qty)                 AS Pcs
                FROM usa.dbo.UsaPallets a WITH (NOLOCK)
                JOIN usa.dbo.KNBBoxes b WITH (NOLOCK)
                    ON a.PalletNo = b.palletno AND a.Contno = b.Contno
                JOIN bfldata.dbo.ContReceipt cr WITH (NOLOCK) ON cr.RefNo = a.Contno
                JOIN OrderAgg oa ON oa.refno = a.Contno
                WHERE a.whouse = @wh
                  AND a.trndate >= @from AND a.trndate < @to
                GROUP BY a.Contno
            )
            SELECT
                COUNT(*)            AS Containers,
                ISNULL(SUM(Pallets), 0) AS Pallets,
                ISNULL(SUM(Boxes), 0)   AS Boxes,
                ISNULL(SUM(Pcs), 0)     AS Pcs
            FROM ContainerLevel"),

        new QueryEntry("YOTO VNA Dashboard", "Internal Transfer Summary (all boxes, Monthly)", "bfldata.dbo.vPLTDeliveryDetails -- YotoVnaDashboardService.GetInternalTransferMonthlyAsync (shared query shape, looped once per InternalTransferDefs entry -- only @fromWh/@toWh differ per box)", @"
                SELECT
                    MONTH(EntryDate)          AS Mo,
                    COUNT(DISTINCT trailerno) AS Trips,
                    COUNT(DISTINCT PalletNo)  AS Pallets,
                    COUNT(DISTINCT Boxno)     AS Boxes,
                    ISNULL(SUM(qty), 0)       AS Quantity
                FROM bfldata.dbo.vPLTDeliveryDetails WITH (NOLOCK)
                WHERE EntryDate >= @yearStart AND EntryDate < @yearEnd
                  AND (@fromWh IS NULL OR WarehouseFrom = @fromWh)
                  AND (@toWh IS NULL OR WarehouseTo = @toWh)
                GROUP BY MONTH(EntryDate)
                ORDER BY Mo"),

        new QueryEntry("YOTO VNA Dashboard", "Internal Transfer Summary (all boxes, Weekly/Range)", "bfldata.dbo.vPLTDeliveryDetails -- YotoVnaDashboardService.GetInternalTransferForRangeAsync (shared query shape, looped once per InternalTransferDefs entry)", @"
                SELECT
                    COUNT(DISTINCT trailerno) AS Trips,
                    COUNT(DISTINCT PalletNo)  AS Pallets,
                    COUNT(DISTINCT Boxno)     AS Boxes,
                    ISNULL(SUM(qty), 0)       AS Quantity
                FROM bfldata.dbo.vPLTDeliveryDetails WITH (NOLOCK)
                WHERE EntryDate >= @from AND EntryDate < @to
                  AND (@fromWh IS NULL OR WarehouseFrom = @fromWh)
                  AND (@toWh IS NULL OR WarehouseTo = @toWh)"),

        // ============================== Warehouse Incentives ==============================
        new QueryEntry("Warehouse Incentives", "Techno Checking", "USA.dbo.AMEChecking, BFLDATA.dbo.JAFZAChecking, DATAREPORTING.dbo.vUPC_SUBCLASS -- WarehouseIncentivesService.GetReportAsync", @"
            ;WITH Base AS (
                SELECT TrnDate, Time1, EmpCode, Itemcode, CmpName, COUNT(Itemcode) AS Qty
                  FROM USA.dbo.AMEChecking
                 WHERE TrnDate >= @fromDate AND TrnDate <= @toDate
                   AND LEFT(Time1, 2) NOT IN ('00','01','02','03','04')
                 GROUP BY TrnDate, Time1, EmpCode, CmpName, Itemcode
                UNION ALL
                SELECT TrnDate, Time1, EmpCode, Itemcode, CmpName, COUNT(Itemcode)
                  FROM BFLDATA.dbo.JAFZAChecking
                 WHERE TrnDate >= @fromDate AND TrnDate <= @toDate
                   AND LEFT(Time1, 2) NOT IN ('00','01','02','03','04')
                 GROUP BY TrnDate, Time1, EmpCode, CmpName, Itemcode
                UNION ALL
                SELECT DATEADD(day, -1, TrnDate), Time1, EmpCode, Itemcode, CmpName, COUNT(Itemcode)
                  FROM USA.dbo.AMEChecking
                 WHERE TrnDate >= @fromDate AND TrnDate <= @toDate
                   AND LEFT(Time1, 2) IN ('00','01','02','03','04')
                 GROUP BY TrnDate, Time1, EmpCode, CmpName, Itemcode
                UNION ALL
                SELECT DATEADD(day, -1, TrnDate), Time1, EmpCode, Itemcode, CmpName, COUNT(Itemcode)
                  FROM BFLDATA.dbo.JAFZAChecking
                 WHERE TrnDate >= @fromDate AND TrnDate <= @toDate
                   AND LEFT(Time1, 2) IN ('00','01','02','03','04')
                 GROUP BY TrnDate, Time1, EmpCode, CmpName, Itemcode
            ),
            Filtered AS (
                SELECT * FROM Base WHERE ISNULL(Itemcode, '') <> ''
            ),
            Subclass AS (
                SELECT itemcode, Division,
                       ROW_NUMBER() OVER (PARTITION BY itemcode ORDER BY (SELECT NULL)) AS rn
                  FROM DATAREPORTING.dbo.vUPC_SUBCLASS
            ),
            Enriched AS (
                SELECT f.TrnDate, f.EmpCode, f.Qty, s.Division,
                       Area = CASE WHEN f.CmpName LIKE 'ROBO%' THEN 'AUTO' ELSE 'MANUAL' END
                  FROM Filtered f
                  LEFT JOIN Subclass s ON s.itemcode = f.Itemcode AND s.rn = 1
            )
            SELECT Country = 'UAE', Warehouse = 'TECHNO', Type = 'CHECKING', Area, TrnDate, EmpCode, Division,
                   Qty = SUM(Qty)
              FROM Enriched
             WHERE (@noAreaFilter = 1 OR Area IN @areas)
               AND (@noDivisionFilter = 1 OR Division IN @divisions)
               AND (@empCodeFilter IS NULL OR EmpCode = @empCodeFilter)
             GROUP BY Area, TrnDate, EmpCode, Division
             ORDER BY TrnDate, EmpCode;"),

        new QueryEntry("Warehouse Incentives", "Techno Pairing — MANUAL source fetch", "BFLDATA.dbo.RFPairDetail, BFLDATA.dbo.DataSettings -- TechnoPairingService.FetchManualAsync (ManualSql; part 1 of 3, see the enrichment entry below)", @"
        SELECT 'MANUAL' AS Area, EntryDate AS TrnDate, EmpCode, Itemcode, COUNT(*) AS Qty
          FROM BFLDATA.dbo.RFPairDetail
         WHERE EntryDate >= @fromDate AND EntryDate <= @toDate
           AND Station NOT LIKE 'ST-%'
           AND SUBSTRING(TrfNo, 1, 1) NOT IN (
               SELECT DISTINCT ExportCountryCode FROM BFLDATA.dbo.DataSettings
                WHERE ExportActive = 'Y' AND ExportCountryCode <> '')
           AND LEFT(TrnTime, 2) NOT IN ('00','01','02','03','04')
         GROUP BY EntryDate, EmpCode, Itemcode
        UNION ALL
        SELECT 'MANUAL', DATEADD(day, -1, EntryDate), EmpCode, Itemcode, COUNT(*)
          FROM BFLDATA.dbo.RFPairDetail
         WHERE EntryDate > @fromDate AND EntryDate <= DATEADD(day, 1, @toDate)
           AND Station NOT LIKE 'ST-%'
           AND SUBSTRING(TrfNo, 1, 1) NOT IN (
               SELECT DISTINCT ExportCountryCode FROM BFLDATA.dbo.DataSettings
                WHERE ExportActive = 'Y' AND ExportCountryCode <> '')
           AND LEFT(TrnTime, 2) IN ('00','01','02','03','04')
         GROUP BY EntryDate, EmpCode, Itemcode;"),

        new QueryEntry("Warehouse Incentives", "Techno Pairing — AUTO source fetch", "BFLDATA.dbo.RFPairDetail + robotics.dbo.PairDetail (TechnoRoboDb server) -- TechnoPairingService.FetchAutoAsync (AutoSql; part 2 of 3)", @"
        SELECT 'AUTO' AS Area, EntryDate AS TrnDate, EmpCode, Itemcode, COUNT(*) AS Qty
          FROM BFLDATA.dbo.RFPairDetail
         WHERE LEN(Itemcode) <= 15
           AND EntryDate >= @fromDate AND EntryDate <= @toDate
           AND LEFT(TrnTime, 2) NOT IN ('00','01','02','03','04')
         GROUP BY EntryDate, EmpCode, Itemcode
        UNION ALL
        SELECT 'AUTO', EntryDate, EmpCode, Itemcode, COUNT(*)
          FROM robotics.dbo.PairDetail
         WHERE LEN(Itemcode) <= 15
           AND EntryDate >= @fromDate AND EntryDate <= @toDate
           AND LEFT(TrnTime, 2) NOT IN ('00','01','02','03','04')
         GROUP BY EntryDate, EmpCode, Itemcode
        UNION ALL
        SELECT 'AUTO', DATEADD(day, -1, EntryDate), EmpCode, Itemcode, COUNT(*)
          FROM BFLDATA.dbo.RFPairDetail
         WHERE LEN(Itemcode) <= 15
           AND EntryDate > @fromDate AND EntryDate <= DATEADD(day, 1, @toDate)
           AND LEFT(TrnTime, 2) IN ('00','01','02','03','04')
         GROUP BY EntryDate, EmpCode, Itemcode
        UNION ALL
        SELECT 'AUTO', DATEADD(day, -1, EntryDate), EmpCode, Itemcode, COUNT(*)
          FROM robotics.dbo.PairDetail
         WHERE LEN(Itemcode) <= 15
           AND EntryDate > @fromDate AND EntryDate <= DATEADD(day, 1, @toDate)
           AND LEFT(TrnTime, 2) IN ('00','01','02','03','04')
         GROUP BY EntryDate, EmpCode, Itemcode;"),

        new QueryEntry("Warehouse Incentives", "Techno Pairing — enrichment & aggregation", "#PairRaw (temp), hodata.dbo.itemmaster, hodata.dbo.itemgroup, usa.dbo.usapriority -- TechnoPairingService.GetReportAsync (part 3 of 3, runs against OnPremBackupDB after MANUAL+AUTO rows are bulk-copied into #PairRaw)", @"
                SELECT Type = 'PAIRING', p.Area, p.TrnDate, p.EmpCode,
                       GroupName = ig.Description,
                       Division = up.DivisionY,
                       Qty = SUM(p.Qty)
                  FROM #PairRaw p
                  LEFT JOIN hodata.dbo.itemmaster im ON im.ItemCode = p.Itemcode
                  LEFT JOIN hodata.dbo.itemgroup ig ON ig.GroupCode = im.GroupCode
                  LEFT JOIN usa.dbo.usapriority up ON up.groupCode = im.GroupCode
                 WHERE (@noAreaFilter = 1 OR p.Area IN @areas)
                   AND (@noDivisionFilter = 1 OR up.DivisionY IN @divisions)
                   AND (@empCodeFilter IS NULL OR p.EmpCode = @empCodeFilter)
                 GROUP BY p.Area, p.TrnDate, p.EmpCode, ig.Description, up.DivisionY
                 ORDER BY p.TrnDate, p.EmpCode;"),

        new QueryEntry("Warehouse Incentives", "Techno Building", "ONLINE.dbo.RFPairingCountPhotoCheckBuild (WmsProductionDb) -- TechnoBuildingService.GetReportAsync (ReportSql)", @"
        IF OBJECT_ID('tempdb..#BuildCountTemp') IS NOT NULL DROP TABLE #BuildCountTemp;

        CREATE TABLE #BuildCountTemp (
            EmpCode VARCHAR(50), Area VARCHAR(10), TrnDate DATETIME,
            HR0A INT, HR1A INT, HR2A INT, HR3A INT, HR4A INT, HR5A INT, HR6A INT, HR7A INT,
            HR8A INT, HR9A INT, HR10A INT, HR11A INT, HR12A INT, HR13A INT, HR14A INT,
            HR15A INT, HR16A INT, HR17A INT, HR18A INT, HR19A INT, HR20A INT, HR21A INT,
            HR22A INT, HR23A INT
        );

        INSERT INTO #BuildCountTemp
        SELECT EmpCode, CASE WHEN Type = 'PB' THEN 'AUTO' ELSE 'MANUAL' END, TrnDate,
               HR0A, HR1A, HR2A, HR3A, HR4A, HR5A, HR6A, HR7A, HR8A, HR9A, HR10A, HR11A,
               HR12A, HR13A, HR14A, HR15A, HR16A, HR17A, 0, 0, 0, 0, 0, 0
          FROM ONLINE.dbo.RFPairingCountPhotoCheckBuild
         WHERE WareHouse = 'TECHNO' AND Type IN ('PB','MB')
           AND TrnDate >= @fromDate AND TrnDate <= @toDate;

        INSERT INTO #BuildCountTemp
        SELECT EmpCode, CASE WHEN Type = 'PB' THEN 'AUTO' ELSE 'MANUAL' END, DATEADD(day, -1, TrnDate),
               0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
               HR18A, HR19A, HR20A, HR21A, HR22A, HR23A
          FROM ONLINE.dbo.RFPairingCountPhotoCheckBuild
         WHERE WareHouse = 'TECHNO' AND Type IN ('PB','MB')
           AND TrnDate > @fromDate AND TrnDate <= DATEADD(day, 1, @toDate);

        SELECT Type = 'BUILDING', Area, TrnDate, EmpCode,
               Qty = SUM(ISNULL(HR0A,0)+ISNULL(HR1A,0)+ISNULL(HR2A,0)+ISNULL(HR3A,0)+ISNULL(HR4A,0)+
                         ISNULL(HR5A,0)+ISNULL(HR6A,0)+ISNULL(HR7A,0)+ISNULL(HR8A,0)+ISNULL(HR9A,0)+
                         ISNULL(HR10A,0)+ISNULL(HR11A,0)+ISNULL(HR12A,0)+ISNULL(HR13A,0)+ISNULL(HR14A,0)+
                         ISNULL(HR15A,0)+ISNULL(HR16A,0)+ISNULL(HR17A,0)+ISNULL(HR18A,0)+ISNULL(HR19A,0)+
                         ISNULL(HR20A,0)+ISNULL(HR21A,0)+ISNULL(HR22A,0)+ISNULL(HR23A,0))
          FROM #BuildCountTemp
         WHERE (@noAreaFilter = 1 OR Area IN @areas)
           AND (@empCodeFilter IS NULL OR EmpCode = @empCodeFilter)
         GROUP BY Area, TrnDate, EmpCode
         ORDER BY TrnDate, EmpCode;"),

        new QueryEntry("Warehouse Incentives", "Techno Pricing", "BFLDATA.dbo.PricingCount, bfldata.dbo.DeptStock, usa.dbo.usapriority -- TechnoPricingService.GetReportAsync (ReportSql)", @"
        ;WITH Agg AS (
            SELECT a.TrnDate, a.EmpCode, a.EmpName, a.GroupName,
                   Division = (SELECT TOP 1 Division FROM bfldata.dbo.DeptStock
                                 WHERE Department IN (
                                     SELECT Department FROM usa.dbo.usapriority WHERE groupCode = a.GroupCode)),
                   Qty = SUM(ISNULL(Ch1,0)+ISNULL(Ch2,0)+ISNULL(Ch3,0)+ISNULL(Ch4,0)+ISNULL(Ch5,0)+
                             ISNULL(Ch6,0)+ISNULL(Ch7,0)+ISNULL(Ch8,0)+ISNULL(Ch9,0)+ISNULL(Ch10,0)+
                             ISNULL(Ch11,0)+ISNULL(Ch12,0)+ISNULL(Ch13,0)+ISNULL(Ch14,0)+ISNULL(Ch15,0)+
                             ISNULL(Ch16,0)+ISNULL(Ch17,0)+ISNULL(Ch18,0)+ISNULL(CH19,0)+ISNULL(Ch20,0)+
                             ISNULL(Ch21,0)+ISNULL(Ch22,0)+ISNULL(Ch0,0)),
                   a.Multiplier
              FROM BFLDATA.dbo.PricingCount a
             WHERE a.TrnDate >= @fromDate AND a.TrnDate <= @toDate
             GROUP BY a.TrnDate, a.EmpCode, a.EmpName, a.GroupName, a.GroupCode, a.Multiplier
        )
        SELECT Type = 'PRICING', TrnDate, EmpCode, EmpName, GroupName, Division, Qty, Multiplier
          FROM Agg
         WHERE (@noDivisionFilter = 1 OR Division IN @divisions)
           AND (@empCodeFilter IS NULL OR EmpCode = @empCodeFilter)
         ORDER BY TrnDate, EmpCode;"),

        // ============================== Ecom Production Report ==============================
        new QueryEntry("Ecom Production Report", "Online WH Users (table)", "LPMSIM.dbo.UserWHDetail -- EcomProductionReportsService.GetUsersAsync", @"
SELECT Empcode, UserName, FullName, Active, AddedUser, CreateTS AS CreateTs
  FROM LPMSIM.dbo.UserWHDetail WITH (NOLOCK)
 WHERE Warehouse = @wh   -- @wh = 'Online'
 ORDER BY Active DESC, FullName"),

        new QueryEntry("Ecom Production Report", "YOTO Production", "USA.dbo.VUPCBOXDET joined to active Online users -- EcomProductionReportsService.GetYotoProductionAsync", @"
; WITH OnlineUsers AS (
    SELECT Empcode, UserName, FullName
      FROM LPMSIM.dbo.UserWHDetail WITH (NOLOCK)
     WHERE Warehouse = @wh AND Active = 1   -- @wh = 'Online'
)
SELECT
    TrnDate  = d.TrnDate,
    Empcode  = u.Empcode,
    UserName = u.UserName,
    FullName = u.FullName,
    BoxCount = CAST(COUNT(DISTINCT d.BoxNo) AS BIGINT),
    Qty      = CAST(ISNULL(SUM(d.Qty), 0) AS BIGINT)
  FROM USA.dbo.VUPCBOXDET d WITH (NOLOCK)
  JOIN OnlineUsers u ON d.PreparedBy = u.Empcode OR d.PreparedBy = u.UserName
 WHERE d.WHouse = 'YOTO' AND d.TrnDate BETWEEN @fromDt AND @toDt
 GROUP BY d.TrnDate, u.Empcode, u.UserName, u.FullName
 ORDER BY d.TrnDate DESC, u.FullName"),
    };
}
