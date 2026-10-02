using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Roster.Api.Auth;

public sealed class KeycloakSettings
{
    public const string SectionName = "Keycloak";

    /// <summary>Name of the Keycloak resource in the AppHost. Service discovery turns it into a URL.</summary>
    public string ServiceName { get; set; } = "keycloak";

    public string Realm { get; set; } = "roster";

    public string ClientId { get; set; } = "roster-api";

    /// <summary>The confidential client's secret. The AppHost generates it and hands it to both Keycloak and the API.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Optional explicit authority, e.g. <c>https://id.example.com/realms/roster</c>. Leave empty under Aspire, where
    /// the authority comes from service discovery. Required outside Development, which insists on HTTPS metadata.
    /// </summary>
    public string? Authority { get; set; }
}

/// <summary>
/// Sign-in for the web app, backend-for-frontend style: the API runs the OpenID Connect code flow against Keycloak and
/// keeps the session in an HttpOnly cookie. The browser never sees a token, and <c>EventSource</c> (which cannot send
/// an Authorization header) just works for the live event stream.
/// </summary>
/// <remarks>
/// <para>The round trip, as the browser sees it:</para>
/// <list type="number">
/// <item>The web app navigates to <c>/api/auth/login?returnUrl=/somewhere</c> (or <c>/register</c>).</item>
/// <item>The OIDC handler redirects to Keycloak's authorize endpoint with a PKCE challenge, a nonce and a state, and
/// drops short-lived correlation and nonce cookies so it can check the answer later.</item>
/// <item>The person signs in (or registers) on Keycloak's own page. Roster never sees the password.</item>
/// <item>Keycloak redirects to <c>/api/auth/signin-oidc?code=...</c>. The handler swaps the code for tokens over the
/// back channel using the client secret and the PKCE verifier, validates the id_token, and signs the person in to the
/// cookie scheme. The cookie (<c>roster</c>, chunked into <c>rosterC1</c>, <c>rosterC2</c> because it carries the
/// tokens) is HttpOnly and SameSite=Lax.</item>
/// <item>The browser lands back on <c>returnUrl</c>. From then on every API call is cookie-authenticated, and
/// <c>/api/auth/me</c> says who is signed in.</item>
/// </list>
/// <para>Keycloak owns identity and the permission roles (<c>admin</c>, <c>member</c>, as realm roles in a <c>roles</c>
/// claim). The person's free-text title belongs to the app's own profile, not to Keycloak.</para>
/// </remarks>
public static class RosterAuth
{
    public const string AdminPolicy = "admin";

    /// <summary>Header every unsafe request must carry; a cross-site form cannot set it. See design doc section 10.</summary>
    public const string RequestHeader = "X-Roster";

    private const string PathBase = "/api/auth";

    // The realm's protocol mapper copies realm roles into a flat "roles" claim (Keycloak's default puts them under
    // realm_access.roles, in the access token only). Keycloak also adds its own built-in roles such as offline_access,
    // so /me filters down to the two that mean something to Roster.
    private const string RoleClaim = "roles";
    private static readonly string[] AppRoles = ["admin", "member"];

