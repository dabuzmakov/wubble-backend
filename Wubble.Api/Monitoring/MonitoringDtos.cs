using System.ComponentModel.DataAnnotations;

namespace Wubble.Api.Monitoring;

public sealed class MonitorRequest
{
    [Required, StringLength(100, MinimumLength = 1)] public string Name { get; init; } = "";
    [MaxLength(2000)] public string? Description { get; init; }
    public bool EmailNotificationsEnabled { get; init; }
}

public sealed class CheckRequest : IValidatableObject
{
    [Required, StringLength(100, MinimumLength = 1)] public string Name { get; init; } = "";
    [Required, MaxLength(2048)] public string Url { get; init; } = "";
    [Required, RegularExpression("^(GET|HEAD)$")] public string Method { get; init; } = "GET";
    [Range(60, 86400)] public int IntervalSeconds { get; init; } = 300;
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 10;
    [Range(100, 599)] public int ExpectedStatusCode { get; init; } = 200;
    [MaxLength(2000)] public string? BodyContains { get; init; }
    [Range(1, 10)] public int FailureThreshold { get; init; } = 3;
    [Range(1, 10)] public int RecoveryThreshold { get; init; } = 1;
    public bool IsEnabled { get; init; } = true;
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!TargetPolicy.TryParse(Url, out _))
            yield return new("Use a public HTTP(S) URL without credentials or a fragment, on port 80 or 443.", [nameof(Url)]);
        if (Method == "HEAD" && !string.IsNullOrEmpty(BodyContains))
            yield return new("HEAD has no response body.", [nameof(BodyContains)]);
    }
}

public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int TotalCount);
public sealed record MonitorResponse(Guid Id, string Name, string? Description, bool EmailNotificationsEnabled,
    DateTimeOffset CreatedAt, string Status, int TotalChecks, int Up, int Down, int Error, int Unknown, int Paused);
public sealed record CheckResponse(Guid Id, Guid MonitorId, string Name, string Url, string Method,
    int IntervalSeconds, int TimeoutSeconds, int ExpectedStatusCode, string? BodyContains,
    int FailureThreshold, int RecoveryThreshold, bool IsEnabled, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? LastCheckedAt, DateTimeOffset? NextRunAt, bool IsRunning);
public sealed record ResultResponse(Guid Id, Guid CheckId, string Trigger, DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt, double DurationMs, string Outcome, int? StatusCode,
    string? ErrorCode, string? ErrorMessage);
public sealed record IncidentResponse(Guid Id, Guid CheckId, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt,
    double DurationSeconds, string Reason, Guid OpenedByResultId, Guid? ClosedByResultId);
public sealed record StatisticsResponse(int TotalRuns, int SuccessfulRuns, int FailedRuns, int InternalErrors,
    double? SuccessRatePercent, double? AverageDurationMs, double? MinDurationMs, double? MaxDurationMs);

internal static class MonitoringMapping
{
    public static CheckResponse ToResponse(this Check c, DateTimeOffset now) => new(c.Id, c.MonitorId,
        c.Name, c.Url, c.Method, c.IntervalSeconds, c.TimeoutSeconds, c.ExpectedStatusCode, c.BodyContains,
        c.FailureThreshold, c.RecoveryThreshold, c.IsEnabled, c.IsEnabled ? c.LastOutcome.ToString() : "Paused",
        c.CreatedAt, c.LastCheckedAt, c.NextRunAt, c.RunLeaseId != null && c.LeaseExpiresAt > now);
    public static ResultResponse ToResponse(this CheckResult r) => new(r.Id, r.CheckId, r.Trigger.ToString(),
        r.StartedAt, r.CompletedAt, r.DurationMs, r.Outcome.ToString(), r.StatusCode, r.ErrorCode, r.ErrorMessage);
    public static IncidentResponse ToResponse(this Incident i, DateTimeOffset now) => new(i.Id, i.CheckId,
        i.OpenedAt, i.ClosedAt, Math.Max(0, ((i.ClosedAt ?? now) - i.OpenedAt).TotalSeconds), i.Reason,
        i.OpenedByResultId, i.ClosedByResultId);
    public static MonitorResponse ToResponse(this Monitor m)
    {
        var active = m.Checks.Where(c => c.IsEnabled).ToArray();
        var up = active.Count(c => c.LastOutcome == CheckOutcome.Up);
        var down = active.Count(c => c.LastOutcome == CheckOutcome.Down);
        var error = active.Count(c => c.LastOutcome == CheckOutcome.Error);
        var unknown = active.Count(c => c.LastOutcome == CheckOutcome.Unknown);
        var status = m.Checks.Count == 0 ? "Unknown" : active.Length == 0 ? "Paused"
            : down > 0 ? "Down" : error > 0 ? "Error" : unknown > 0 ? "Unknown" : "Up";
        return new(m.Id, m.Name, m.Description, m.EmailNotificationsEnabled, m.CreatedAt,
            status, m.Checks.Count, up, down, error, unknown, m.Checks.Count - active.Length);
    }
}
