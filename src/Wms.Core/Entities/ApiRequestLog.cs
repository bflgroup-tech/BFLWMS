namespace Wms.Core.Entities;

/// <summary>One row per request made to /api/v1/*. Deliberately holds no request/response
/// body content — method, path, status code, client identity, and timing only.</summary>
public class ApiRequestLog
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int StatusCode { get; set; }
    public string? ClientId { get; set; }
    public string? ClientName { get; set; }
    public string? ClientIp { get; set; }
    public int DurationMs { get; set; }
}
