using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wubble.Api.Auth;
using Wubble.Api.Data;
using Wubble.Api.Monitoring;
using Wubble.Api.Users;
using Wubble.MonitoringChecks;

var assertions = 0;
void Assert(bool ok, string description)
{
    if (!ok) throw new Exception("FAIL: " + description);
    assertions++;
    Console.WriteLine("PASS: " + description);
}
async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
{
    if (response.StatusCode != status)
        throw new Exception($"Expected {status}, received {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    return (await response.Content.ReadFromJsonAsync<T>())!;
}

// Executor tests never depend on external sites or DNS availability.
foreach (var address in new[] { "127.0.0.1", "10.0.0.1", "169.254.169.254", "168.63.129.16", "172.16.0.1", "192.168.1.1", "100.64.0.1", "0.0.0.0", "224.0.0.1", "::1", "fe80::1", "fc00::1", "::ffff:127.0.0.1", "2002:7f00:1::", "64:ff9b::7f00:1", "2001:db8::1" })
    Assert(!TargetPolicy.IsPublic(IPAddress.Parse(address)), "SSRF blocked " + address);
Assert(TargetPolicy.IsPublic(IPAddress.Parse("8.8.8.8")) && TargetPolicy.IsPublic(IPAddress.Parse("2606:4700:4700::1111")), "Public IPv4 and IPv6 allowed");
foreach (var url in new[] { "file:///etc/passwd", "http://user:password@example.com", "http://example.com:22", "http://127.1", "http://2130706433", "http://[::1]", "http://localhost", "http://x.local", "https://example.com/#fragment" })
    Assert(!TargetPolicy.TryParse(url, out _), "Unsafe URL rejected " + url);

var probeCheck = new Check { Url = "https://example.com", BodyContains = "healthy" };
using (var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("service healthy") }))))
{
    var executor = new HttpCheckExecutor(http);
    Assert((await executor.ExecuteAsync(probeCheck, default)).Outcome == CheckOutcome.Up, "HTTP code and body match");
    probeCheck.BodyContains = "Healthy";
    Assert((await executor.ExecuteAsync(probeCheck, default)).ErrorCode == "body_mismatch", "Body match is case sensitive");
    probeCheck.ExpectedStatusCode = 204;
    Assert((await executor.ExecuteAsync(probeCheck, default)).ErrorCode == "unexpected_status", "Unexpected status classified");
}
probeCheck.ExpectedStatusCode = 200;
probeCheck.TimeoutSeconds = 1;
using (var http = new HttpClient(new StubHandler(async (_, ct) => { await Task.Delay(5000, ct); return new(HttpStatusCode.OK); })))
    Assert((await new HttpCheckExecutor(http).ExecuteAsync(probeCheck, default)).ErrorCode == "timeout", "Timeout classified");
using (var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', HttpCheckExecutor.MaxBodyBytes + 1)) }))))
    Assert((await new HttpCheckExecutor(http).ExecuteAsync(probeCheck, default)).ErrorCode == "response_too_large", "Oversized body rejected");
using (var http = new HttpClient(new StubHandler((_, _) => throw new HttpRequestException(HttpRequestError.NameResolutionError, "test"))))
    Assert((await new HttpCheckExecutor(http).ExecuteAsync(probeCheck, default)).ErrorCode == "dns_error", "DNS error classified");
using (var http = new HttpClient(new StubHandler((_, _) => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "test"))))
    Assert((await new HttpCheckExecutor(http).ExecuteAsync(probeCheck, default)).ErrorCode == "tls_error", "TLS error classified");
using (var http = new HttpClient(new StubHandler((_, _) => throw new HttpRequestException("test", new BlockedTargetException()))))
    Assert((await new HttpCheckExecutor(http).ExecuteAsync(probeCheck, default)).Outcome == CheckOutcome.Error, "Blocked DNS target is not a site outage");

using (var http = new HttpClient(new SocketsHttpHandler
{
    UseProxy = false,
    ConnectCallback = (_, _) => ValueTask.FromException<Stream>(new BlockedTargetException()),
}))
    Assert((await new HttpCheckExecutor(http).ExecuteAsync(probeCheck, default)).ErrorCode == "blocked_target",
        "Real SocketsHttpHandler preserves blocked-target classification");

var connection = Environment.GetEnvironmentVariable("WUBBLE_TEST_DB")
    ?? throw new InvalidOperationException("Set WUBBLE_TEST_DB to a dedicated PostgreSQL database.");
await using (var migrationDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options))
    await migrationDb.Database.MigrateAsync();
