namespace Wms.Data.Robotic;

/// <summary>Config for the two external Robotics APIs the Chute Mapping page
/// calls: chute-to-shop mapping updates and chute enable/disable. Populated
/// from configuration section "RoboticApi".</summary>
public sealed class RoboticApiOptions
{
    public const string SectionName = "RoboticApi";

    public string ChuteMappingApiUrl { get; set; } = "";
    public string ChuteMappingApiToken { get; set; } = "";

    public string ChuteStatusApiUrl { get; set; } = "";
    public string ChuteStatusApiToken { get; set; } = "";
}
