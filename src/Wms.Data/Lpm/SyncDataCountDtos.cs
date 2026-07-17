namespace Wms.Data.Lpm;

public record SyncRow(string Description, int? Regional, int? HO, string? RegionalError = null, string? HoError = null);
public record SyncFilter(string Country, DateTime DateFrom, DateTime DateTo);

public record CountryCount(int? Regional, string? RegionalError, int? HO, string? HoError);
public record SyncRowMulti(string Description, IReadOnlyDictionary<string, CountryCount> Countries);
