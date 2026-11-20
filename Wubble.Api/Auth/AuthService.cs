using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wubble.Api.Users;

namespace Wubble.Api.Auth;

/// <summary>Бизнес-логика аутентификации. Ничего не знает про HTTP и cookie.</summary>
public sealed class AuthService(
    UserManager<User> users,
    SignInManager<User> signIn,
    TokenService tokens,
    EmailVerificationService verification,
    IOptions<AuthOptions> options,
    TimeProvider time)
{
    public async Task<Result<AuthRegistration>> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim();
        var username = request.Username.Trim();

        // Предпроверки дают понятные 409; гонку двух одновременных регистраций
        // всё равно ловит уникальный индекс Identity (см. MapIdentityErrors).
        if (await users.FindByEmailAsync(email) is not null)
            return AuthErrors.EmailTaken;
        if (await users.FindByNameAsync(username) is not null)
            return AuthErrors.UsernameTaken;

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = username,
            CreatedAt = time.GetUtcNow(),
        };

        IdentityResult created;
        try
        {
            created = await users.CreateAsync(user, request.Password);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "EmailIndex" })
        {
            return AuthErrors.EmailTaken;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UserNameIndex" })
        {
            return AuthErrors.UsernameTaken;
        }
        if (!created.Succeeded)
            return MapIdentityErrors(created);

        var issued = await tokens.IssueForNewSessionAsync(user, ct);

        // Отправляем письмо, но не требуем подтверждения для входа.
        await verification.TrySendAsync(user, ct);

        return new AuthRegistration(
            UserResponse.From(user),
            issued);
    }

    public async Task<Result<IssuedTokens>> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
            return AuthErrors.InvalidCredentials;

        // RequireConfirmedEmail в настройках Identity намеренно выключен:
        // SignInManager проверяет его ДО пароля, и ответ «email не подтверждён»
        // раскрывал бы существование аккаунта. Проверяем вручную после пароля.
        var check = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

        if (check.IsLockedOut)
            return AuthErrors.AccountLocked;
        if (!check.Succeeded)
            return AuthErrors.InvalidCredentials;

        return await tokens.IssueForNewSessionAsync(user, ct);
    }

    public Task<Result<IssuedTokens>> RefreshAsync(string? refreshToken, CancellationToken ct) =>
        tokens.RotateAsync(refreshToken, ct);

    public Task LogoutAsync(string? refreshToken, CancellationToken ct) =>
        tokens.RevokeSessionAsync(refreshToken, ct);

    public async Task<UserResponse?> GetCurrentUserAsync(ClaimsPrincipal principal)
    {
        var user = await users.GetUserAsync(principal);
        return user is null ? null : UserResponse.From(user);
    }

    // null при успехе. Повторное подтверждение уже подтверждённого email — тоже успех.</returns>
    public async Task<AuthError?> VerifyEmailAsync(VerifyEmailRequest request)
    {
        var user = await users.FindByIdAsync(request.UserId.ToString());
        if (user is null)
            return AuthErrors.InvalidVerificationToken;
        if (user.EmailConfirmed)
            return null;

        var token = EmailVerificationService.DecodeToken(request.Token);
        if (token is null)
            return AuthErrors.InvalidVerificationToken;

        var result = await users.ConfirmEmailAsync(user, token);
        return result.Succeeded ? null : AuthErrors.InvalidVerificationToken;
    }

    /// <summary>
    /// Всегда завершается «успешно», чтобы по ответу нельзя было узнать,
    /// зарегистрирован ли email и подтверждён ли он.
    /// </summary>
    public async Task ResendVerificationAsync(ResendVerificationRequest request, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user?.EmailConfirmed is not false)
            return;

        if (user.LastVerificationEmailSentAt is { } lastSent
            && time.GetUtcNow() - lastSent < options.Value.VerificationEmailCooldown)
        {
            return;
        }

        await verification.TrySendAsync(user, ct);
    }

    private static AuthError MapIdentityErrors(IdentityResult result)
    {
        var codes = result.Errors.Select(e => e.Code).ToHashSet();

        if (codes.Contains(nameof(IdentityErrorDescriber.DuplicateEmail)))
            return AuthErrors.EmailTaken;
        if (codes.Contains(nameof(IdentityErrorDescriber.DuplicateUserName)))
            return AuthErrors.UsernameTaken;

        var errors = result.Errors
            .GroupBy(e => FieldFor(e.Code))
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());

        return AuthErrors.Validation(errors);
    }

    private static string FieldFor(string identityErrorCode) => identityErrorCode switch
    {
        _ when identityErrorCode.StartsWith("Password", StringComparison.Ordinal) => "password",
        _ when identityErrorCode.Contains("UserName", StringComparison.Ordinal) => "username",
        _ when identityErrorCode.Contains("Email", StringComparison.Ordinal) => "email",
        _ => "general",
    };
}
