using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Wubble.Api.Auth;
using Wubble.Api.Data;

namespace Wubble.Api.Monitoring;

public sealed class CheckStateRequest
{
    [Required] public bool? IsEnabled { get; init; }
}

[ApiController, Authorize, Route("api/monitors/{monitorId:guid}/checks")]
public sealed class ChecksController(AppDbContext db, CheckRunner runner, TimeProvider time) : ControllerBase
{
    private Guid OwnerId => Guid.Parse(User.FindFirst(AuthClaims.UserId)!.Value);
    private Task<bool> OwnsAsync(Guid monitorId, CancellationToken ct) =>
        db.Monitors.AnyAsync(m => m.Id == monitorId && m.OwnerId == OwnerId, ct);
    private Task<bool> HasCheckAsync(Guid monitorId, Guid id, CancellationToken ct) =>
        db.Checks.AnyAsync(c => c.Id == id && c.MonitorId == monitorId && c.Monitor.OwnerId == OwnerId, ct);
    private ObjectResult ConflictRunning() => Problem(statusCode: 409, title: "Check is running. Try again after it completes.",
        extensions: new Dictionary<string, object?> { ["code"] = "already_running" });

    [HttpGet]
    public async Task<ActionResult<Page<CheckResponse>>> List(Guid monitorId,
        [Range(1, 100000)] int page = 1, [Range(1, 100)] int pageSize = 20, CancellationToken ct = default)
    {
        if (!await OwnsAsync(monitorId, ct)) return NotFound();
        var query = db.Checks.AsNoTracking().Where(c => c.MonitorId == monitorId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new Page<CheckResponse>(items.Select(c => c.ToResponse(time.GetUtcNow())).ToArray(), page, pageSize, total);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CheckResponse>> Get(Guid monitorId, Guid id, CancellationToken ct)
    {
        var check = await db.Checks.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id
            && c.MonitorId == monitorId && c.Monitor.OwnerId == OwnerId, ct);
        return check is null ? NotFound() : check.ToResponse(time.GetUtcNow());
    }

    [HttpPost]
    [ProducesResponseType<CheckResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CheckResponse>> Create(Guid monitorId, CheckRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var monitor = await db.LockMonitorAsync(monitorId, ct);
        if (monitor is null || monitor.OwnerId != OwnerId) return NotFound();
        if (await db.Checks.CountAsync(c => c.MonitorId == monitorId, ct) >= 100)
            return Problem(statusCode: 409, title: "A monitor can contain at most 100 checks.",
                extensions: new Dictionary<string, object?> { ["code"] = "check_limit" });
        var check = new Check { MonitorId = monitorId, CreatedAt = time.GetUtcNow() };
        Apply(check, request);
        db.Checks.Add(check);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return CreatedAtAction(nameof(Get), new { monitorId, id = check.Id }, check.ToResponse(time.GetUtcNow()));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<CheckResponse>> Update(Guid monitorId, Guid id, CheckRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var monitor = await db.LockMonitorAsync(monitorId, ct);
        if (monitor is null || monitor.OwnerId != OwnerId) return NotFound();
        var check = await db.Checks.SingleOrDefaultAsync(c => c.Id == id && c.MonitorId == monitorId, ct);
        if (check is null) return NotFound();
        if (IsRunning(check)) return ConflictRunning();
        Apply(check, request);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return check.ToResponse(time.GetUtcNow());
    }

    [HttpPatch("{id:guid}/state")]
    public async Task<ActionResult<CheckResponse>> SetState(Guid monitorId, Guid id, CheckStateRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var monitor = await db.LockMonitorAsync(monitorId, ct);
        if (monitor is null || monitor.OwnerId != OwnerId) return NotFound();
        var check = await db.Checks.SingleOrDefaultAsync(c => c.Id == id && c.MonitorId == monitorId, ct);
        if (check is null) return NotFound();
        if (IsRunning(check)) return ConflictRunning();
        check.IsEnabled = request.IsEnabled!.Value;
        check.NextRunAt = check.IsEnabled ? time.GetUtcNow() : null;
        check.ConsecutiveFailures = check.ConsecutiveSuccesses = 0;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return check.ToResponse(time.GetUtcNow());
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid monitorId, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var monitor = await db.LockMonitorAsync(monitorId, ct);
        if (monitor is null || monitor.OwnerId != OwnerId) return NotFound();
        var check = await db.Checks.SingleOrDefaultAsync(c => c.Id == id && c.MonitorId == monitorId, ct);
        if (check is null) return NotFound();
        if (IsRunning(check)) return ConflictRunning();
        await db.IncidentNotifications.Where(n => db.Incidents.Any(i => i.CheckId == id && i.Id == n.IncidentId)).ExecuteDeleteAsync(ct);
        db.Checks.Remove(check);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/run")]
    [EnableRateLimiting("monitoring-run")]
    public async Task<ActionResult<ResultResponse>> Run(Guid monitorId, Guid id, CancellationToken ct)
    {
        if (!await HasCheckAsync(monitorId, id, ct)) return NotFound();
        var result = await runner.RunAsync(monitorId, id, OwnerId, RunTrigger.Manual, ct);
        if (result.Result is { } value) return value.ToResponse();
        if (result.Error == "not_found") return NotFound();
        return Problem(statusCode: result.Error is "capacity_reached" or "server_stopping" ? 503 : 409,
            title: "Check could not be started or completed. Try again later.",
            extensions: new Dictionary<string, object?> { ["code"] = result.Error });
    }

    [HttpGet("{id:guid}/results")]
    public async Task<ActionResult<Page<ResultResponse>>> Results(Guid monitorId, Guid id,
        DateTimeOffset? from = null, DateTimeOffset? to = null,
        [Range(1, 100000)] int page = 1, [Range(1, 100)] int pageSize = 20, CancellationToken ct = default)
    {
        if (!await HasCheckAsync(monitorId, id, ct)) return NotFound();
        from = from?.ToUniversalTime();
        to = to?.ToUniversalTime();
        if (from >= to) return Problem(statusCode: 400, title: "from must be earlier than to.");
        var query = db.CheckResults.AsNoTracking().Where(r => r.CheckId == id);
        if (from != null) query = query.Where(r => r.StartedAt >= from);
        if (to != null) query = query.Where(r => r.StartedAt < to);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(r => r.StartedAt).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new Page<ResultResponse>(items.Select(r => r.ToResponse()).ToArray(), page, pageSize, total);
    }

    [HttpGet("{id:guid}/results/{resultId:guid}")]
    public async Task<ActionResult<ResultResponse>> Result(Guid monitorId, Guid id, Guid resultId, CancellationToken ct)
    {
        var result = await db.CheckResults.AsNoTracking().SingleOrDefaultAsync(r => r.Id == resultId && r.CheckId == id
            && r.Check.MonitorId == monitorId && r.Check.Monitor.OwnerId == OwnerId, ct);
        return result is null ? NotFound() : result.ToResponse();
    }

    [HttpGet("{id:guid}/incidents")]
    public async Task<ActionResult<Page<IncidentResponse>>> Incidents(Guid monitorId, Guid id, bool? isOpen = null,
        [Range(1, 100000)] int page = 1, [Range(1, 100)] int pageSize = 20, CancellationToken ct = default)
    {
        if (!await HasCheckAsync(monitorId, id, ct)) return NotFound();
        var query = db.Incidents.AsNoTracking().Where(i => i.CheckId == id);
        if (isOpen != null) query = query.Where(i => (i.ClosedAt == null) == isOpen);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(i => i.OpenedAt).ThenBy(i => i.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new Page<IncidentResponse>(items.Select(i => i.ToResponse(time.GetUtcNow())).ToArray(), page, pageSize, total);
    }

    [HttpGet("{id:guid}/statistics")]
    public async Task<ActionResult<StatisticsResponse>> Statistics(Guid monitorId, Guid id,
        DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default)
    {
        if (!await HasCheckAsync(monitorId, id, ct)) return NotFound();
        to ??= time.GetUtcNow();
        from ??= to.Value.AddDays(-7);
        from = from?.ToUniversalTime();
        to = to?.ToUniversalTime();
        if (from >= to) return Problem(statusCode: 400, title: "from must be earlier than to.");
        var aggregate = await db.CheckResults.Where(r => r.CheckId == id && r.StartedAt >= from && r.StartedAt < to)
            .GroupBy(r => r.CheckId).Select(g => new
            {
                Total = g.Count(), Up = g.Count(r => r.Outcome == CheckOutcome.Up), Down = g.Count(r => r.Outcome == CheckOutcome.Down),
                Errors = g.Count(r => r.Outcome == CheckOutcome.Error),
                Average = g.Where(r => r.Outcome != CheckOutcome.Error).Average(r => (double?)r.DurationMs),
                Min = g.Where(r => r.Outcome != CheckOutcome.Error).Min(r => (double?)r.DurationMs),
                Max = g.Where(r => r.Outcome != CheckOutcome.Error).Max(r => (double?)r.DurationMs),
            }).SingleOrDefaultAsync(ct);
        return aggregate is null ? new StatisticsResponse(0, 0, 0, 0, null, null, null, null)
            : new StatisticsResponse(aggregate.Total, aggregate.Up, aggregate.Down, aggregate.Errors,
                aggregate.Up + aggregate.Down == 0 ? null : 100.0 * aggregate.Up / (aggregate.Up + aggregate.Down),
                aggregate.Average, aggregate.Min, aggregate.Max);
    }

    private bool IsRunning(Check check) => check.RunLeaseId != null && check.LeaseExpiresAt > time.GetUtcNow();
    private void Apply(Check check, CheckRequest r)
    {
        check.Name = r.Name.Trim(); check.Url = r.Url.Trim(); check.Method = r.Method;
        check.IntervalSeconds = r.IntervalSeconds; check.TimeoutSeconds = r.TimeoutSeconds;
        check.ExpectedStatusCode = r.ExpectedStatusCode; check.BodyContains = string.IsNullOrEmpty(r.BodyContains) ? null : r.BodyContains;
        check.FailureThreshold = r.FailureThreshold; check.RecoveryThreshold = r.RecoveryThreshold;
        check.IsEnabled = r.IsEnabled; check.NextRunAt = r.IsEnabled ? time.GetUtcNow() : null;
        check.LastOutcome = CheckOutcome.Unknown; check.LastCheckedAt = null;
        check.ConsecutiveFailures = check.ConsecutiveSuccesses = 0;
        check.RunLeaseId = null; check.LeaseExpiresAt = null;
    }
}
