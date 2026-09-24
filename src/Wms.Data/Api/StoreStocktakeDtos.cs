namespace Wms.Data.Api;

public record StoreStocktakeItemRequest(
    string ItemCode,
    string? Ean,
    int Quantity,
    int Soh,
    int Variance);

public record StoreStocktakeRequest(
    string StockCountId,
    string StoreId,
    DateTime TrnDate,
    List<StoreStocktakeItemRequest> Items);

public record StoreStocktakeResultItemRequest(
    string ItemCode,
    string? Ean,
    string? QrCode,
    string? Rfid,
    int Quantity);

public record StoreStocktakeResultRequest(
    string StockCountId,
    string StoreId,
    DateTime TrnDate,
    string Username,
    List<StoreStocktakeResultItemRequest> Items);
