namespace Wms.Data.Lpm;

/// <summary>Config for the external Altavant WMS-integration APIs (GIN store-inbounds
/// + product master). Both share one ApiKey. Populated from configuration section
/// "ApiGinIntegration".</summary>
public sealed class ApiGinIntegrationOptions
{
    public const string SectionName = "ApiGinIntegration";

    /// <summary>GIN store-inbounds endpoint URL.</summary>
    public string BaseUrl { get; set; } = "https://api.bfl.altavantconsulting.eu/v1/store-inbounds";

    /// <summary>Product master endpoint URL.</summary>
    public string ProductsUrl { get; set; } = "https://api.bfl.altavantconsulting.eu/v1/products";

    /// <summary>Sent as the raw "apikey" request header (API Key auth, not Bearer).
    /// Overridden in Program.cs from the top-level "Oct_ApiKey" App Service setting
    /// (shared with ApiEpcIntegrationOptions — both Altavant endpoints take the same
    /// key), not read from the "ApiGinIntegration" section below.</summary>
    public string ApiKey { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
