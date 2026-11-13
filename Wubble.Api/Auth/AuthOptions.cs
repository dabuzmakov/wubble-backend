namespace Wubble.Api.Auth;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; init; } = "wubble-api";
    public string Audience { get; init; } = "wubble-web";

    /// <summary>Секрет HS256, минимум 32 байта. Только через user-secrets / переменные окружения.</summary>
    public string SigningKey { get; init; } = "";

    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Адрес фронтенда — из него строится ссылка подтверждения email.</summary>
    public string FrontendBaseUrl { get; init; } = "";

    /// <summary>
    /// Domain для cookie с access-токеном. Должен быть общим для фронта и API,
    /// иначе фронт не сможет прочитать cookie. Пусто = host-only (подходит для localhost).
    /// </summary>
    public string? AccessTokenCookieDomain { get; init; }

    /// <summary>Скользящий срок: каждая ротация выдаёт токен на этот срок заново.</summary>
    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Окно, в котором повтор только что ротированного токена считается гонкой вкладок,
    /// а не кражей (семья не отзывается).
    /// </summary>
    public TimeSpan RefreshTokenReuseGracePeriod { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan EmailConfirmationTokenLifetime { get; init; } = TimeSpan.FromDays(1);

    public TimeSpan VerificationEmailCooldown { get; init; } = TimeSpan.FromMinutes(1);
}

public sealed class CorsSettings
{
    public const string Section = "Cors";

    /// <summary>Origin фронтенда, например https://wubble.alisaboules.ru или http://localhost:5173.</summary>
    public string[] AllowedOrigins { get; init; } = [];
}
