using System.ComponentModel.DataAnnotations;
using Wubble.Api.Users;

namespace Wubble.Api.Auth;

public sealed class RegisterRequest
{
    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; init; } = "";

    /// <summary>3–32 символа: латиница, цифры, «_», «-», «.».</summary>
    [Required, RegularExpression(@"^[a-zA-Z0-9_.\-]{3,32}$",
        ErrorMessage = "Username must be 3-32 characters: latin letters, digits, '_', '-', '.'.")]
    public string Username { get; init; } = "";

    [Required, MinLength(8), MaxLength(128)]
    public string Password { get; init; } = "";

    [Required, MaxLength(4096)]
    public string RecaptchaToken { get; init; } = "";
}

public sealed class LoginRequest
{
    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; init; } = "";

    [Required, MaxLength(128)]
    public string Password { get; init; } = "";

    [Required, MaxLength(4096)]
    public string RecaptchaToken { get; init; } = "";
}

public sealed class VerifyEmailRequest
{
    [Required]
    public Guid UserId { get; init; }

    [Required, MaxLength(2048)]
    public string Token { get; init; } = "";
}

public sealed class ResendVerificationRequest
{
    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; init; } = "";
}

public sealed record UserResponse(Guid Id, string Email, string Username, bool EmailConfirmed)
{
    public static UserResponse From(User user) =>
        new(user.Id, user.Email!, user.UserName!, user.EmailConfirmed);
}

/// <summary>Внутренний результат выдачи токенов; наружу уходит только через cookie.</summary>
public sealed record IssuedTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);
