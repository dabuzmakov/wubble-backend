using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wubble.Api.Auth;
using Wubble.Api.Monitoring;
using Wubble.Api.Users;

namespace Wubble.Api.Data;

/// <summary>
/// IdentityUserContext, а не IdentityDbContext: глобальные роли нам не нужны
/// (роли Owner/Editor/Viewer будут внутри Monitor), поэтому таблиц ролей не создаём.
/// IDataProtectionKeyContext — ключи DataProtection живут в PostgreSQL, чтобы ссылки
/// подтверждения email не ломались после перезапуска контейнера.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityUserContext<User, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<Wubble.Api.Monitoring.Monitor> Monitors => Set<Wubble.Api.Monitoring.Monitor>();
    public DbSet<Check> Checks => Set<Check>();
    public DbSet<CheckResult> CheckResults => Set<CheckResult>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentNotification> IncidentNotifications => Set<IncidentNotification>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<User>().HasIndex(u => u.NormalizedEmail).HasDatabaseName("EmailIndex").IsUnique();
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("RefreshTokens");
        b.HasKey(t => t.Id);

        b.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => t.FamilyId);
        b.HasIndex(t => t.UserId);

        // ReplacedById намеренно без внешнего ключа: это просто ссылка для диагностики цепочки.
        b.HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
