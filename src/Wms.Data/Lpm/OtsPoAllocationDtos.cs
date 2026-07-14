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
    int      TgtEOM,          // TargetEOM from LPM_EOM_Output (current Month/Year)
    int      SOHToday,        // SUM from Racks.dbo.LPM_Locstock
    int      WeeksToInclude,  // per-country config from WmsCountryOtsWeeks
    int      WeekSales,       // SUM(SalesTgtWk) over the next N weeks starting current wk
    int      InTransit,       // (Ex2SOH + boxsoh) / storeCount(country); UAE = 0
    int      Ex2DcSoh,        // r1whsoh / storeCount(country)
    int      CountingWIP,     // SUM(AllocatedQty) for approved-but-not-completed containers per (StoreID, DivCode)
    int      OtsQtyToday,     // CurrentEOW + WeekSales - SOH - InTransit - Ex2DC - CountingWIP
    double   OtsPercentToday, // OtsQtyToday / CurrentEOW * 100; 0 when CurrentEOW <= 0
    int      PrevMonthEOM,    // TargetEOM from LPM_EOM_Output for the PREVIOUS Month/Year; 0 if missing
    decimal  WkReduction,     // (PrevMonthEOM - TgtEOM) / weeksInCurrentMonth; 0 when PrevMonthEOM = 0
    int      CurrentEOW       // PrevMonthEOM - (WkReduction * weeksElapsedSoFar). Falls back to TgtEOM when PrevMonthEOM = 0
);

/// <summary>One row per available (Month, Year) picker option.</summary>
public record OtsMonthYearOption(int Month, int Year);
