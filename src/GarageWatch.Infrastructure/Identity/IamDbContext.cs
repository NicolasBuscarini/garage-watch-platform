using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Data.Sqlite;

namespace GarageWatch.Infrastructure.Identity;

public sealed class IamDbContext(DbContextOptions<IamDbContext> options, IPublicIdGenerator publicIds)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    internal bool DeferPublicIdCollisionRetries { get; set; }
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
    public DbSet<AccessProfile> AccessProfiles => Set<AccessProfile>();
    public DbSet<AccessProfileVersion> AccessProfileVersions => Set<AccessProfileVersion>();
    public DbSet<AccessProfilePermission> AccessProfilePermissions => Set<AccessProfilePermission>();
    public DbSet<UserAccessProfileAssignment> UserAccessProfileAssignments => Set<UserAccessProfileAssignment>();
    public DbSet<UserAccessProfileScope> UserAccessProfileScopes => Set<UserAccessProfileScope>();
    public DbSet<IamMigrationState> IamMigrationStates => Set<IamMigrationState>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<ClipAnalysisDispatchPolicy> ClipAnalysisDispatchPolicies => Set<ClipAnalysisDispatchPolicy>();
    public DbSet<ClipAnalysisJob> ClipAnalysisJobs => Set<ClipAnalysisJob>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAddedPublicIds();
        for (var attempt = 0; ; attempt++)
        {
            try { return base.SaveChanges(acceptAllChangesOnSuccess); }
            catch (DbUpdateException exception) when (!DeferPublicIdCollisionRetries && attempt < 4 && IsPublicIdCollision(exception))
            {
                RegenerateAddedPublicIds();
            }
        }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureAddedPublicIds();
        for (var attempt = 0; ; attempt++)
        {
            try { return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
            catch (DbUpdateException exception) when (!DeferPublicIdCollisionRetries && attempt < 4 && IsPublicIdCollision(exception))
            {
                RegenerateAddedPublicIds();
            }
        }
    }

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

        ConfigurePublicId<ApplicationUser>(builder, PublicIdPrefixes.UserPrefix);

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
            ConfigurePublicId(entity, PublicIdPrefixes.ContextPrefix);
        });

        builder.Entity<CameraCatalogEntry>(entity =>
        {
            entity.HasKey(camera => camera.CameraId);
            entity.Property(camera => camera.CameraId).HasMaxLength(64).IsRequired();
            entity.Property(camera => camera.ContextId).HasMaxLength(64).IsRequired();
            entity.Property(camera => camera.Name).HasMaxLength(128).IsRequired();
            entity.Property(camera => camera.Version).IsConcurrencyToken();
            entity.Property(camera => camera.ClipTriggerEnabled).IsRequired();
            entity.Property(camera => camera.ClipTriggerVersion).IsConcurrencyToken();
            entity.Property(camera => camera.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(camera => camera.UpdatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasIndex(camera => new { camera.ContextId, camera.Name });
            ConfigurePublicId(entity, PublicIdPrefixes.CameraPrefix);
            entity.HasOne(camera => camera.Context)
                .WithMany(context => context.Cameras)
                .HasForeignKey(camera => camera.ContextId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AccessProfile>(entity =>
        {
            entity.HasKey(profile => profile.Id);
            entity.Property(profile => profile.Name).HasMaxLength(128).IsRequired();
            entity.Property(profile => profile.Version).IsConcurrencyToken();
            entity.HasIndex(profile => profile.Name).IsUnique();
            ConfigurePublicId(entity, PublicIdPrefixes.AccessProfilePrefix);
        });
        builder.Entity<AccessProfileVersion>(entity =>
        {
            entity.HasKey(version => version.Id);
            entity.HasIndex(version => new { version.ProfileId, version.Version }).IsUnique();
            ConfigurePublicId(entity, PublicIdPrefixes.AccessProfileVersionPrefix);
            entity.HasOne(version => version.Profile).WithMany(profile => profile.Versions)
                .HasForeignKey(version => version.ProfileId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(version => version.Permissions).WithOne(permission => permission.Version)
                .HasForeignKey(permission => permission.AccessProfileVersionId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<AccessProfilePermission>(entity =>
        {
            entity.HasKey(permission => new { permission.AccessProfileVersionId, permission.PermissionKey });
            entity.Property(permission => permission.PermissionKey).HasMaxLength(128).IsRequired();
        });
        builder.Entity<UserAccessProfileAssignment>(entity =>
        {
            entity.HasKey(assignment => assignment.Id);
            entity.HasIndex(assignment => new { assignment.UserId, assignment.AccessProfileVersionId })
                .IsUnique().HasFilter("\"RevokedAtUtc\" IS NULL");
            entity.HasOne(assignment => assignment.User).WithMany()
                .HasForeignKey(assignment => assignment.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(assignment => assignment.ProfileVersion).WithMany()
                .HasForeignKey(assignment => assignment.AccessProfileVersionId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(assignment => assignment.AssignedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(assignment => assignment.RevokedAtUtc).HasConversion(NullableUtcTicksConverter);
            ConfigurePublicId(entity, PublicIdPrefixes.ProfileAssignmentPrefix);
        });
        builder.Entity<UserAccessProfileScope>(entity =>
        {
            entity.HasKey(scope => new { scope.AssignmentId, scope.ContextId, scope.CameraId });
            entity.Property(scope => scope.ContextId).HasMaxLength(64).IsRequired();
            entity.Property(scope => scope.CameraId).HasMaxLength(64).IsRequired();
            entity.HasOne(scope => scope.Assignment).WithMany(assignment => assignment.Scopes)
                .HasForeignKey(scope => scope.AssignmentId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<IamMigrationState>(entity =>
        {
            entity.HasKey(state => state.Key);
            entity.Property(state => state.Key).HasMaxLength(128);
            entity.Property(state => state.CompletedAtUtc).HasConversion(UtcTicksConverter);
        });
        builder.Entity<AccessRequest>(entity =>
        {
            entity.HasKey(request => request.Id);
            entity.Property(request => request.Purpose).HasMaxLength(32).IsRequired();
            entity.Property(request => request.Status).HasMaxLength(24).IsRequired();
            entity.Property(request => request.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasIndex(request => new { request.UserId, request.IdempotencyKey }).IsUnique();
            entity.HasIndex(request => request.UserId).IsUnique().HasFilter("\"Status\" = 'pending'");
            entity.HasIndex(request => new { request.Status, request.CreatedAtUtc });
            entity.HasOne(request => request.User).WithMany().HasForeignKey(request => request.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            ConfigurePublicId(entity, PublicIdPrefixes.AccessRequestPrefix);
        });
        builder.Entity<ClipAnalysisDispatchPolicy>(entity =>
        {
            entity.HasKey(policy => policy.CameraId);
            entity.Property(policy => policy.CameraId).HasMaxLength(64).IsRequired();
            entity.Property(policy => policy.ContextId).HasMaxLength(64).IsRequired();
            entity.Property(policy => policy.Policy).HasMaxLength(32).IsRequired();
            entity.Property(policy => policy.UpdatedByUserId).HasMaxLength(64).IsRequired();
            entity.Property(policy => policy.Version).IsConcurrencyToken();
            entity.Property(policy => policy.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(policy => policy.UpdatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasOne<CameraCatalogEntry>().WithMany().HasForeignKey(policy => policy.CameraId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ClipAnalysisJob>(entity =>
        {
            entity.HasKey(job => job.Id);
            entity.Property(job => job.ClipId).HasMaxLength(64).IsRequired();
            entity.Property(job => job.ContextId).HasMaxLength(64).IsRequired();
            entity.Property(job => job.CameraId).HasMaxLength(64).IsRequired();
            entity.Property(job => job.AnalysisVersion).HasMaxLength(64).IsRequired();
            entity.Property(job => job.State).HasMaxLength(32).IsRequired();
            entity.Property(job => job.RequestKind).HasMaxLength(32).IsRequired();
            entity.Property(job => job.LeaseExpiresAtUtc).HasConversion(NullableUtcTicksConverter);
            entity.Property(job => job.CreatedAtUtc).HasConversion(UtcTicksConverter);
            entity.Property(job => job.UpdatedAtUtc).HasConversion(UtcTicksConverter);
            entity.HasIndex(job => new { job.ClipId, job.AnalysisVersion }).IsUnique();
            entity.HasIndex(job => new { job.ClipId, job.IdempotencyKey }).IsUnique();
            entity.HasIndex(job => new { job.State, job.CreatedAtUtc });
            ConfigurePublicId(entity, PublicIdPrefixes.ClipAnalysisJobPrefix);
        });
    }

    private void EnsureAddedPublicIds()
    {
        foreach (var entry in ChangeTracker.Entries<IHasPublicId>().Where(entry => entry.State == EntityState.Added))
            entry.Entity.PublicId ??= publicIds.Generate(PublicIdPrefixes.For(entry.Entity));
    }

    private void RegenerateAddedPublicIds()
    {
        foreach (var entry in ChangeTracker.Entries<IHasPublicId>().Where(entry => entry.State == EntityState.Added))
        {
            entry.Entity.PublicId = publicIds.Generate(PublicIdPrefixes.For(entry.Entity));
            entry.Property(nameof(IHasPublicId.PublicId)).IsModified = false;
        }
    }

    private static bool IsPublicIdCollision(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 } sqlite &&
        sqlite.Message.Contains("PublicId", StringComparison.OrdinalIgnoreCase);

    private static void ConfigurePublicId<TEntity>(ModelBuilder builder, string prefix)
        where TEntity : class, IHasPublicId => ConfigurePublicId(builder.Entity<TEntity>(), prefix);

    private static void ConfigurePublicId<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity,
        string prefix) where TEntity : class, IHasPublicId
    {
        entity.Property(item => item.PublicId).HasMaxLength(prefix.Length + 12);
        entity.HasIndex(item => item.PublicId).IsUnique().HasFilter("\"PublicId\" IS NOT NULL");
    }
}
