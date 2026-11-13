using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wubble.Api.Auth;
using Wubble.Api.Data;
using Wubble.Api.Users;

// Используйте отдельную тестовую БД: программа применяет миграции.
var connection = Environment.GetEnvironmentVariable("WUBBLE_TEST_DB")
    ?? throw new InvalidOperationException("Set WUBBLE_TEST_DB to a dedicated PostgreSQL database.");
var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;
var jwt = Options.Create(new JwtOptions { SigningKey = new string('x', 64) });
var auth = Options.Create(new AuthOptions { RefreshTokenReuseGracePeriod = TimeSpan.Zero });
TokenService Service(AppDbContext db) => new(db, jwt, auth, TimeProvider.System);
void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}
async Task<Result<IssuedTokens>> Rotate(string raw)
{
    await using var db = new AppDbContext(dbOptions);
    return await Service(db).RotateAsync(raw, default);
}
async Task Logout(string raw)
{
    await using var db = new AppDbContext(dbOptions);
    await Service(db).RevokeSessionAsync(raw, default);
}
await using var setup = new AppDbContext(dbOptions);
await setup.Database.MigrateAsync();
var id = Guid.NewGuid();
var user = new User { Id = id, UserName = id.ToString(), NormalizedUserName = id.ToString(),
    Email = $"{id}@example.test", NormalizedEmail = $"{id}@EXAMPLE.TEST", EmailConfirmed = true };
setup.Users.Add(user);
await setup.SaveChangesAsync();
try
{
    var tokens = await Service(setup).IssueForNewSessionAsync(user, default);
    var first = await Rotate(tokens.RefreshToken);
    Check(first.Error is null && first.Value!.RefreshToken != tokens.RefreshToken, "Refresh rotates token");
    var replay = await Rotate(tokens.RefreshToken);
    Check(replay.Error is not null, "Old refresh cannot be used again");
    Check((await Rotate(first.Value!.RefreshToken)).Error is not null, "Replay revokes family");

    tokens = await Service(setup).IssueForNewSessionAsync(user, default);
    await Logout(tokens.RefreshToken);
    Check((await Rotate(tokens.RefreshToken)).Error is not null, "Logout revokes refresh");
    var family = await setup.RefreshTokens.AsNoTracking().Where(t => t.UserId == id)
        .OrderByDescending(t => t.CreatedAt).Select(t => t.FamilyId).FirstAsync();
    Check(!await Service(setup).IsSessionActiveAsync(id, family, default), "Logout invalidates JWT session");

    for (var i = 0; i < 20; i++)
    {
        tokens = await Service(setup).IssueForNewSessionAsync(user, default);
        var rotate = Rotate(tokens.RefreshToken);
        await Task.WhenAll(rotate, Logout(tokens.RefreshToken));
        if (rotate.Result.Value is { } issued)
            Check((await Rotate(issued.RefreshToken)).Error is not null, "Concurrent logout revokes replacement");
        Check(!await setup.RefreshTokens.AsNoTracking().AnyAsync(t => t.UserId == id && t.RevokedAt == null),
            "No active token after concurrent refresh/logout");
    }
    tokens = await Service(setup).IssueForNewSessionAsync(user, default);
    var race = await Task.WhenAll(Rotate(tokens.RefreshToken), Rotate(tokens.RefreshToken));
    Check(race.Count(r => r.Error is null) == 1, "Only one concurrent refresh succeeds");
}
finally
{
    await setup.Users.Where(u => u.Id == id).ExecuteDeleteAsync();
}
