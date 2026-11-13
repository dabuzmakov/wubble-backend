using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wubble.Api.Auth;
using Wubble.Api.Data;

namespace Wubble.Api.Monitoring;

[ApiController, Authorize, Route("api/monitors")]
public sealed class MonitorsController(AppDbContext db, TimeProvider time) : ControllerBase
{
    private Guid OwnerId => Guid.Parse(User.FindFirst(AuthClaims.UserId)!.Value);

    [HttpGet]
    public async Task<ActionResult<Page<MonitorResponse>>> List(
        [Range(1, 100000)] int page = 1, [Range(1, 100)] int pageSize = 20, CancellationToken ct = default)
    {
        var query = db.Monitors.AsNoTracking().Where(m => m.OwnerId == OwnerId);
        var total = await query.CountAsync(ct);
        var items = await query.Include(m => m.Checks).OrderByDescending(m => m.CreatedAt).ThenBy(m => m.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new Page<MonitorResponse>(items.Select(m => m.ToResponse()).ToArray(), page, pageSize, total);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MonitorResponse>> Get(Guid id, CancellationToken ct)
    {
        var monitor = await db.Monitors.AsNoTracking().Include(m => m.Checks)
            .SingleOrDefaultAsync(m => m.Id == id && m.OwnerId == OwnerId, ct);
        return monitor is null ? NotFound() : monitor.ToResponse();
    }

    [HttpPost]
    [ProducesResponseType<MonitorResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<MonitorResponse>> Create(MonitorRequest request, CancellationToken ct)
    {
        var monitor = new Monitor { OwnerId = OwnerId, Name = request.Name.Trim(),
            Description = request.Description?.Trim(), EmailNotificationsEnabled = request.EmailNotificationsEnabled,
            CreatedAt = time.GetUtcNow() };
        db.Monitors.Add(monitor);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = monitor.Id }, monitor.ToResponse());
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<MonitorResponse>> Update(Guid id, MonitorRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var monitor = await db.LockMonitorAsync(id, ct);
        if (monitor is null || monitor.OwnerId != OwnerId) return NotFound();
        monitor.Name = request.Name.Trim();
        monitor.Description = request.Description?.Trim();
        monitor.EmailNotificationsEnabled = request.EmailNotificationsEnabled;
        await db.SaveChangesAsync(ct);
        await db.Entry(monitor).Collection(m => m.Checks).LoadAsync(ct);
        await tx.CommitAsync(ct);
        return monitor.ToResponse();
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var monitor = await db.LockMonitorAsync(id, ct);
        if (monitor is null || monitor.OwnerId != OwnerId) return NotFound();
        var now = time.GetUtcNow();
        if (await db.Checks.AnyAsync(c => c.MonitorId == id && c.RunLeaseId != null && c.LeaseExpiresAt > now, ct))
            return Problem(statusCode: 409, title: "A check is running. Try again after it completes.",
                extensions: new Dictionary<string, object?> { ["code"] = "already_running" });
        db.Monitors.Remove(monitor);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/incidents")]
    public async Task<ActionResult<Page<IncidentResponse>>> Incidents(Guid id, bool? isOpen = null,
        [Range(1, 100000)] int page = 1, [Range(1, 100)] int pageSize = 20, CancellationToken ct = default)
    {
        if (!await db.Monitors.AnyAsync(m => m.Id == id && m.OwnerId == OwnerId, ct)) return NotFound();
        var query = db.Incidents.AsNoTracking().Where(i => i.Check.MonitorId == id);
        if (isOpen is not null) query = query.Where(i => (i.ClosedAt == null) == isOpen);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(i => i.OpenedAt).ThenBy(i => i.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new Page<IncidentResponse>(items.Select(i => i.ToResponse(time.GetUtcNow())).ToArray(), page, pageSize, total);
    }
}
