using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SdlcStudio.Api.Ai;
using SdlcStudio.Api.Data;
using SdlcStudio.Api.Domain;
using SdlcStudio.Api.Options;

namespace SdlcStudio.Api.Endpoints;

/// <summary>Admin area: manage the instructions, prompts and skills the agents are built from. No auth, by request.</summary>
public static class AdminEndpoints
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapAdminEndpoints()
        {
            var admin = app.MapGroup("/api/admin/templates").WithTags("Admin");

            admin.MapGet("/", async (StudioDbContext db, CancellationToken ct) =>
                TypedResults.Ok(await db.Templates.AsNoTracking().OrderBy(t => t.Kind).ThenBy(t => t.Key)
                    .Select(t => TemplateDto.From(t)).ToListAsync(ct)));

            admin.MapGet("/{id:int}", async Task<Results<Ok<TemplateDto>, NotFound>> (int id, StudioDbContext db, CancellationToken ct) =>
                await db.Templates.FindAsync([id], ct) is { } t ? TypedResults.Ok(TemplateDto.From(t)) : TypedResults.NotFound());

            admin.MapPost("/", async Task<Results<Created<TemplateDto>, Conflict<string>>> (TemplateRequest req, StudioDbContext db, CancellationToken ct) =>
            {
                if (await db.Templates.AnyAsync(t => t.Key == req.Key, ct))
                    return TypedResults.Conflict($"A template with key '{req.Key}' already exists.");

                var t = new PromptTemplate { Key = req.Key, Title = req.Title, Content = req.Content };
                Apply(t, req);
                db.Templates.Add(t);
                await db.SaveChangesAsync(ct);
                return TypedResults.Created($"/api/admin/templates/{t.Id}", TemplateDto.From(t));
            });

            admin.MapPut("/{id:int}", async Task<Results<Ok<TemplateDto>, NotFound>> (int id, TemplateRequest req, StudioDbContext db, CancellationToken ct) =>
            {
                var t = await db.Templates.FindAsync([id], ct);
                if (t is null) return TypedResults.NotFound();

                Apply(t, req);
                t.Version++; // every edit is a new version; agents carry the version they were built with in their Description
                await db.SaveChangesAsync(ct);
                return TypedResults.Ok(TemplateDto.From(t));
            });

            admin.MapDelete("/{id:int}", async (int id, StudioDbContext db, CancellationToken ct) =>
            {
                await db.Templates.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
                return TypedResults.NoContent();
            });

            // Reload the defaults from Content/*.md (overwrites edits).
            admin.MapPost("/reset", async (PromptLibrary library, IWebHostEnvironment env, CancellationToken ct) =>
                TypedResults.Ok(new { updated = await library.SeedAsync(Path.Combine(AppContext.BaseDirectory, "Content"), overwrite: true, ct) }));

            app.MapGet("/api/meta", (ChatClientFactory llm, Microsoft.Extensions.Options.IOptions<StudioOptions> studio, StudioDbContext db) =>
                TypedResults.Ok(new MetaDto(
                    llm.Provider.ToString(),
                    llm.Model,
                    studio.Value.Integrations.Mode.ToString(),
                    db.Database.ProviderName?.Split('.').Last() ?? "unknown",
                    [.. Enum.GetValues<WorkflowPhase>().Select(p => new PhaseInfoDto(p, p.DisplayName, p.HasApprovalGate))])))
                .WithTags("Meta");

            return app;
        }
    }

    private static void Apply(PromptTemplate t, TemplateRequest req)
    {
        t.Key = req.Key;
        t.Kind = req.Kind;
        t.Title = req.Title;
        t.Description = req.Description ?? "";
        t.Content = req.Content;
        t.Skills = req.Skills ?? "";
        t.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
