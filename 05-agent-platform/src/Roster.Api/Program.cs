using Roster.Api.Auth;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Telemetry, health checks and service discovery. Service discovery matters for sign-in: the AppHost tells this
// process where Keycloak lives (services__keycloak__...), and the OIDC handler finds it as https+http://keycloak.
builder.AddServiceDefaults();

// Cookie session plus Keycloak sign-in, and a fallback policy that makes every endpoint require a signed-in person
// unless it says AllowAnonymous. See Auth/RosterAuth.cs for the whole flow.
builder.AddRosterAuth();

// Errors and bare status codes (401, 403, 404) come back as RFC 9457 problem details, which the web app can show.
builder.Services.AddProblemDetails();

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
app.MapAuthEndpoints();

app.Run();
