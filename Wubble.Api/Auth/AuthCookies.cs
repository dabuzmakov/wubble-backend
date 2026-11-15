using Microsoft.Extensions.Options;

namespace Wubble.Api.Auth;

/// <summary>
/// Access-токен — обычная cookie (без HttpOnly): фронт читает её и шлёт в Authorization: Bearer.
/// Сервер access-токен из cookie НЕ читает — только из заголовка, поэтому CSRF через неё невозможен.
/// Refresh-токен — HttpOnly cookie, уходит только на /api/auth.
/// </summary>
public sealed class AuthCookies(IOptions<AuthOptions> options)
{
    public const string AccessTokenName = "wubble_access";
    public const string RefreshTokenName = "wubble_refresh";
    private const string RefreshTokenPath = "/api/auth";

    private readonly AuthOptions _options = options.Value;

    public static string? ReadRefreshToken(HttpRequest request) =>
        request.Cookies[RefreshTokenName];

    public void Append(HttpResponse response, IssuedTokens tokens)
    {
        response.Cookies.Append(AccessTokenName, tokens.AccessToken,
            AccessCookieOptions(tokens.AccessTokenExpiresAt));
        response.Cookies.Append(RefreshTokenName, tokens.RefreshToken,
            RefreshCookieOptions(tokens.RefreshTokenExpiresAt));
    }

    /// <summary>Удаление с теми же Domain/Path, иначе браузер не удалит cookie.</summary>
    public void Clear(HttpResponse response)
    {
        response.Cookies.Delete(AccessTokenName, AccessCookieOptions(expires: null));
        response.Cookies.Delete(RefreshTokenName, RefreshCookieOptions(expires: null));
    }

    private CookieOptions AccessCookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = false,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Domain = string.IsNullOrWhiteSpace(_options.AccessTokenCookieDomain)
            ? null
            : _options.AccessTokenCookieDomain,
        Path = "/",
        Expires = expires,
        IsEssential = true,
    };

    private static CookieOptions RefreshCookieOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = RefreshTokenPath,
        Expires = expires,
        IsEssential = true,
    };
}

/// <summary>
/// Дополнительная защита cookie-эндпоинтов (/refresh, /logout) от CSRF:
/// если браузер прислал Origin, он должен быть в списке разрешённых.
/// Запросы без Origin (curl, тесты) пропускаются.
/// </summary>
public sealed class OriginGuard(IOptions<CorsSettings> cors)
{
    public bool IsAllowed(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        return string.IsNullOrEmpty(origin)
               || cors.Value.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
    }
}
