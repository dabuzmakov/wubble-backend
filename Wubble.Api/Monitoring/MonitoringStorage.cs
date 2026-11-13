using Microsoft.EntityFrameworkCore;
using Wubble.Api.Data;

namespace Wubble.Api.Monitoring;

public static class MonitoringStorage
{
    // Один порядок блокировок для claim, завершения, изменения и удаления: сначала Monitor.
    public static async Task<Monitor?> LockMonitorAsync(this AppDbContext db, Guid id, CancellationToken ct)
        => (await db.Monitors.FromSqlInterpolated($"SELECT * FROM \"Monitors\" WHERE \"Id\" = {id} FOR UPDATE")
            .ToListAsync(ct)).SingleOrDefault();
}

public sealed class MonitoringOptions
{
    public int MaxConcurrentRuns { get; init; } = 10;
    public int PollIntervalSeconds { get; init; } = 5;
    public bool SchedulerEnabled { get; init; } = true;
}

public sealed class RunCapacity(Microsoft.Extensions.Options.IOptions<MonitoringOptions> options) : IDisposable
{
    public SemaphoreSlim Semaphore { get; } = new(options.Value.MaxConcurrentRuns);
    public void Dispose() => Semaphore.Dispose();
}