await using var app = new ApiFactory(connection);
var options = new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false };
using var client = app.CreateClient(options);
using var outsider = app.CreateClient(options);
var userIds = new List<Guid>();
async Task SeedUser(HttpClient browser)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    Assert(!db.Database.HasPendingModelChanges(), "Migration snapshot matches model");
    var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
    var id = Guid.NewGuid();
    var user = new User { Id = id, UserName = "test_" + id.ToString("N")[..12], Email = $"{id}@example.test", EmailConfirmed = true };
    var created = await users.CreateAsync(user, "test-password-123");
    if (!created.Succeeded) throw new Exception(string.Join(", ", created.Errors.Select(e => e.Description)));
    userIds.Add(id);
    var tokens = await scope.ServiceProvider.GetRequiredService<TokenService>().IssueForNewSessionAsync(user, default);
    browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
}
async Task WithDb(Func<AppDbContext, Task> action)
{
    using var scope = app.Services.CreateScope();
    await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
}
try
{
    Assert((await client.GetAsync("/api/monitors")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous monitoring access rejected");
    await SeedUser(client);
    await SeedUser(outsider);
    var monitor = await Read<MonitorResponse>(await client.PostAsJsonAsync("/api/monitors", new MonitorRequest { Name = "Shop", EmailNotificationsEnabled = true }), HttpStatusCode.Created);
    Assert(monitor.Status == "Unknown", "Empty monitor has unknown state");
    var root = $"/api/monitors/{monitor.Id}";
    var checks = root + "/checks";
    Assert((await outsider.GetAsync(root)).StatusCode == HttpStatusCode.NotFound, "Foreign monitor hidden");
    Assert((await client.PostAsJsonAsync(checks, new CheckRequest { Name = "Private", Url = "http://127.0.0.1" })).StatusCode == HttpStatusCode.BadRequest, "Private target rejected by API");
    Assert((await client.PostAsJsonAsync(checks, new CheckRequest { Name = "HEAD", Url = "https://example.com", Method = "HEAD", BodyContains = "text" })).StatusCode == HttpStatusCode.BadRequest, "HEAD body assertion rejected");
    Assert((await client.PostAsJsonAsync(checks, new CheckRequest { Name = "Fast", Url = "https://example.com", IntervalSeconds = 1 })).StatusCode == HttpStatusCode.BadRequest, "Interval lower bound enforced");
    var check = await Read<CheckResponse>(await client.PostAsJsonAsync(checks,
        new CheckRequest { Name = "Health", Url = "https://example.com", IsEnabled = false, FailureThreshold = 2 }), HttpStatusCode.Created);
    var endpoint = checks + "/" + check.Id;
    Assert(check.Status == "Paused" && check.NextRunAt == null, "Paused check has no schedule");
    Assert((await outsider.PostAsync(endpoint + "/run", null)).StatusCode == HttpStatusCode.NotFound, "Foreign manual run rejected");
    Assert((await outsider.PutAsJsonAsync(endpoint, new CheckRequest { Name = "stolen", Url = "https://example.com" })).StatusCode == HttpStatusCode.NotFound, "Foreign edit rejected");
    var first = await Read<ResultResponse>(await client.PostAsync(endpoint + "/run", null));
    Assert(first.Outcome == "Up" && first.Trigger == "Manual", "Paused check can run manually");
    var history = await Read<Page<ResultResponse>>(await client.GetAsync(endpoint + "/results"));
    Assert(history.TotalCount == 1 && history.Items[0].Id == first.Id, "Manual result persisted");
    Assert((await outsider.GetAsync(endpoint + "/results")).StatusCode == HttpStatusCode.NotFound, "Foreign history hidden");
    Assert((await client.GetAsync(endpoint + "/results?from=2026-01-01T00:00:00%2B05:00&to=2027-01-01T00:00:00%2B05:00")).StatusCode == HttpStatusCode.OK,
        "History accepts timezone offsets");
    Assert((await client.GetAsync(endpoint + "/statistics?from=2026-01-01T00:00:00%2B05:00&to=2027-01-01T00:00:00%2B05:00")).StatusCode == HttpStatusCode.OK,
        "Statistics accept timezone offsets");
    Assert((await client.GetAsync(endpoint + "/results?from=2026-12-01&to=2026-01-01")).StatusCode == HttpStatusCode.BadRequest, "Inverted history dates rejected");
    app.Executor.Next = new(CheckOutcome.Down, 503, 40, "unexpected_status", "Expected HTTP 200, received 503.");
    await Read<ResultResponse>(await client.PostAsync(endpoint + "/run", null));
    var incidents = await Read<Page<IncidentResponse>>(await client.GetAsync(endpoint + "/incidents"));
    Assert(incidents.TotalCount == 0, "One failure below threshold does not open incident");
    await Read<ResultResponse>(await client.PostAsync(endpoint + "/run", null));
    await Read<ResultResponse>(await client.PostAsync(endpoint + "/run", null));
    incidents = await Read<Page<IncidentResponse>>(await client.GetAsync(endpoint + "/incidents?isOpen=true"));
    Assert(incidents.TotalCount == 1, "Repeated failures create one incident");
    var eventWorker = ActivatorUtilities.CreateInstance<IncidentNotificationWorker>(app.Services);
    app.Mail.Fail = true;
    await eventWorker.SendPendingAsync(default);
    await WithDb(async db =>
    {
        var notification = await db.IncidentNotifications.SingleAsync(n => n.MonitorId == monitor.Id);
        Assert(notification.Attempts == 1 && notification.SentAt == null, "SMTP failure retained for retry");
        notification.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    });
    app.Mail.Fail = false;
    await eventWorker.SendPendingAsync(default);
    Assert(app.Mail.Sent.Count == 1, "Incident email delivered after retry");
    app.Executor.Next = new(CheckOutcome.Error, null, 1, "internal_error", "Wubble error");
    await Read<ResultResponse>(await client.PostAsync(endpoint + "/run", null));
    incidents = await Read<Page<IncidentResponse>>(await client.GetAsync(endpoint + "/incidents?isOpen=true"));
    Assert(incidents.TotalCount == 1, "Internal error does not recover incident");
    app.Executor.Next = new(CheckOutcome.Up, 200, 15);
    await Read<ResultResponse>(await client.PostAsync(endpoint + "/run", null));
    incidents = await Read<Page<IncidentResponse>>(await client.GetAsync(endpoint + "/incidents"));
    Assert(incidents.Items[0].ClosedAt != null && incidents.Items[0].ClosedByResultId != null, "Recovery closes incident");
    await eventWorker.SendPendingAsync(default);
    Assert(app.Mail.Sent.Count == 2, "Recovery email sent once");
    var stats = await Read<StatisticsResponse>(await client.GetAsync(endpoint + "/statistics"));
    Assert(stats.TotalRuns == 6 && stats.SuccessfulRuns == 2 && stats.FailedRuns == 3 && stats.InternalErrors == 1
        && stats.SuccessRatePercent == 40, "Statistics exclude internal errors from success rate");

    // Block one request, then run/edit/delete concurrently through different scopes/connections.
    app.Executor.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    app.Executor.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var running = client.PostAsync(endpoint + "/run", null);
    await app.Executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Assert((await client.PostAsync(endpoint + "/run", null)).StatusCode == HttpStatusCode.Conflict, "Concurrent run rejected");
    Assert((await client.DeleteAsync(root)).StatusCode == HttpStatusCode.Conflict, "Deleting monitor during run rejected");
    Assert((await client.PatchAsJsonAsync(endpoint + "/state", new { isEnabled = true })).StatusCode == HttpStatusCode.Conflict, "Changing running check rejected");
    app.Executor.Release.SetResult();
    await Read<ResultResponse>(await running);
    app.Executor.Started = app.Executor.Release = null;

    // Scheduler obeys pause and next due time; expired lease represents an interrupted server.
    var scheduler = ActivatorUtilities.CreateInstance<MonitoringScheduler>(app.Services);
    var before = app.Executor.Calls;
    await scheduler.RunDueAsync(default);
    Assert(app.Executor.Calls == before, "Scheduler ignores paused checks");
    await Read<CheckResponse>(await client.PatchAsJsonAsync(endpoint + "/state", new { isEnabled = true }));
    await Task.WhenAll(scheduler.RunDueAsync(default), scheduler.RunDueAsync(default));
    Assert(app.Executor.Calls == before + 1, "Concurrent scheduler polls execute once");
    await scheduler.RunDueAsync(default);
    Assert(app.Executor.Calls == before + 1, "Future schedule is not run early");
    await WithDb(async db =>
    {
        var stored = await db.Checks.SingleAsync(c => c.Id == check.Id);
        stored.NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        stored.RunLeaseId = Guid.NewGuid(); stored.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    });
    await scheduler.RunDueAsync(default);
    Assert(app.Executor.Calls == before + 2, "Expired lease and missed schedule recover once");
    history = await Read<Page<ResultResponse>>(await client.GetAsync(endpoint + "/results?pageSize=1"));
    Assert(history.Items.Count == 1 && history.Items[0].Trigger == "Scheduled", "Scheduled results saved and paginated");
    var summary = await Read<MonitorResponse>(await client.GetAsync(root));
    Assert(summary.Status == "Up" && summary.Up == 1, "Monitor summary reflects check outcome");
    Assert((await outsider.DeleteAsync(root)).StatusCode == HttpStatusCode.NotFound, "Foreign delete rejected");
    Assert((await client.DeleteAsync(root)).StatusCode == HttpStatusCode.NoContent, "Owner can delete idle monitor");
    await WithDb(async db => Assert(!await db.CheckResults.AnyAsync(r => r.CheckId == check.Id)
        && !await db.Incidents.AnyAsync(i => i.CheckId == check.Id)
        && !await db.IncidentNotifications.AnyAsync(n => n.MonitorId == monitor.Id), "Deletion cascades to history, incidents and notifications"));
    Console.WriteLine($"Completed: {assertions} assertions passed.");
}
finally
{
    // Do not delete the test database or any users outside this run.
    await WithDb(async db => await db.Users.Where(u => userIds.Contains(u.Id)).ExecuteDeleteAsync());
}
