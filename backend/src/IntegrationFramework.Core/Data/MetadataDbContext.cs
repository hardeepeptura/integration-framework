using IntegrationFramework.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Core.Data;

public class MetadataDbContext(DbContextOptions<MetadataDbContext> options) : DbContext(options)
{
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<StepRun> StepRuns => Set<StepRun>();
    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<EntityMapping> EntityMappings => Set<EntityMapping>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Workflow>(e =>
        {
            e.HasKey(w => w.Id);
            e.Property(w => w.Name).HasMaxLength(200).IsRequired();
            e.Property(w => w.GraphJson).IsRequired();
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
    }
}
