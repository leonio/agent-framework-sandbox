using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Roster.Platform.Data;

/// <summary>
/// Lets the EF Core tools build a <see cref="RosterDb"/> without a host, so migrations can be added from this project:
/// <code>dotnet ef migrations add &lt;Name&gt; --project src/Roster.Platform</code>
/// Adding a migration never connects, so the connection string is a placeholder. Applying migrations is the migrator
/// host's job, with the real connection string from Aspire.
/// </summary>
public sealed class RosterDbDesignTimeFactory : IDesignTimeDbContextFactory<RosterDb>
{
    public RosterDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RosterDb>()
            .UseNpgsql("Host=localhost;Database=roster;Username=postgres")
            .UseSnakeCaseNamingConvention()
            .Options);
}
