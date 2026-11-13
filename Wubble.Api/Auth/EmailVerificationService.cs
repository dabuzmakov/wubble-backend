using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Wubble.Api.Email;
using Wubble.Api.Users;

namespace Wubble.Api.Auth;

/// <summary>
/// Генерация ссылки подтверждения и отправка письма.
/// Ошибка SMTP не должна ломать регистрацию: пользователь создан и может запросить письмо повторно.
/// </summary>
public sealed class EmailVerificationService(
    UserManager<User> users,
    IMailSender mail,
    IOptions<AuthOptions> options,
    TimeProvider time,
    ILogger<EmailVerificationService> logger)
{
    public async Task TrySendAsync(User user, CancellationToken ct)
    {
        try
        {
            var token = await users.GenerateEmailConfirmationTokenAsync(user);
            // Сырой токен содержит '+', '/', '=' — в URL его нужно закодировать.
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

            var link = $"{options.Value.FrontendBaseUrl.TrimEnd('/')}/verify-email" +
                       $"?userId={user.Id}&token={encodedToken}";

            await mail.SendAsync(EmailTemplates.Verification(user.Email!, user.UserName!, link), ct);

            user.LastVerificationEmailSentAt = time.GetUtcNow();
            await users.UpdateAsync(user);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to send verification email to user {UserId}", user.Id);
        }
    }

    public static string? DecodeToken(string encoded)
    {
        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(encoded));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
