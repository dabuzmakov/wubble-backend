using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Wubble.Api.Monitoring;

public sealed class BlockedTargetException : Exception
{
    public BlockedTargetException() : base("Only public HTTP(S) targets on ports 80 and 443 are allowed.") { }
}

public static class TargetPolicy
{
    public static bool TryParse(string value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048
            || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https") || parsed.Port is not (80 or 443)
            || parsed.UserInfo.Length != 0 || parsed.Fragment.Length != 0 || parsed.Host.Length == 0)
            return false;
        var host = parsed.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || new[] { ".localhost", ".local", ".internal" }.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && !IsPublic(ip))
            return false;
        uri = parsed;
        return true;
    }

    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || (b[0] == 168 && b[1] == 63 && b[2] == 129 && b[3] == 16)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && (b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2) || (b[1] == 88 && b[2] == 99)))
                || (b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100)))
                || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }
        if (ip.AddressFamily != AddressFamily.InterNetworkV6 || (b[0] & 0xe0) != 0x20) return false;
        return !(b[0] == 0x20 && b[1] == 0x01 && (b[2] <= 1 || (b[2] == 0x0d && b[3] == 0xb8))
            || (b[0] == 0x20 && b[1] == 0x02) || (b[0] == 0x3f && (b[1] & 0xf0) == 0xf0));
    }

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        // DNS проверяется непосредственно перед соединением, а соединение идёт к проверенному IP.
        if (addresses.Length == 0 || addresses.Any(ip => !IsPublic(ip))) throw new BlockedTargetException();
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("Unable to connect to target.");
    }
}

public sealed record ProbeResult(CheckOutcome Outcome, int? StatusCode, double DurationMs,
    string? ErrorCode = null, string? ErrorMessage = null);

public interface ICheckExecutor
{
    Task<ProbeResult> ExecuteAsync(Check check, CancellationToken ct);
}

public sealed class HttpCheckExecutor(HttpClient client) : ICheckExecutor
{
    public const int MaxBodyBytes = 1024 * 1024;
    public async Task<ProbeResult> ExecuteAsync(Check check, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(check.TimeoutSeconds));
        try
        {
            if (!TargetPolicy.TryParse(check.Url, out var uri)) throw new BlockedTargetException();
            using var request = new HttpRequestMessage(new HttpMethod(check.Method), uri);
            request.Headers.UserAgent.ParseAdd("Wubble/1.0");
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var status = (int)response.StatusCode;
            if (status != check.ExpectedStatusCode)
                return new(CheckOutcome.Down, status, watch.Elapsed.TotalMilliseconds, "unexpected_status",
                    $"Expected HTTP {check.ExpectedStatusCode}, received {status}.");
            if (!string.IsNullOrEmpty(check.BodyContains))
            {
                if (response.Content.Headers.ContentLength > MaxBodyBytes)
                    return TooLarge(status, watch.Elapsed.TotalMilliseconds);
                if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    return new(CheckOutcome.Down, status, watch.Elapsed.TotalMilliseconds, "unsupported_encoding",
                        "Server returned a compressed body despite Accept-Encoding: identity.");
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                using var data = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxBodyBytes + 1 - (int)data.Length)), deadline.Token);
                    if (read == 0) break;
                    data.Write(buffer, 0, read);
                    if (data.Length > MaxBodyBytes) return TooLarge(status, watch.Elapsed.TotalMilliseconds);
                }
                var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
                Encoding encoding;
                try { encoding = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
                catch (ArgumentException) { encoding = Encoding.UTF8; }
                var text = encoding.GetString(data.GetBuffer(), 0, (int)data.Length);
                if (!text.Contains(check.BodyContains, StringComparison.Ordinal))
                    return new(CheckOutcome.Down, status, watch.Elapsed.TotalMilliseconds, "body_mismatch", "Expected text was not found.");
            }
            return new(CheckOutcome.Up, status, watch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(CheckOutcome.Down, null, watch.Elapsed.TotalMilliseconds, "timeout", "Target did not respond within the configured timeout.");
        }
        catch (Exception ex) when (ex is BlockedTargetException || ex.InnerException is BlockedTargetException)
        {
            return new(CheckOutcome.Error, null, watch.Elapsed.TotalMilliseconds, "blocked_target", "Target address is not permitted.");
        }
        catch (HttpRequestException ex)
        {
            var code = ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "dns_error",
                HttpRequestError.SecureConnectionError => "tls_error",
                _ => "network_error",
            };
            return new(CheckOutcome.Down, null, watch.Elapsed.TotalMilliseconds, code, "HTTP request to target failed.");
        }
        catch (IOException)
        {
            return new(CheckOutcome.Down, null, watch.Elapsed.TotalMilliseconds, "network_error", "Reading the response failed.");
        }
    }
    private static ProbeResult TooLarge(int status, double duration) => new(CheckOutcome.Down, status, duration,
        "response_too_large", "Response body exceeds the 1 MiB limit for content checks.");
}
