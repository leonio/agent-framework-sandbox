using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SdlcStudio.Api.Data;

/// <summary>
/// The studio database: admin content (prompts/skills/instructions) and the state of every
/// workflow run.
/// </summary>
/// <remarks>
/// Uses a C# 12+ primary constructor. WHY: there is nothing to do in the constructor except hand
/// the options to the base class, so the primary constructor removes boilerplate.
/// </remarks>
public sealed class StudioDbContext(DbContextOptions<StudioDbContext> options) : DbContext(options)
{
    public DbSet<PromptTemplate> Templates => Set<PromptTemplate>();
    public DbSet<WorkflowRun> Runs => Set<WorkflowRun>();
    public DbSet<RunMessage> Messages => Set<RunMessage>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<ReviewFinding> Findings => Set<ReviewFinding>();
    public DbSet<Epic> Epics => Set<Epic>();
    public DbSet<Story> Stories => Set<Story>();
    public DbSet<DevTask> Tasks => Set<DevTask>();
    public DbSet<Checkpoint> Checkpoints => Set<Checkpoint>();
    public DbSet<PhaseDecision> Decisions => Set<PhaseDecision>();
    public DbSet<RunEvent> Events => Set<RunEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Store every enum as its name. WHY: the database stays readable ("AwaitingApproval"
        // rather than 2) and reordering enum members cannot silently corrupt data.
        // ALTERNATIVE: the default int mapping (smaller, but opaque and order-sensitive).
        builder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);

        // SQLite cannot ORDER BY a DateTimeOffset column. Storing ticks keeps the model identical on
        // both providers. SQL Server has a native datetimeoffset type, so we only convert there.
        if (Database.IsSqlite())
        {
            builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<PromptTemplate>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Title).HasMaxLength(200);
        });

        b.Entity<WorkflowRun>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.RunId);
            e.HasMany(x => x.Artifacts).WithOne().HasForeignKey(x => x.RunId);
            e.HasMany(x => x.Findings).WithOne().HasForeignKey(x => x.RunId);
            e.HasMany(x => x.Epics).WithOne().HasForeignKey(x => x.RunId);
            e.HasMany(x => x.Stories).WithOne().HasForeignKey(x => x.RunId);
            e.HasMany(x => x.Tasks).WithOne().HasForeignKey(x => x.RunId);
            e.HasMany(x => x.Checkpoints).WithOne().HasForeignKey(x => x.RunId);
            e.HasMany(x => x.Decisions).WithOne().HasForeignKey(x => x.RunId);
            e.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.RunId);
        });

        // Stories and tasks are also reachable from the run; SQL Server rejects multiple cascade
        // paths, so the epic/story links do not cascade (the run's cascade deletes them anyway).
        b.Entity<Story>().HasOne<Epic>().WithMany().HasForeignKey(x => x.EpicId).OnDelete(DeleteBehavior.NoAction);
        b.Entity<DevTask>().HasOne<Story>().WithMany(x => x.Tasks).HasForeignKey(x => x.StoryId).OnDelete(DeleteBehavior.NoAction);
    }
}
