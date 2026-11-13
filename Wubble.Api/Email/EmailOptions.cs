using MailKit.Security;

namespace Wubble.Api.Email;

public sealed class EmailOptions
{
    public const string Section = "Email";

    public string Host { get; init; } = "";
    public int Port { get; init; } = 587;

    /// <summary>None | Auto | SslOnConnect | StartTls | StartTlsWhenAvailable.</summary>
    public SecureSocketOptions Security { get; init; } = SecureSocketOptions.StartTls;

    public string? Username { get; init; }
    public string? Password { get; init; }

    public string FromAddress { get; init; } = "wubble@alisaboules.ru";
    public string FromName { get; init; } = "Wubble";
}
