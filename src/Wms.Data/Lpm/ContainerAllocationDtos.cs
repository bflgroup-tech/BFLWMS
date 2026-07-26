namespace Wms.Data.Lpm;

/// <summary>One row in the "Load PO Data" grid on the Container Allocation page.</summary>
public record PoDataRow(
    string    Contno,
    DateTime? ContReceiptDT,
    string?   PONO,
    string?   LPM,
    string?   Buyer,
    string?   Division,
    string?   Brand,
    int       Qty,
    string?   DestCountry);

/// <summary>
/// Outcome of the Phase-1 validation. Each check that ran produces a step
/// entry. Ok = true means every step passed; Phase-2 process can then start.
/// </summary>
public record ContainerAllocationValidationResult(
    bool                       Ok,
    IReadOnlyList<ValidationStep> Steps);

public record ValidationStep(string Label, bool Ok, string? Detail);

/// <summary>Progress event from ProcessAllocationAsync.</summary>
public record AllocationProgress(int Current, int Total, string? CurrentItem);

/// <summary>One row in the allocation preview / output. Each row = one PO line item distributed to one destination store.</summary>
public record AllocationRow(
    string  Contno,
    string  OraPONo,
    string  ItemCode,
    string? ItemName,
    string? Brand,
    int     PoQty,
    string  StoreID,
    string? StoreName,
    string  Country,
    string? Division,
    string  VolumeGroup,
    int     SkuMax,
    int     AllocQty,
    int     MerchNeedMonth,
    int     DivCode,
    int     RoundRobinExtra,
    string? LPM,
    DateTime? LPMDt,
    double? OTS = null,
    // P3 enrichment + audit fields.
    string?  Season           = null,    // usa.USAOrgFile.season
    string?  Style            = null,    // usa.USAOrgFile.Style
    string?  Size             = null,    // usa.USAOrgFile.Size
    string?  Department       = null,    // datareporting.vupc_subclass.Department
    decimal? SalesPrice       = null,    // hodata.salesprice or <DataName>.RFSalesprice (per store country)
    string?  PalletType       = null,    // WMS_Building_PalletTypes.PalletTypeS (or PalletTypeW when season='W')
    int      PrevAllocatedQty = 0,       // (StoreID, DivCode) seed at allocation time
    int?     PriorityRank     = null,    // LPM_EOM_Output.PriorityRank per (StoreID, DivCode) — lower = higher priority
    int?     MnwToday         = null,    // OTSOutput.Mnwtoday latest per (StoreID, DivCode) by OTSDate DESC
    int?     Phase2Qty        = null,    // pcs of AllocQty coming from Phase 2 (RR-Rest + Overflow of FillSKUMax+RR)
    // OTS-run-based FillSKUMax+RR pass tracking (new algorithm — see
    // ContainerAllocationService FillSKUMaxRoundRobin branch).
    int?     Pass1Qty         = null,    // OTS% >= AvgOTS% pass
    int?     Pass2Qty         = null,    // 0 < OTS% < AvgOTS% pass
    int?     Pass3Qty         = null,    // OTS% <= 0 round-robin pass
    int?     Pass4Qty         = null,    // Pass 4 ratio distribution across all eligible stores
    int?     Pass4RatioCap    = null,    // Store's OTS-driven tier cap at Pass 4 time (denominator for the ratio share)
    decimal? AvgOtsPercent    = null,    // per-Division AVG(OtsPercentToday WHERE > 0) at item time
    int?     OtsQtyToday      = null,    // OtsQtyToday from WmsOtsPoAllocationRun for this (StoreID, DivCode) — initial value, not decremented
    int?     TgtEOM           = null,    // TgtEOM from WmsOtsPoAllocationRun for this (StoreID, DivCode) — FillSKUMax+RR only
    int?     RawSkuMax        = null,    // Raw SKUMax from LPM_SKUMaxRule band lookup (before subtracting SOHToday) — FillSKUMax+RR only. 0 = no band matched.
    // Analysis columns — same across all rows for a given item (AvgOtsMin/Max) or per-store snapshot.
    string?  SkuMaxBand       = null,    // 'MinMin' / 'MinMax' / 'IdealMax' / 'MaxMax' — the tier the picker landed on at the LAST pass that wrote this row.
    decimal? AvgOtsMin        = null,    // AvgOts - OTSBandPct — lower edge of the IdealMax band for this item.
    decimal? AvgOtsMax        = null,    // AvgOts + OTSBandPct — upper edge of the IdealMax band for this item.
    decimal? InitialOtsPct    = null,    // OtsPercentToday from WmsOtsPoAllocationRun (static, matches OTS PO Allocation report %).
    int?     Soh              = null,    // per-(Store, Item) SOH from racks.LPM_locstock used in cap = tier - SOH.
    int?     RunningOtsQty    = null);   // runningOtsQty at the moment this row was written (after prior-item decrements).

