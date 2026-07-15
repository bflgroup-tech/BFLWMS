namespace Wms.Data.Lpm;

public record SyncRow(string Description, int? Regional, int? HO, string? RegionalError = null, string? HoError = null);

public record SyncFilter(string Country, DateTime DateFrom, DateTime DateTo);
