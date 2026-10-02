using System.Text.Json;
using System.Text.Json.Serialization;
using Roster.Api.Auth;
using Roster.Api.Endpoints;
using Roster.Api.Http;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Telemetry, health checks and service discovery. Service discovery matters for sign-in: the AppHost tells this
// process where Keycloak lives (services__keycloak__...), and the OIDC handler finds it as https+http://keycloak.
builder.AddServiceDefaults();

// Cookie session plus Keycloak sign-in, and a fallback policy that makes every endpoint require a signed-in person
// unless it says AllowAnonymous. See Auth/RosterAuth.cs for the whole flow.
builder.AddRosterAuth();

// The platform: database, agent runtime, queue, scenarios, retro, scorecards (see AddRosterPlatform), plus the live
// event stream, which only the API needs (one LISTEN connection serving every browser watching an assignment).
builder.AddRosterPlatform();
builder.Services.AddRosterEventStream();

// JSON on the wire: camelCase (the default) and enums as camelCase strings ("awaitingTriage", "rejected").
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

// Errors and bare status codes (401, 403, 404) come back as RFC 9457 problem details, which the web app can show. The
// platform's "no" exceptions map to 400, 403, 404 and 409 (Http/PlatformExceptionHandler.cs).
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<PlatformExceptionHandler>();

// An OpenAPI document describing every endpoint, at /openapi/v1.json (Development only, and readable without signing
// in there, so tools and the web app's type generation can fetch it).
builder.Services.AddOpenApi();

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Cheap CSRF defence: state-changing requests must carry the X-Roster header. It runs before authentication so a
// forged request is turned away before anything reads the cookie.
app.UseRosterRequestHeader();

// Authentication reads the session cookie (and handles Keycloak's redirect back to /api/auth/signin-oidc);
// authorization then applies the fallback policy and any named policy such as "admin".
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapAuthEndpoints();
app.MapProfileEndpoints();
app.MapCredentialEndpoints();
app.MapModelEndpointApi();
app.MapAgentEndpoints();
app.MapAssignmentEndpoints();
app.MapEventEndpoints();
app.MapRetroEndpoints();

app.Run();
