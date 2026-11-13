using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wubble.Api.Monitoring;

internal sealed class MonitorConfiguration : IEntityTypeConfiguration<Monitor>
{
    public void Configure(EntityTypeBuilder<Monitor> b)
    {
        b.ToTable("Monitors");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(2000);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.OwnerId);
    }
}
internal sealed class CheckConfiguration : IEntityTypeConfiguration<Check>
{
    public void Configure(EntityTypeBuilder<Check> b)
    {
        b.ToTable("Checks");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Url).HasMaxLength(2048).IsRequired();
        b.Property(x => x.Method).HasMaxLength(4).IsRequired();
        b.Property(x => x.BodyContains).HasMaxLength(2000);
        b.Property(x => x.LastOutcome).HasConversion<string>().HasMaxLength(16);
        b.HasOne(x => x.Monitor).WithMany(x => x.Checks).HasForeignKey(x => x.MonitorId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.IsEnabled, x.NextRunAt });
    }
}
internal sealed class CheckResultConfiguration : IEntityTypeConfiguration<CheckResult>
{
    public void Configure(EntityTypeBuilder<CheckResult> b)
    {
        b.ToTable("CheckResults");
        b.HasKey(x => x.Id);
        b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Trigger).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.ErrorCode).HasMaxLength(64);
        b.Property(x => x.ErrorMessage).HasMaxLength(500);
        b.HasOne(x => x.Check).WithMany().HasForeignKey(x => x.CheckId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.CheckId, x.StartedAt });
    }
}
internal sealed class IncidentConfiguration : IEntityTypeConfiguration<Incident>
{
    public void Configure(EntityTypeBuilder<Incident> b)
    {
        b.ToTable("Incidents");
        b.HasKey(x => x.Id);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasOne(x => x.Check).WithMany().HasForeignKey(x => x.CheckId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.CheckId, x.OpenedAt });
        b.HasIndex(x => x.CheckId).IsUnique().HasFilter("\"ClosedAt\" IS NULL");
    }
}
internal sealed class IncidentNotificationConfiguration : IEntityTypeConfiguration<IncidentNotification>
{
    public void Configure(EntityTypeBuilder<IncidentNotification> b)
    {
        b.ToTable("IncidentNotifications");
        b.HasKey(x => x.Id);
        b.HasOne(x => x.Monitor).WithMany().HasForeignKey(x => x.MonitorId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.IncidentId, x.IsRecovery }).IsUnique();
        b.HasIndex(x => new { x.SentAt, x.NextAttemptAt });
    }
}
