using Microsoft.EntityFrameworkCore;
using SdlcStudio.Api.Ai;

namespace SdlcStudio.Api.Data;

public static class DatabaseSetup
{
    /// <summary>
    /// Registers the DbContext for the configured provider.
    /// </summary>
    /// <remarks>
    /// Default: SQL Server LocalDB (Windows, ships with Visual Studio). <c>Database:Provider = Sqlite</c> is
    /// offered so the sample also runs on macOS / Linux / CI. Both use the same model.
    /// We register a DbContext *factory* (for the activity journal, which writes from parallel executors)
    /// and resolve the scoped DbContext from it, so there is one options configuration.
    /// </remarks>
    public static IServiceCollection AddStudioDatabase(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Database:Provider"] ?? "SqlServer";
        services.AddDbContextFactory<StudioDbContext>(options =>
        {
            if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
                options.UseSqlite(config.GetConnectionString("StudioSqlite"));
            else
                options.UseSqlServer(config.GetConnectionString("Studio"), sql => sql.EnableRetryOnFailure());
        });
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<StudioDbContext>>().CreateDbContext());
        return services;
    }

    /// <summary>Creates the database if needed and seeds the Admin content.</summary>
    /// <remarks>
    /// WHY EnsureCreated instead of EF migrations? It keeps the sample to "F5 and go" on two providers with
    /// no migration files to maintain. For anything long-lived switch to migrations
    /// (<c>dotnet ef migrations add Initial</c> + <c>Database.MigrateAsync()</c>); EnsureCreated cannot evolve
    /// an existing schema. If you change the entities while experimenting, delete the database (or the
    /// .db file) and restart.
    /// </remarks>
    public static async Task InitializeStudioDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        await db.Database.EnsureCreatedAsync();

        var seeded = await scope.ServiceProvider.GetRequiredService<PromptLibrary>()
            .SeedAsync(Path.Combine(AppContext.BaseDirectory, "Content"), overwrite: false);
        app.Logger.LogInformation("Database ready ({Provider}); seeded {Count} new template(s)", db.Database.ProviderName, seeded);
    }
}
