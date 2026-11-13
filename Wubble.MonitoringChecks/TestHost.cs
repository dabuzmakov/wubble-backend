using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wubble.Api.Email;
using Wubble.Api.Monitoring;

namespace Wubble.MonitoringChecks;

internal sealed class ApiFactory(string connection) : WebApplicationFactory<Wubble.Api.Auth.AuthController>
{
    public FakeExecutor Executor { get; } = new();
    public FakeMailSender Mail { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connection,
            ["Jwt:SigningKey"] = "wubble-integration-tests-only-64-byte-key-01234567890123456789012345",
            ["Monitoring:SchedulerEnabled"] = "false",
            ["Logging:LogLevel:Default"] = "Warning",
        }));
        builder.ConfigureServices(services =>
        {
            foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService)
                && (s.ImplementationType == typeof(MonitoringScheduler) || s.ImplementationType == typeof(IncidentNotificationWorker))).ToArray())
                services.Remove(service);
            services.RemoveAll<ICheckExecutor>();
            services.AddSingleton<ICheckExecutor>(Executor);
            services.RemoveAll<IMailSender>();
            services.AddSingleton<IMailSender>(Mail);
        });
    }
}
internal sealed class FakeExecutor : ICheckExecutor
{
    public ProbeResult Next { get; set; } = new(CheckOutcome.Up, 200, 12);
    public int Calls;
    public TaskCompletionSource? Started { get; set; }
    public TaskCompletionSource? Release { get; set; }
    public async Task<ProbeResult> ExecuteAsync(Check check, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        Started?.TrySetResult();
        if (Release is { } gate) await gate.Task.WaitAsync(ct);
        return Next;
    }
}
internal sealed class FakeMailSender : IMailSender
{
    public ConcurrentQueue<OutgoingEmail> Sent { get; } = new();
    public bool Fail { get; set; }
    public Task SendAsync(OutgoingEmail email, CancellationToken ct)
    {
        if (Fail) throw new IOException("Simulated SMTP failure");
        Sent.Enqueue(email);
        return Task.CompletedTask;
    }
}
internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
}
