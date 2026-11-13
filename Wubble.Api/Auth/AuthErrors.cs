namespace Wubble.Api.Auth;

/// <summary>
/// Ошибка бизнес-операции. Code — машиночитаемый идентификатор для фронтенда
/// (попадает в ProblemDetails.extensions.code), Title — текст для разработчика.
/// </summary>
public sealed record AuthError(
    int StatusCode,
    string Code,
    string Title,
    IReadOnlyDictionary<string, string[]>? Errors = null);

public static class AuthErrors
{
    public static readonly AuthError EmailTaken =
        new(409, "email_taken", "An account with this email already exists.");

    public static readonly AuthError UsernameTaken =
        new(409, "username_taken", "This username is already taken.");

    public static readonly AuthError InvalidCredentials =
        new(401, "invalid_credentials", "Invalid email or password.");

    public static readonly AuthError EmailNotConfirmed =
        new(403, "email_not_confirmed", "Email address is not confirmed.");

    public static readonly AuthError AccountLocked =
        new(429, "account_locked", "Too many failed login attempts. Try again later.");

    public static readonly AuthError InvalidRefreshToken =
        new(401, "invalid_refresh_token", "Session has expired. Please sign in again.");

    /// <summary>
    /// Токен только что ротирован параллельным запросом (другая вкладка).
    /// Фронт должен дождаться другого refresh и повторить запрос один раз.
    /// </summary>
    public static readonly AuthError RefreshTokenAlreadyRotated =
        new(409, "refresh_token_already_rotated", "Token was just refreshed by another request. Retry.");

    public static readonly AuthError InvalidVerificationToken =
        new(400, "invalid_or_expired_token", "Verification link is invalid or has expired.");

    public static readonly AuthError ForbiddenOrigin =
        new(403, "forbidden_origin", "Request origin is not allowed.");

    public static AuthError Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(400, "validation_error", "One or more validation errors occurred.", errors);
}

/// <summary>Результат операции: либо значение, либо ошибка.</summary>
public readonly record struct Result<T>(T? Value, AuthError? Error)
{
    public static implicit operator Result<T>(T value) => new(value, null);
    public static implicit operator Result<T>(AuthError error) => new(default, error);
}
