using Microsoft.AspNetCore.Identity;

namespace Wubble.Api.Users;

/// <summary>
/// Пользователь Wubble. Email, UserName, PasswordHash, EmailConfirmed, lockout и т. д.
/// приходят из IdentityUser; здесь только то, чего в Identity нет.
/// </summary>
public class User : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Когда последний раз отправляли письмо подтверждения (для кулдауна resend).</summary>
    public DateTimeOffset? LastVerificationEmailSentAt { get; set; }
}
