using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Wubble.Api.Email;

public sealed record OutgoingEmail(string To, string Subject, string HtmlBody, string TextBody);

/// <summary>
/// Абстракция над отправкой почты: в тестах подменяется фейком,
/// локально письма ловит Mailpit (см. docker-compose.yml).
/// </summary>
public interface IMailSender
{
    Task SendAsync(OutgoingEmail email, CancellationToken ct);
}

/// <summary>
/// SMTP через MailKit (System.Net.Mail.SmtpClient Microsoft не рекомендует для нового кода).
/// Подключение на каждое письмо — для объёмов MVP этого достаточно.
/// </summary>
public sealed class SmtpMailSender(IOptions<EmailOptions> options) : IMailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(OutgoingEmail email, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(MailboxAddress.Parse(email.To));
        message.Subject = email.Subject;
        message.Body = new BodyBuilder
        {
            HtmlBody = email.HtmlBody,
            TextBody = email.TextBody,
        }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 10_000 };
        await client.ConnectAsync(_options.Host, _options.Port, _options.Security, ct);

        if (!string.IsNullOrEmpty(_options.Username))
            await client.AuthenticateAsync(_options.Username, _options.Password ?? "", ct);

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}
