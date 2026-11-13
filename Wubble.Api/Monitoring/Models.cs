using Wubble.Api.Users;

namespace Wubble.Api.Monitoring;

public enum CheckOutcome { Unknown, Up, Down, Error }
public enum RunTrigger { Manual, Scheduled }

public sealed class Monitor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool EmailNotificationsEnabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<Check> Checks { get; set; } = [];
}

public sealed class Check
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MonitorId { get; set; }
    public Monitor Monitor { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Method { get; set; } = "GET";
    public int IntervalSeconds { get; set; } = 300;
    public int TimeoutSeconds { get; set; } = 10;
    public int ExpectedStatusCode { get; set; } = 200;
    public string? BodyContains { get; set; }
    public int FailureThreshold { get; set; } = 3;
    public int RecoveryThreshold { get; set; } = 1;
    public bool IsEnabled { get; set; } = true;
    public CheckOutcome LastOutcome { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int ConsecutiveSuccesses { get; set; }
    // Короткая lease вместо транзакции, открытой на всё время HTTP-запроса.
    public Guid? RunLeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}

public sealed class CheckResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CheckId { get; set; }
    public Check Check { get; set; } = null!;
    public RunTrigger Trigger { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public double DurationMs { get; set; }
    public CheckOutcome Outcome { get; set; }
    public int? StatusCode { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class Incident
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CheckId { get; set; }
    public Check Check { get; set; } = null!;
    public Guid OpenedByResultId { get; set; }
    public Guid? ClosedByResultId { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string Reason { get; set; } = "";
}

// Событие сохраняется в той же транзакции, что и инцидент. Сбой SMTP не теряет событие.
public sealed class IncidentNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MonitorId { get; set; }
    public Monitor Monitor { get; set; } = null!;
    public Guid IncidentId { get; set; }
    public bool IsRecovery { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}
