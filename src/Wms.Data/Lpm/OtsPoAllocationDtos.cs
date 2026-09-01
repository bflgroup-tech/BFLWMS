namespace Wms.Data.Lpm;

/// <summary>One row on the OTS for PO Allocation report — one per (Country, StoreID, DivCode).</summary>
public record OtsPoAllocationRow(
    string   Country,
    string   StoreID,
    string?  StoreName,
    int      DivCode,
    string?  Division,
    string?  VolumeGroup,
    int?     PriorityRank,
    string   EOMMonth,        // formatted "MMM-yyyy" for the picked Month/Year
    string?  TgtEOMMonth,     // "MMM-yyyy" of the month TgtEOM was read from (last-included-week's month; per country)
    int      TgtEOM,          // TargetEOM from LPM_EOM_Output for (TgtEOMMonth, TgtEOMYear)
    int      SOHToday,        // SUM from Racks.dbo.LPM_Locstock
    int      NoOfLeadWeeks,   // per-country config from WmsCountryOtsWeeks — how many weeks of lead the interpolation projects forward
    int      WeekSales,       // SUM(SalesTgtWk) over the next N weeks starting current wk
    int      LeadIntransit,   // vTransferDetail qty per country whose LPMDt <= (1st of month that today+leadWeeks lands in), split across (Country, DivCode) stores via largest-remainder. UAE=0.
    int      LeadDCSOH,       // {DataName}..whboxitemexport qty per country whose LPMDt <= same cutoff, split across (Country, DivCode) stores via largest-remainder.
    int      InTransit,       // (Ex2SOH + boxsoh) / storeCount(country); UAE = 0
    int      Ex2DcSoh,        // R1WHSOH for (Country, DivCode), split by Week Sales — that country's OWN export warehouse
    int      UaeDcSoh,        // share of the UAE DC pool (racks..WHBoxItems, LPMDt window), split by Week Sales across all countries except ECOM
    int      CountingWIP,     // SUM(AllocatedQty) for approved-but-not-completed containers per (StoreID, DivCode)
    int      OtsQtyToday,     // CurrentEOW + WeekSales - SOH - InTransit - Ex2DcSoh - UaeDcSoh - CountingWIP
    double   OtsPercentToday, // OtsQtyToday / CurrentEOW * 100; 0 when CurrentEOW <= 0
    string?  PrevEOMMonth,    // "MMM-yyyy" of the month PrevMonthEOM was read from (TgtEOMMonth - 1)
    int      PrevMonthEOM,    // TargetEOM from LPM_EOM_Output for PrevEOMMonth; 0 if missing
    int      DivisorWeeks,    // #weeks in the TARGET EOM month (the month TargetWeek falls in) — the WeekAdjustment divisor
    decimal  WeekAdjustment,  // (TgtEOM - PrevMonthEOM) / DivisorWeeks ; positive = scaling up, negative = winding down
    int      CurrentWeek,     // latest wk in LPM_OTS_Output — same for every row
    int      TargetWeek,      // CurrentWeek + NoOfLeadWeeks (per country)
    int      WeeksMultiplier, // TargetWeek - last week of the month BEFORE the target month = weeks INTO the target month
    int      CurrentEOW       // PrevMonthEOM + (WeekAdjustment * WeeksMultiplier). Falls back to TgtEOM when PrevMonthEOM = 0
);

/// <summary>One row per available (Month, Year) picker option.</summary>
public record OtsMonthYearOption(int Month, int Year);

/// <summary>One row from dbo.StoreDivGrade — the "Generate Volume Group" output.</summary>
public record StoreDivGradeRow(
    int      Month1,
    int      Year1,
    string   Country,
    string   StoreID,
    string?  StoreName,
    int      DivCode,
    string?  Division,
    decimal? SalesAmt,
    decimal? AvgSalesAmt,
    decimal? AvgSalesPct,
    string?  Grade);
