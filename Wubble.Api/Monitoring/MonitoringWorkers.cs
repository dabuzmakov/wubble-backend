using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wubble.Api.Data;
using Wubble.Api.Email;

namespace Wubble.Api.Monitoring;

public sealed class MonitoringScheduler(IServiceScopeFactory scopes, IOptions<MonitoringOptions> options,
    TimeProvider time, ILogger<MonitoringScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.SchedulerEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds));
        do
        {
            try { await RunDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Monitoring scheduler iteration failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunDueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        var due = await db.Checks.AsNoTracking().Where(c => c.IsEnabled && c.NextRunAt <= now
                && (c.RunLeaseId == null || c.LeaseExpiresAt <= now))
            .OrderBy(c => c.NextRunAt).ThenBy(c => c.Id)
            .Take(options.Value.MaxConcurrentRuns)
            .Select(c => new { c.MonitorId, c.Id }).ToListAsync(ct);
        await Parallel.ForEachAsync(due, new ParallelOptions
        {
            MaxDegreeOfParallelism = options.Value.MaxConcurrentRuns, CancellationToken = ct,
        }, async (item, token) =>
        {
            using var runScope = scopes.CreateScope();
            try
            {
                await runScope.ServiceProvider.GetRequiredService<CheckRunner>()
                    .RunAsync(item.MonitorId, item.Id, null, RunTrigger.Scheduled, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Scheduled check {CheckId} failed", item.Id); }
        });
    }
}

public sealed class IncidentNotificationWorker(IServiceScopeFactory scopes, TimeProvider time,
    ILogger<IncidentNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try { await SendPendingAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Incident notification iteration failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task SendPendingAsync(CancellationToken ct)
    {
        // Один небольшой пакет за итерацию; SMTP имеет таймаут в SmtpMailSender.
        for (var index = 0; index < 10; index++)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = time.GetUtcNow();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // SKIP LOCKED позволяет нескольким экземплярам выбирать разные события.
            var pending = await db.IncidentNotifications.FromSqlInterpolated($"""
                SELECT n.* FROM "IncidentNotifications" n
                WHERE n."SentAt" IS NULL AND n."Attempts" < 5 AND n."NextAttemptAt" <= {now}
                AND (NOT n."IsRecovery" OR NOT EXISTS (
                    SELECT 1 FROM "IncidentNotifications" earlier
                    WHERE earlier."IncidentId" = n."IncidentId" AND NOT earlier."IsRecovery"
                    AND earlier."SentAt" IS NULL AND earlier."Attempts" < 5))
                ORDER BY "CreatedAt", "IsRecovery" LIMIT 1 FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct);
            var message = pending.SingleOrDefault();
            if (message is null) return;
            var monitor = await db.Monitors.Include(m => m.Owner).SingleOrDefaultAsync(m => m.Id == message.MonitorId, ct);
            var incident = await db.Incidents.Include(i => i.Check).SingleOrDefaultAsync(i => i.Id == message.IncidentId, ct);
            if (monitor is null || incident is null || !monitor.EmailNotificationsEnabled || !monitor.Owner.EmailConfirmed)
            {
                db.IncidentNotifications.Remove(message);
            }
            else
            {
                message.Attempts++;
                try
                {
                    var eventName = message.IsRecovery ? "Восстановление" : "Сбой";
                    var text = $"{eventName}: {monitor.Name} / {incident.Check.Name}\n" +
                               $"Время события (UTC): {message.CreatedAt:O}\n" +
                               (message.IsRecovery ? "Проверка снова проходит." : incident.Reason);
                    var mail = new OutgoingEmail(monitor.Owner.Email!, $"Wubble: {eventName}",
                        $"<pre>{WebUtility.HtmlEncode(text)}</pre>", text);
                    using var deliveryDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deliveryDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                    await scope.ServiceProvider.GetRequiredService<IMailSender>().SendAsync(mail, deliveryDeadline.Token);
                    message.SentAt = time.GetUtcNow();
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Incident notification {Id} attempt {Attempt} failed", message.Id, message.Attempts);
                    message.NextAttemptAt = now.AddMinutes(Math.Pow(2, message.Attempts - 1));
                }
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
    }
}
