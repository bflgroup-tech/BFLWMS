namespace Wms.Data.Lpm;

/// <summary>One warehouse column in the "Daily Transfer Qty by Warehouse" report (Admin-only,
/// Production Summary Report) — TerritoryCode is the key BFL_MFP_OUTBOUND_T1.territory uses for
/// this warehouse's Merch Target lookup (e.g. "ae" for UAE/TECHNO, "sa" for KSA's ShopName).</summary>
public sealed record WarehouseTransferColumn(string Warehouse, string Country, string TerritoryCode);

/// <summary>One (Date, Warehouse) transfer-qty cell, summed from bfldata.dbo.DailyCountCategoryTrf's hourly columns.</summary>
public sealed record DailyWarehouseTransferRow(DateTime TrnDate, string Warehouse, long TransferQty);

/// <summary>Sum of merch_need for one territory, for the selected (Year, Week) — from LPMSIM.dbo.BFL_MFP_OUTBOUND_T1.</summary>
public sealed record WarehouseMerchNeedRow(string TerritoryCode, long MerchNeed);

public sealed record DailyTransferByWarehouseResult(
    List<WarehouseTransferColumn>   Columns,
    List<DailyWarehouseTransferRow> Daily,
    List<WarehouseMerchNeedRow>     MerchNeeds);
