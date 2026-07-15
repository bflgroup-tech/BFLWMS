namespace Wms.Data.Lpm;

public record SyncRow(string Description, int? Regional, int? HO);

public record SyncFilter(string Country, DateTime DateFrom, DateTime DateTo);
