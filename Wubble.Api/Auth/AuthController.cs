using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;

namespace Wubble.Api.Auth;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService auth, AuthCookies cookies, OriginGuard originGuard) : ControllerBase
{
    /// <summary>Регистрация. Токены не выдаются — сначала нужно подтвердить email.</summary>
    [HttpPost("register")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType<UserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var result = await auth.RegisterAsync(request, ct);
        return result.Error is { } error
            ? ToProblem(error)
            : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>Вход. Токены приходят в cookie wubble_access и wubble_refresh.</summary>
    [HttpPost("login")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        if (!originGuard.IsAllowed(Request))
            return ToProblem(AuthErrors.ForbiddenOrigin);

        var result = await auth.LoginAsync(request, ct);
        if (result.Error is { } error)
            return ToProblem(error);

        cookies.Append(Response, result.Value!);
        return NoContent();
    }

    /// <summary>
    /// Обновление токенов по refresh-cookie. 409 — токен только что обновила другая вкладка:
    /// дождитесь завершения другого refresh и повторите запрос один раз.
    /// </summary>
    [HttpPost("refresh")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        if (!originGuard.IsAllowed(Request))
            return ToProblem(AuthErrors.ForbiddenOrigin);

        var result = await auth.RefreshAsync(AuthCookies.ReadRefreshToken(Request), ct);

        if (result.Error is { } error)
        {
            // При гонке вкладок cookie НЕ трогаем: в браузере уже лежит новый валидный токен.
            if (error.Code != AuthErrors.RefreshTokenAlreadyRotated.Code)
                cookies.Clear(Response);
            return ToProblem(error);
        }

        cookies.Append(Response, result.Value!);
        return NoContent();
    }

    /// <summary>Выход. Работает и с истёкшим access-токеном — аутентификация по refresh-cookie.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (!originGuard.IsAllowed(Request))
            return ToProblem(AuthErrors.ForbiddenOrigin);

        await auth.LogoutAsync(AuthCookies.ReadRefreshToken(Request), ct);
        cookies.Clear(Response);
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Me()
    {
        var user = await auth.GetCurrentUserAsync(User);
        return user is null ? Unauthorized() : Ok(user);
    }

    [HttpPost("verify-email")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> VerifyEmail(VerifyEmailRequest request)
    {
        var error = await auth.VerifyEmailAsync(request);
        return error is null ? NoContent() : ToProblem(error);
    }

    /// <summary>Всегда 202, независимо от того, существует ли email.</summary>
    [HttpPost("resend-verification")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ResendVerification(ResendVerificationRequest request, CancellationToken ct)
    {
        await auth.ResendVerificationAsync(request, ct);
        return Accepted();
    }

    private ObjectResult ToProblem(AuthError error)
    {
        ProblemDetails problem;

        if (error.Errors is null)
        {
            problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, error.StatusCode, error.Title);
        }
        else
        {
            var modelState = new ModelStateDictionary();
            foreach (var (field, messages) in error.Errors)
                foreach (var message in messages)
                    modelState.AddModelError(field, message);

            problem = ProblemDetailsFactory.CreateValidationProblemDetails(
                HttpContext, modelState, error.StatusCode, error.Title);
        }

        problem.Extensions["code"] = error.Code;

        return new ObjectResult(problem)
        {
            StatusCode = error.StatusCode,
            ContentTypes = { "application/problem+json" },
        };
    }
}

public static class RateLimitPolicies
{
    public const string Auth = "auth";
}
