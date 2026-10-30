using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GarageWatch.Infrastructure.Identity;

public sealed class IamDbContext(DbContextOptions<IamDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    private static readonly ValueConverter<DateTimeOffset, long> UtcTicksConverter = new(
        value => value.UtcTicks,
        value => new DateTimeOffset(value, TimeSpan.Zero));
    private static readonly ValueConverter<DateTimeOffset?, long?> NullableUtcTicksConverter = new(
        value => value.HasValue ? value.Value.UtcTicks : null,
        value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);

    public DbSet<CameraAccessGrant> CameraAccessGrants => Set<CameraAccessGrant>();
    public DbSet<CameraContext> CameraContexts => Set<CameraContext>();
    public DbSet<CameraCatalogEntry> CameraCatalog => Set<CameraCatalogEntry>();
    public DbSet<IamAuditEvent> IamAuditEvents => Set<IamAuditEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<CameraAccessGrant>(entity =>
        {
            entity.HasKey(grant => grant.Id);
            entity.Property(grant => grant.ContextId).HasMaxLength(64).IsRequired();
            entity.Property(grant => grant.CameraId).HasMaxLength(64).IsRequired();
            entity.Property(grant => grant.GrantedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(grant => grant.RevokedAtUtc).HasConversion(NullableUtcTicksConverter);
            entity.HasIndex(grant => new { grant.UserId, grant.CameraId })
                .IsUnique()
                .HasFilter("\"RevokedAtUtc\" IS NULL");
            entity.HasOne(grant => grant.User)
                .WithMany()
                .HasForeignKey(grant => grant.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<IamAuditEvent>(entity =>
        {
            entity.HasKey(audit => audit.Id);
            entity.Property(audit => audit.Action).HasMaxLength(64).IsRequired();
            entity.Property(audit => audit.ResourceId).HasMaxLength(64);
            entity.Property(audit => audit.OccurredAtUtc).HasConversion(UtcTicksConverter);
            entity.HasIndex(audit => new { audit.SubjectUserId, audit.OccurredAtUtc });
            entity.HasOne(audit => audit.Actor)
                .WithMany()
                .HasForeignKey(audit => audit.ActorUserId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(audit => audit.Subject)
                .WithMany()
                .HasForeignKey(audit => audit.SubjectUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<CameraContext>(entity =>
        {
            entity.HasKey(context => context.ContextId);
            entity.Property(context => context.ContextId).HasMaxLength(64).IsRequired();
            entity.Property(context => context.Name).HasMaxLength(128).IsRequired();
            entity.Property(context => context.Version).IsConcurrencyToken();
            entity.Property(context => context.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(context => context.UpdatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasIndex(context => context.Name);
        });

        builder.Entity<CameraCatalogEntry>(entity =>
        {
            entity.HasKey(camera => camera.CameraId);
            entity.Property(camera => camera.CameraId).HasMaxLength(64).IsRequired();
            entity.Property(camera => camera.ContextId).HasMaxLength(64).IsRequired();
            entity.Property(camera => camera.Name).HasMaxLength(128).IsRequired();
            entity.Property(camera => camera.Version).IsConcurrencyToken();
            entity.Property(camera => camera.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(camera => camera.UpdatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasIndex(camera => new { camera.ContextId, camera.Name });
            entity.HasOne(camera => camera.Context)
                .WithMany(context => context.Cameras)
                .HasForeignKey(camera => camera.ContextId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
