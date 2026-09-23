namespace Wms.Core.Entities;

/// <summary>A registered machine-to-machine client for the WMS API
/// (OAuth2 client-credentials grant). SecretHash never stores the plaintext
/// secret — it is shown once at creation time and cannot be retrieved again.</summary>
public class ApiClientApp
{
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public string SecretHash { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime CreateTS { get; set; }
    public string CreatedBy { get; set; } = "";
}
