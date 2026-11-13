using Microsoft.EntityFrameworkCore;
using Wubble.Api.Data;

namespace Wubble.Api.Monitoring;

public sealed record RunResult(CheckResult? Result = null, string? Error = null);

public sealed class CheckRunner(AppDbContext db, ICheckExecutor executor, RunCapacity capacity,
    TimeProvider time, IHostApplicationLifetime lifetime, ILogger<CheckRunner> logger)
{
    public async Task<RunResult> RunAsync(Guid monitorId, Guid checkId, Guid? ownerId, RunTrigger trigger, CancellationToken ct)
    {
        if (!await capacity.Semaphore.WaitAsync(0, ct)) return new(Error: "capacity_reached");
        try
        {
            Check check;
            var lease = Guid.NewGuid();
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                var monitor = await db.LockMonitorAsync(monitorId, ct);
                if (monitor is null || (ownerId != null && monitor.OwnerId != ownerId)) return new(Error: "not_found");
                check = (await db.Checks.SingleOrDefaultAsync(c => c.Id == checkId && c.MonitorId == monitorId, ct))!;
                if (check is null) return new(Error: "not_found");
                var now = time.GetUtcNow();
                if (check.RunLeaseId != null && check.LeaseExpiresAt > now) return new(Error: "already_running");
                if (trigger == RunTrigger.Scheduled && (!check.IsEnabled || check.NextRunAt > now || check.NextRunAt == null))
                    return new(Error: "not_due");
                check.RunLeaseId = lease;
                check.LeaseExpiresAt = now.AddSeconds(check.TimeoutSeconds + 60);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            var started = time.GetUtcNow();
            ProbeResult probe;
            try
            {
                // После claim отключение HTTP-клиента не отменяет сохранение результата.
                probe = await executor.ExecuteAsync(check, lifetime.ApplicationStopping);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                return new(Error: "server_stopping"); // lease восстановится после истечения
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Internal failure executing check {CheckId}", checkId);
                probe = new(CheckOutcome.Error, null, (time.GetUtcNow() - started).TotalMilliseconds,
                    "internal_error", "Wubble could not execute this check.");
            }
            db.ChangeTracker.Clear();
            using var saveDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var saveCt = saveDeadline.Token;
            await using var finish = await db.Database.BeginTransactionAsync(saveCt);
            var parent = await db.LockMonitorAsync(monitorId, saveCt);
            if (parent is null) return new(Error: "not_found");
            check = (await db.Checks.SingleOrDefaultAsync(c => c.Id == checkId, saveCt))!;
            if (check is null || check.RunLeaseId != lease) return new(Error: "lease_lost");
            var result = new CheckResult
            {
                CheckId = checkId, Trigger = trigger, StartedAt = started, CompletedAt = time.GetUtcNow(),
                DurationMs = probe.DurationMs, Outcome = probe.Outcome, StatusCode = probe.StatusCode,
                ErrorCode = probe.ErrorCode, ErrorMessage = probe.ErrorMessage,
            };
            db.CheckResults.Add(result);
            check.LastOutcome = result.Outcome;
            check.LastCheckedAt = result.CompletedAt;
            check.NextRunAt = check.IsEnabled ? result.CompletedAt.AddSeconds(check.IntervalSeconds) : null;
            check.RunLeaseId = null;
            check.LeaseExpiresAt = null;
            await ApplyIncidentAsync(parent, check, result, saveCt);
            await db.SaveChangesAsync(saveCt);
            await finish.CommitAsync(saveCt);
            return new(Result: result);
        }
        finally { capacity.Semaphore.Release(); }
    }

    private async Task ApplyIncidentAsync(Monitor monitor, Check check, CheckResult result, CancellationToken ct)
    {
        // Внутренние/запрещённые запросы не доказывают недоступность целевого сайта.
        if (result.Outcome == CheckOutcome.Error)
        {
            check.ConsecutiveSuccesses = check.ConsecutiveFailures = 0;
            return;
        }
        var incident = await db.Incidents.SingleOrDefaultAsync(i => i.CheckId == check.Id && i.ClosedAt == null, ct);
        if (result.Outcome == CheckOutcome.Down)
        {
            check.ConsecutiveSuccesses = 0;
            check.ConsecutiveFailures = Math.Min(check.ConsecutiveFailures + 1, check.FailureThreshold);
            if (incident is not null || check.ConsecutiveFailures < check.FailureThreshold) return;
            incident = new Incident { CheckId = check.Id, OpenedAt = result.CompletedAt,
                OpenedByResultId = result.Id, Reason = result.ErrorMessage ?? "Check failed." };
            db.Incidents.Add(incident);
            Enqueue(monitor, incident, result.CompletedAt, recovery: false);
        }
        else
        {
            check.ConsecutiveFailures = 0;
            check.ConsecutiveSuccesses = Math.Min(check.ConsecutiveSuccesses + 1, check.RecoveryThreshold);
            if (incident is null || check.ConsecutiveSuccesses < check.RecoveryThreshold) return;
            incident.ClosedAt = result.CompletedAt;
            incident.ClosedByResultId = result.Id;
            Enqueue(monitor, incident, result.CompletedAt, recovery: true);
        }
    }
    private void Enqueue(Monitor monitor, Incident incident, DateTimeOffset now, bool recovery)
    {
        if (!monitor.EmailNotificationsEnabled) return;
        db.IncidentNotifications.Add(new IncidentNotification { MonitorId = monitor.Id,
            IncidentId = incident.Id, IsRecovery = recovery, CreatedAt = now, NextAttemptAt = now });
    }
}
