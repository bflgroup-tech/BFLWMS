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
    int      TgtEOM,          // MerchNeedMonth from LPM_EOM_Output
    int      SOHToday,        // SUM from Racks.dbo.LPM_Locstock
    int      WeeksToInclude,  // per-country config from WmsCountryOtsWeeks
    int      WeekSales,       // SUM(SalesTgtWk) over the next N weeks starting current wk
    int      InTransit,       // (Ex2SOH + boxsoh) / storeCount(country); UAE = 0
    int      Ex2DcSoh,        // r1whsoh / storeCount(country)
    int      CountingWIP,     // SUM(AllocatedQty) for approved-but-not-completed containers per (StoreID, DivCode)
    int      OtsQtyToday,     // TgtEOM + WeekSales - SOH - InTransit - Ex2DC - CountingWIP
    double   OtsPercentToday  // OtsQtyToday / TgtEOM * 100; 0 when TgtEOM = 0
);

/// <summary>One row per available (Month, Year) picker option.</summary>
public record OtsMonthYearOption(int Month, int Year);
