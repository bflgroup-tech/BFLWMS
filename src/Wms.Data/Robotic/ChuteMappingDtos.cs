namespace Wms.Data.Robotic;

public record ChuteConfigRow(
    string ChuteId, int? Status, string? Direction,
    string? ShopId, string? ShopName, string? TotId);

public record ShopNameRow(int? RoboShopId, string? ShopName, int Qty)
{
    // Dapper requires a constructor whose parameter count matches the query's column count exactly —
    // it won't fall back to the default above. Queries that don't select Qty (e.g. SearchShopsAsync)
    // need this 2-arg overload, or materialization throws and gets swallowed as "No results".
    public ShopNameRow(int? RoboShopId, string? ShopName) : this(RoboShopId, ShopName, 0) { }
}

public record ChuteCountRow(string? ShopId, int ChuteCount);

public record ChutePendingQtyRow(string ChuteId, int PendingQty);
