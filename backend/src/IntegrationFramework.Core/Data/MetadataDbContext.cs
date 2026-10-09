using IntegrationFramework.Core.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Core.Data;

// IDataProtectionKeyContext: SSO state/cookies are encrypted with DataProtection
// keys stored here so every API replica can decrypt them (multi-replica login).
public class MetadataDbContext(DbContextOptions<MetadataDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<StepRun> StepRuns => Set<StepRun>();
    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<EntityMapping> EntityMappings => Set<EntityMapping>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<WorkflowShare> WorkflowShares => Set<WorkflowShare>();
    public DbSet<RunQueueItem> RunQueueItems => Set<RunQueueItem>();
    public DbSet<Project> Projects => Set<Project>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(p => p.Name).IsUnique();
        });

        modelBuilder.Entity<Workflow>(e =>
        {
            e.HasKey(w => w.Id);
            e.Property(w => w.Name).HasMaxLength(200).IsRequired();
            e.Property(w => w.GraphJson).IsRequired();
            e.HasOne(w => w.Project)
                .WithMany(p => p.Workflows)
                .HasForeignKey(w => w.ProjectId)
                .OnDelete(DeleteBehavior.Restrict); // delete guard is enforced in the API
            e.HasIndex(w => w.ProjectId);
        });

        modelBuilder.Entity<WorkflowRun>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Status).HasMaxLength(20).IsRequired();
            e.HasOne(r => r.Workflow)
                .WithMany(w => w.Runs)
                .HasForeignKey(r => r.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(r => r.StepRuns)
                .WithOne(s => s.Run)
                .HasForeignKey(s => s.RunId)
                .OnDelete(DeleteBehavior.Cascade);
            // Dashboard aggregates over time windows (COUNT/SUM CASE + GROUP BY).
            e.HasIndex(r => new { r.StartedAt, r.Status });
            e.HasIndex(r => new { r.WorkflowId, r.StartedAt });
        });

        modelBuilder.Entity<StepRun>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.NodeId).HasMaxLength(100).IsRequired();
            e.Property(s => s.NodeType).HasMaxLength(50).IsRequired();
            e.Property(s => s.Status).HasMaxLength(20).IsRequired();
        });

        modelBuilder.Entity<Connection>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).HasMaxLength(200).IsRequired();
            e.Property(c => c.Kind).HasMaxLength(10).IsRequired();
            e.Property(c => c.AuthType).HasMaxLength(20).IsRequired();
            e.Property(c => c.DbType).HasMaxLength(20);
        });

        modelBuilder.Entity<EntityMapping>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Name).HasMaxLength(200).IsRequired();
            e.Property(m => m.SourceSystem).HasMaxLength(100);
            e.Property(m => m.TargetSystem).HasMaxLength(100);
            e.Property(m => m.MappingJson).IsRequired();
        });

        modelBuilder.Entity<WebhookEvent>(e =>
        {
            e.HasKey(w => w.Id);
            e.Property(w => w.Status).HasMaxLength(20).IsRequired();
            e.HasIndex(w => w.WorkflowId);
            e.HasIndex(w => w.ReceivedAt);
        });

        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasAlternateKey(u => u.Email);
            e.Property(u => u.Email).HasMaxLength(320).IsRequired();
            e.Property(u => u.DisplayName).HasMaxLength(200);
            e.Property(u => u.Role).HasMaxLength(20).IsRequired();
        });

        modelBuilder.Entity<WorkflowShare>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Email).HasMaxLength(320).IsRequired();
            e.Property(s => s.Permission).HasMaxLength(10).IsRequired();
            e.HasOne(s => s.Workflow)
                .WithMany(w => w.Shares)
                .HasForeignKey(s => s.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);
            // One grant per recipient per workflow.
            e.HasIndex(s => new { s.WorkflowId, s.Email }).IsUnique();
        });

        modelBuilder.Entity<Workflow>(e => e.HasIndex(w => w.OwnerEmail));

        modelBuilder.Entity<RunQueueItem>(e =>
        {
            e.HasKey(q => q.Id);
            e.Property(q => q.TriggerType).HasMaxLength(20).IsRequired();
            e.Property(q => q.Status).HasMaxLength(20).IsRequired();
            e.Property(q => q.DedupeKey).HasMaxLength(200).IsRequired();
            e.Property(q => q.ClaimedBy).HasMaxLength(100);
            // Claim races resolve through this token (one winner per UPDATE).
            e.Property(q => q.Version).IsConcurrencyToken();
            // Claim scan: queued + expired-visibility items oldest-first.
            e.HasIndex(q => new { q.Status, q.EnqueuedAt });
            // One dedupe key per workflow: no double-fired schedules, ever.
            e.HasIndex(q => new { q.WorkflowId, q.DedupeKey }).IsUnique();
        });
    }
}
