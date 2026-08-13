namespace Wms.Data.Gcp;

/// <summary>Config for direct BigQuery access (WeeklySalesFromGCP nightly batch).
/// Populated from configuration section "BigQuery".</summary>
public sealed class GcpBigQueryOptions
{
    public const string SectionName = "BigQuery";

    /// <summary>GCP project that owns the dataset, e.g. <c>mvp-data-bi</c>.</summary>
    public string ProjectId { get; set; } = "";

    /// <summary>Absolute path to a service-account JSON key file. When blank,
    /// falls back to Application Default Credentials (e.g. the
    /// GOOGLE_APPLICATION_CREDENTIALS env var — used in production where the
    /// key is mounted from Azure Key Vault).</summary>
    public string CredentialsPath { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProjectId);
}
