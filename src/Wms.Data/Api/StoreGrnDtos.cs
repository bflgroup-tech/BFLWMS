namespace Wms.Data.Api;

public record StoreGrnItemRequest(
    string ItemCode,
    string? Ean,
    string? Epc,
    string? QrCode,
    int TrfQty,
    int ScanQty);

public record StoreGrnTransferRequest(
    string TrfNo,
    List<StoreGrnItemRequest> Items);

public record StoreGrnRequest(
    string StoreId,
    DateTime DateTime,
    string GinNo,
    string User,
    List<StoreGrnTransferRequest> Transfers);
