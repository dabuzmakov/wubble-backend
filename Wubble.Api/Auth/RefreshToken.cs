using Wubble.Api.Users;

namespace Wubble.Api.Auth;

/// <summary>
/// Запись о выданном refresh-токене. Сам токен в БД не хранится — только его SHA-256.
/// FamilyId объединяет цепочку ротаций одного входа (одной «сессии»):
/// при обнаружении повторного использования отзывается вся семья.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>SHA-256 токена в hex (64 символа).</summary>
    public string TokenHash { get; set; } = null!;

    public Guid FamilyId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Заполнено, если токен ротирован или отозван (logout / подозрение на кражу).</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Id токена, который заменил этот при ротации. Null — отозван без замены.</summary>
    public Guid? ReplacedById { get; set; }
}
