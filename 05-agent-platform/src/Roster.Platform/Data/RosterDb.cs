using Microsoft.EntityFrameworkCore;

namespace Roster.Platform.Data;

/// <summary>
/// The platform's EF Core model on Postgres. Every table the platform uses is here: people, credentials and
/// endpoints; assignments, phases and findings; the job queue and the event feed; the ledger and agent versions; retros.
/// </summary>
/// <remarks>
/// <para>Conventions applied here and at registration:</para>
/// <list type="bullet">
/// <item>Table and column names are snake_case (<c>model_endpoints.default_model</c>), set with
/// <c>UseSnakeCaseNamingConvention()</c> where the context is registered. Hand-written SQL (the job claim) relies on it.</item>
/// <item>Enums are stored as their names (<c>'AwaitingTriage'</c>), so rows read well in psql and adding a value never
/// renumbers old rows.</item>
/// <item>Every property whose name ends in <c>Json</c> is a <c>jsonb</c> column holding a JSON string. The code reads
/// and writes plain strings; Postgres validates and can index them.</item>
/// <item>Times are <see cref="DateTimeOffset"/> in UTC, stored as <c>timestamptz</c>.</item>
/// </list>
/// <para>Schema changes go through migrations in <c>Migrations/</c>, applied by the migrator host.</para>
/// </remarks>
public sealed class RosterDb(DbContextOptions<RosterDb> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserCredential> Credentials => Set<UserCredential>();
    public DbSet<ModelEndpoint> Endpoints => Set<ModelEndpoint>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<Phase> Phases => Set<Phase>();
    public DbSet<Finding> Findings => Set<Finding>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<RunEvent> RunEvents => Set<RunEvent>();
    public DbSet<Invocation> Invocations => Set<Invocation>();
    public DbSet<AgentVersion> AgentVersions => Set<AgentVersion>();
    public DbSet<RetroSession> RetroSessions => Set<RetroSession>();
    public DbSet<RetroMessage> RetroMessages => Set<RetroMessage>();
    public DbSet<RetroCard> RetroCards => Set<RetroCard>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums as names, one line per enum type.
        configurationBuilder.Properties<CredentialKind>().HaveConversion<string>();
        configurationBuilder.Properties<EndpointKind>().HaveConversion<string>();
        configurationBuilder.Properties<AssignmentState>().HaveConversion<string>();
        configurationBuilder.Properties<PhaseState>().HaveConversion<string>();
        configurationBuilder.Properties<FindingDecision>().HaveConversion<string>();
        configurationBuilder.Properties<JobState>().HaveConversion<string>();
        configurationBuilder.Properties<RetroState>().HaveConversion<string>();
        configurationBuilder.Properties<CardSentiment>().HaveConversion<string>();
        configurationBuilder.Properties<CardState>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        // People, credentials, endpoints. Deleting a person deletes what they own.
        model.Entity<AppUser>(e =>
        {
            e.HasOne<ModelEndpoint>().WithMany().HasForeignKey(u => u.DefaultEndpointId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<UserCredential>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => c.UserId);
        });

        model.Entity<ModelEndpoint>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<UserCredential>().WithMany().HasForeignKey(x => x.CredentialId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.OwnerId);
        });

        // Assignments and everything that hangs off them go when the assignment goes.
        model.Entity<Assignment>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(a => a.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ModelEndpoint>().WithMany().HasForeignKey(a => a.EndpointId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(a => a.Phases).WithOne().HasForeignKey(p => p.AssignmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => new { a.OwnerId, a.CreatedAt });
        });

        model.Entity<Phase>(e => e.HasIndex(p => new { p.AssignmentId, p.Order }).IsUnique());

        model.Entity<Finding>(e =>
        {
            e.HasOne<Assignment>().WithMany().HasForeignKey(f => f.AssignmentId).OnDelete(DeleteBehavior.Cascade);
            // NO ACTION, checked at the end of the statement, so deleting an assignment (which cascades to both
            // findings and invocations) works whatever order Postgres deletes them in.
            e.HasOne<Invocation>().WithMany().HasForeignKey(f => f.InvocationId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(f => f.AssignmentId);
        });

        // The queue. The claim query filters on pool, state and due time, oldest first.
        model.Entity<Job>(e =>
        {
            e.HasIndex(j => new { j.Pool, j.State, j.NotBefore });
            e.HasIndex(j => j.IdempotencyKey).IsUnique(); // Postgres allows many NULLs in a unique index.
        });

        model.Entity<RunEvent>(e =>
        {
            e.HasOne<Assignment>().WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AssignmentId, x.Id });
        });

        // The ledger.
        model.Entity<Invocation>(e =>
        {
            e.HasOne<Assignment>().WithMany().HasForeignKey(i => i.AssignmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(i => i.AssignmentId);
            e.HasIndex(i => new { i.AgentName, i.AgentHash });
        });

        model.Entity<AgentVersion>(e => e.HasIndex(v => new { v.AgentName, v.Hash }).IsUnique());

        // Retros.
        model.Entity<RetroSession>(e =>
        {
            e.HasOne<Assignment>().WithMany().HasForeignKey(s => s.AssignmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.AssignmentId).IsUnique();
        });

        model.Entity<RetroMessage>(e =>
        {
            e.HasOne<RetroSession>().WithMany().HasForeignKey(m => m.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => new { m.SessionId, m.Sequence }).IsUnique();
        });

        model.Entity<RetroCard>(e =>
        {
            e.HasOne<RetroSession>().WithMany().HasForeignKey(c => c.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => c.AssignmentId);
            e.HasIndex(c => new { c.AgentName, c.AgentHash });
        });

        // Every "...Json" property is jsonb.
        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(string) && p.Name.EndsWith("Json", StringComparison.Ordinal)))
            {
                property.SetColumnType("jsonb");
            }
        }
    }
}