/// <summary>One row in the blocked-items list: an (item, store) pair that was
/// excluded from allocation by LPM_StoreDeptAccess or LPM_StoreDivAccess.</summary>
public record BlockedItemRow(
    string  Contno,
    string  ItemCode,
    string? ItemName,
    string? Division,
    string? Department,
    string  StoreID,
    string? StoreName,
    string  Country,
    int     PoQty,
    int     DivCode,
    string  BlockReason);   // 'DeptAccess' / 'DivAccess' / 'DeptAccess+DivAccess'

/// <summary>State info shown above the buttons. Now tracks per-RunOption final
/// row counts so the page knows whether each algorithm has been run for this container.</summary>
public record AllocationStatus(
    bool HasDraft,
    bool HasFinal,
    int  DraftRows,
    int  FinalRows,
    DateTime? FinalAt,
    string? DraftRunOption,
    int  FillSkuMaxRows,
    int  RoundRobinRows,
    int  FillSKUMaxRoundRobinRows = 0,
    int  AzureAllocRows           = 0,   // dbo.WMS_ContAllocationData row count on Azure — > 0 means the container has been synced and Delete should be blocked at UI level
    int  FillMinMinPlusOthersRows = 0);

/// <summary>How to distribute qty across eligible stores.
/// FillSKUMax and RoundRobin are kept for run-history compatibility but are
/// no longer offered in the Container Allocation page dropdown.</summary>
public enum RunOption { FillSKUMax = 0, RoundRobin = 1, FillSKUMaxRoundRobin = 2, FillMinMinPlusOthers = 3 }

/// <summary>What ProcessAllocationAsync returns — allocations + the
/// (item, store) pairs blocked by LPM_StoreDeptAccess / LPM_StoreDivAccess.</summary>
public record AllocationProcessResult(
    List<AllocationRow>    Allocations,
    List<BlockedItemRow>   Blocked,
    List<AllocationTraceRow>? Trace = null);

/// <summary>Per-Pass audit trail — one row per (ContNo, Itemcode, StoreID, Pass)
/// touch, captured only when the operator ticks "Trace Allocation" on the Container
/// Allocation page. Persisted to dbo.WmsAllocationTrace on LPMSIM so operators can
/// reconstruct WHY each store got its final quantity.</summary>
public record AllocationTraceRow(
    string   ContNo,
    string   Itemcode,
    string   StoreID,
    int      DivCode,
    int      Pass,               // 1..4
    int      SortRank,           // position in the pass's sorted store list (0-based)
    string?  VolumeGroup,
    string?  TierName,           // MinMin / MinMax / IdealMax / MaxMax
    decimal? LiveOtsPctBefore,
    int      Cap,                // tier cap - SOH (what the Pass could give)
    int      Soh,
    int      CurrentBeforeTake,
    int      RemainingBefore,
    int      Take,
    int      RemainingAfter,
    int      RunningOtsQtyAfter,
    string   RunOption,
    string?  SkipReason = null,     // NULL=allocated, else 'CapReached' / 'ShareZero'
    // OTS picker reference values — same across all passes for a given (store, item),
    // regardless of which pass fired. Distinct from the pass-specific TierName/Cap.
    int?     DefaultSkuMax  = null, // OTS tier picker's effective cap (RawSkuMax - Soh)
    int?     RawSkuMax      = null, // OTS tier picker's raw tier value
    string?  OtsTierName    = null, // MinMin / MinMax / IdealMax / MaxMax  (OTS picker's tier)
    decimal? AvgOtsPercent  = null,
    decimal? AvgOtsMin      = null,
    decimal? AvgOtsMax      = null,
    decimal? InitialOtsPct  = null);

/// <summary>Header row read back for the "Processed Contnos" dropdown banner.
/// Mirrors WMS_Cont_Allocation_Header columns.</summary>
public record BatchInfo(
    int       BatchNo,
    string    ContNo,
    string?   Warehouse,
    string    GenCountry,
    string    Country,            // comma-separated allocation destinations
    string    RunOption,
    int?      RowCount1,
    int?      TotalQty,
    DateTime  ProcessedTS,
    string?   ProcessedBy,
    DateTime? ApprovedDt,
    string?   ApprovedBy);
