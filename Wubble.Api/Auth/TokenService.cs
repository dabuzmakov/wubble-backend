using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Wubble.Api.Data;
using Wubble.Api.Users;

namespace Wubble.Api.Auth;

/// <summary>
/// Выдача access (JWT) и refresh токенов, ротация и отзыв refresh-токенов.
/// </summary>
public sealed class TokenService(
    AppDbContext db,
    IOptions<JwtOptions> jwtOptions,
    IOptions<AuthOptions> authOptions,
    TimeProvider time)
{
    private readonly JwtOptions _jwt = jwtOptions.Value;
    private readonly AuthOptions _auth = authOptions.Value;

    /// <summary>Новый вход: начинается новая семья refresh-токенов.</summary>
    public async Task<IssuedTokens> IssueForNewSessionAsync(User user, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var (refresh, rawRefresh) = CreateRefreshToken(user.Id, familyId: Guid.NewGuid(), now);

        db.RefreshTokens.Add(refresh);
        await db.SaveChangesAsync(ct);

        return BuildTokens(user, refresh.FamilyId, rawRefresh, refresh.ExpiresAt, now);
    }

    /// <summary>
    /// Refresh Token Rotation: старый токен отзывается, выдаётся новый в той же семье.
    /// Повторное предъявление уже отозванного токена = вероятная кража → отзываем всю семью.
    /// </summary>
    public async Task<Result<IssuedTokens>> RotateAsync(string? rawToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken))
            return AuthErrors.InvalidRefreshToken;

        var hash = Hash(rawToken);
        var familyId = await db.RefreshTokens.Where(t => t.TokenHash == hash)
            .Select(t => (Guid?)t.FamilyId).SingleOrDefaultAsync(ct);
        if (familyId is null)
            return AuthErrors.InvalidRefreshToken;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockFamilyAsync(familyId.Value, ct);
        var now = time.GetUtcNow();

        var stored = await db.RefreshTokens
            .AsNoTracking()
            .Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null)
            return AuthErrors.InvalidRefreshToken;

        if (stored.RevokedAt is { } revokedAt)
        {
            var rotatedMomentsAgo = stored.ReplacedById is not null
                                    && now - revokedAt <= _auth.RefreshTokenReuseGracePeriod;
            if (rotatedMomentsAgo)
                return AuthErrors.RefreshTokenAlreadyRotated;

            await RevokeFamilyAsync(stored.FamilyId, now, ct);
            await tx.CommitAsync(ct);
            return AuthErrors.InvalidRefreshToken;
        }

        if (stored.ExpiresAt <= now || !stored.User.EmailConfirmed
            || stored.User.LockoutEnd > now)
            return AuthErrors.InvalidRefreshToken;

        var (next, rawNext) = CreateRefreshToken(stored.UserId, stored.FamilyId, now);

        // Условный UPDATE атомарен: если два запроса пришли одновременно,
        // строку «выиграет» только один, второй получит 0 затронутых строк.
        var won = await db.RefreshTokens
            .Where(t => t.Id == stored.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, (DateTimeOffset?)now)
                .SetProperty(t => t.ReplacedById, (Guid?)next.Id), ct);

        if (won == 0)
            return AuthErrors.RefreshTokenAlreadyRotated; // транзакция откатится при dispose

        db.RefreshTokens.Add(next);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return BuildTokens(stored.User, stored.FamilyId, rawNext, next.ExpiresAt, now);
    }

    /// <summary>Logout: отзывает всю семью, к которой принадлежит токен. Неизвестный токен игнорируется.</summary>
    public async Task RevokeSessionAsync(string? rawToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken))
            return;

        var hash = Hash(rawToken);
        var familyId = await db.RefreshTokens
            .Where(t => t.TokenHash == hash)
            .Select(t => (Guid?)t.FamilyId)
            .SingleOrDefaultAsync(ct);

        if (familyId is { } id)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockFamilyAsync(id, ct);
            await RevokeFamilyAsync(id, time.GetUtcNow(), ct);
            await tx.CommitAsync(ct);
        }
    }

    // Одна транзакционная блокировка для rotation, logout и обнаружения replay.
    // Коллизия ключей только сериализует независимые сессии, не смешивая их данные.
    private Task<int> LockFamilyAsync(Guid familyId, CancellationToken ct)
    {
        var key = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(familyId.ToByteArray());
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
    }

    public Task<bool> IsSessionActiveAsync(Guid userId, Guid familyId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return db.RefreshTokens.AsNoTracking().AnyAsync(t =>
            t.UserId == userId && t.FamilyId == familyId && t.RevokedAt == null
            && t.ExpiresAt > now && t.User.EmailConfirmed
            && (t.User.LockoutEnd == null || t.User.LockoutEnd <= now), ct);
    }

    private Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, (DateTimeOffset?)now), ct);

    private (RefreshToken Entity, string Raw) CreateRefreshToken(Guid userId, Guid familyId, DateTimeOffset now)
    {
        var raw = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var entity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = familyId,
            TokenHash = Hash(raw),
            CreatedAt = now,
            ExpiresAt = now + _auth.RefreshTokenLifetime,
        };
        return (entity, raw);
    }

    private IssuedTokens BuildTokens(User user, Guid familyId, string rawRefresh, DateTimeOffset refreshExpiresAt, DateTimeOffset now)
    {
        var accessExpiresAt = now + _jwt.AccessTokenLifetime;
        var access = CreateAccessToken(user, familyId, now, accessExpiresAt);
        return new IssuedTokens(access, accessExpiresAt, rawRefresh, refreshExpiresAt);
    }

    private string CreateAccessToken(User user, Guid familyId, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
                [AuthClaims.Name] = user.UserName!,
                [AuthClaims.SessionId] = familyId.ToString(),
            },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}

public static class AuthClaims
{
    public const string UserId = JwtRegisteredClaimNames.Sub;
    public const string Name = "name";
    public const string SessionId = "sid";
}
