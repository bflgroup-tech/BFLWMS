namespace Wms.Data.Lpm;

/// <summary>One row of the Nightly Batches admin table. BuildVersion is the app's
/// own Version+LatestPrNumber (same values MainLayout.razor's footer shows),
/// stamped by ScheduledJobService.StartRunAsync from the entry assembly at the
/// moment the run started — lets a stale-instance-ran-old-code incident (see
/// TryAcquireJobLockAsync's doc comment) be spotted directly in this table
/// instead of an ad-hoc SQL comparison against the current source data.</summary>
public record RptJobRunRow(
    long      RunId,
    string    JobName,
    string?   Country,
    string    Mode,
    DateTime  StartTS,
    DateTime? EndTS,
    string    Status,
    int?      RowsProcessed,
    int?      DatesProcessed,
    string?   ErrorMessage,
    string?   TriggeredBy,
    string?   BuildVersion = null);

/// <summary>Country toggle row.</summary>
public record RptCountryConfigRow(string Country, bool IsActive, DateTime UpdatedTS, string UpdatedBy);
