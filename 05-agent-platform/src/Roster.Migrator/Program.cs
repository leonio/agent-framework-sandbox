using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Roster.Platform.Data;

// The migrator: bring the database up to the latest migration, seed it, exit. Exit code 0 lets the api and runners
// start (the AppHost waits for completion); anything else keeps them waiting and shows the failure in the dashboard.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddRosterDatabase();

using IHost host = builder.Build();

// Started so the OpenTelemetry exporters run and the logs and traces of this short run reach the dashboard.
await host.StartAsync();
ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Roster.Migrator");

try
{
    await using RosterDb db = await host.Services.GetRequiredService<IDbContextFactory<RosterDb>>().CreateDbContextAsync();

    // 1. Schema. MigrateAsync creates the database if needed and applies every pending migration in order; it is a
    //    no-op when the database is current, so running the migrator on every start is safe.
    List<string> pending = [.. await db.Database.GetPendingMigrationsAsync()];
    logger.LogInformation(pending.Count == 0 ? "Database schema is current" : "Applying {Count} migration(s): {Migrations}",
        pending.Count, string.Join(", ", pending));
    await db.Database.MigrateAsync();

    // 2. Seed. A fresh database gets one shared endpoint of kind "fake", so assignments can run before anyone has
    //    configured a real model. Only when no shared endpoint exists, so an admin's changes are never overwritten.
    if (!await db.Endpoints.AnyAsync(e => e.OwnerId == null))
    {
        db.Endpoints.Add(new ModelEndpoint
        {
            Id = Guid.NewGuid(),
            Name = "Fake (offline)",
            Kind = EndpointKind.Fake,
            DefaultModel = "fake",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        logger.LogInformation("Seeded the shared fake endpoint");
    }

    logger.LogInformation("Migrator finished");
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Migration failed");
    Environment.ExitCode = 1;
}
finally
{
    await host.StopAsync();
}
