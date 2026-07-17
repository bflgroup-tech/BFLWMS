namespace Wms.Data.Robotic;

/// <summary>Per-warehouse config for the Robotics DB connection and the two
/// external chute APIs (mapping save, enable/disable) — each warehouse
/// (JAFZA, TECHNO, ...) has its own Robotics SQL Server and its own API
/// endpoints/tokens.</summary>
public sealed class RoboticWarehouseOptions
{
    /// <summary>Key into ConnectionStrings, e.g. "JafazaRoboDb" / "TechnoRoboDb".</summary>
    public string ConnectionStringKey { get; set; } = "";

    public string ChuteMappingApiUrl { get; set; } = "";
    public string ChuteMappingApiToken { get; set; } = "";

    public string ChuteStatusApiUrl { get; set; } = "";
    public string ChuteStatusApiToken { get; set; } = "";
}

/// <summary>Config for the Chute Mapping page — one entry per warehouse,
/// keyed by warehouse code (e.g. "JAFZA", "TECHNO") matching WmsWHMaster.
/// Populated from configuration section "RoboticApi".</summary>
public sealed class RoboticApiOptions
{
    public const string SectionName = "RoboticApi";

    public Dictionary<string, RoboticWarehouseOptions> Warehouses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