    public static WebApplicationBuilder AddRosterAuth(this WebApplicationBuilder builder)
    {
        KeycloakSettings keycloak = builder.Configuration.GetSection(KeycloakSettings.SectionName).Get<KeycloakSettings>() ?? new();
        if (string.IsNullOrEmpty(keycloak.ClientSecret))
        {
            throw new InvalidOperationException(
                "Keycloak:ClientSecret is not set. The AppHost passes it; outside Aspire set Keycloak__ClientSecret.");
        }

        bool isDevelopment = builder.Environment.IsDevelopment();

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "roster";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
                options.SlidingExpiration = true;

                // An API answers with status codes; the web app decides when to send someone to sign in.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            })
            .AddKeycloakOpenIdConnect(keycloak.ServiceName, keycloak.Realm, options =>
            {
                if (!string.IsNullOrEmpty(keycloak.Authority))
                {
                    options.Authority = keycloak.Authority;
                }

                // Service discovery hands out https+http://keycloak, which the HTTPS metadata check rejects. Fine for
                // local runs; anywhere else, set Keycloak:Authority to an https URL.
                options.RequireHttpsMetadata = !isDevelopment;

                options.ClientId = keycloak.ClientId;
                options.ClientSecret = keycloak.ClientSecret;
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.Scope.Add("email");

                // The id_token is kept for the id_token_hint on sign-out, which lets Keycloak end its session without
                // asking. Claims come from the id_token, under their JWT names.
                options.SaveTokens = true;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.RoleClaimType = RoleClaim;

                // Under /api so the web app's dev proxy forwards them and the cookie lands on the web app's origin.
                options.CallbackPath = PathBase + "/signin-oidc";
                options.SignedOutCallbackPath = PathBase + "/signout-callback-oidc";
                options.SignedOutRedirectUri = "/";

                // Code in the query string (PKCE protects it) and Lax cookies, so local runs over plain HTTP work:
                // browsers drop SameSite=None cookies that are not Secure.
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

                // Plain authorization requests, no PAR. Keycloak 26.6 ignored prompt=create (the register link) when it
                // arrived inside a pushed request, and PAR adds nothing we need for local sign-in.
                options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
            });

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AdminPolicy, policy => policy.RequireRole("admin"));

        return builder;
    }

    /// <summary>Rejects unsafe requests without the <see cref="RequestHeader"/> header, unless the endpoint opts out.</summary>
    public static IApplicationBuilder UseRosterRequestHeader(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            bool unsafeMethod = !(HttpMethods.IsGet(context.Request.Method)
                || HttpMethods.IsHead(context.Request.Method)
                || HttpMethods.IsOptions(context.Request.Method));

            if (unsafeMethod
                && !context.Request.Headers.ContainsKey(RequestHeader)
                && context.GetEndpoint()?.Metadata.GetMetadata<WithoutRosterHeader>() is null)
            {
                await Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: $"Missing {RequestHeader} header",
                    detail: $"Requests that change state must send the {RequestHeader} header.").ExecuteAsync(context);
                return;
            }

            await next(context);
        });

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder auth = app.MapGroup(PathBase).WithTags("Auth");

        // Top-level navigations from the web app, so they are GETs and need no header.
        auth.MapGet("/login", (string? returnUrl) =>
                Results.Challenge(
                    new AuthenticationProperties { RedirectUri = LocalOrRoot(returnUrl) },
                    [OpenIdConnectDefaults.AuthenticationScheme]))
            .AllowAnonymous();

        // prompt=create sends the person straight to Keycloak's registration page.
        auth.MapGet("/register", (string? returnUrl) =>
                Results.Challenge(
                    new OpenIdConnectChallengeProperties { RedirectUri = LocalOrRoot(returnUrl), Prompt = "create" },
                    [OpenIdConnectDefaults.AuthenticationScheme]))
            .AllowAnonymous();

        // A plain form post, because signing out ends with a redirect to Keycloak. That is why it skips the header
        // check: the worst a forged sign-out does is sign someone out, and SameSite=Lax already blocks it cross-site.
        auth.MapPost("/logout", (ClaimsPrincipal user) =>
                user.Identity?.IsAuthenticated == true
                    ? Results.SignOut(
                        new AuthenticationProperties { RedirectUri = "/" },
                        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme])
                    : Results.LocalRedirect("/"))
            .AllowAnonymous()
            .WithMetadata(new WithoutRosterHeader());

        auth.MapGet("/me", (ClaimsPrincipal user) => new Me(
            Id: user.FindFirstValue("sub") ?? throw new InvalidOperationException("Signed-in principal has no sub claim."),
            Name: user.FindFirstValue("name") ?? user.FindFirstValue("preferred_username") ?? "",
            Email: user.FindFirstValue("email"),
            Roles: [.. user.FindAll(RoleClaim).Select(c => c.Value).Where(AppRoles.Contains)]));

        return app;
    }

    private static string LocalOrRoot(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\')) ? url : "/";

    /// <summary>The signed-in person as the web app sees them. The title joins this once the app profile exists.</summary>
    public sealed record Me(string Id, string Name, string? Email, string[] Roles);

    /// <summary>Endpoint metadata: this unsafe endpoint does not need the <see cref="RequestHeader"/> header.</summary>
    public sealed class WithoutRosterHeader;
}
