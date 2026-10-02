IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// Generated on first run and kept in this project's user secrets. They must not change between runs: Keycloak's data
// volume keeps the realm exactly as it was imported the first time, secret and seeded admin password included.
IResourceBuilder<ParameterResource> apiClientSecret = builder.AddParameter(
    "roster-api-client-secret", new GenerateParameterDefault { MinLength = 32, Special = false }, secret: true, persist: true);
IResourceBuilder<ParameterResource> rosterAdminPassword = builder.AddParameter(
    "roster-admin-password", new GenerateParameterDefault { MinLength = 16, Special = false }, secret: true, persist: true);

// The credential vault's master key (design doc section 9). Losing it makes stored credentials unreadable, so it is
// persisted like the others; outside local runs it would come from a secret store.
IResourceBuilder<ParameterResource> vaultKey = builder.AddParameter(
    "vault-key", new GenerateParameterDefault { MinLength = 48, Special = false }, secret: true, persist: true);

// The database: Postgres with its data in a volume, and the roster database everything shares.
IResourceBuilder<PostgresDatabaseResource> rosterDb = builder.AddPostgres("postgres")
    .WithDataVolume()
    .AddDatabase("roster");

// Runs once per start: applies migrations and seeds the shared fake endpoint. Everything else waits for it to finish.
IResourceBuilder<ProjectResource> migrator = builder.AddProject<Projects.Roster_Migrator>("migrator")
    .WithReference(rosterDb)
    .WaitFor(rosterDb);

// Runners claim jobs from the database queue. Two replicas show the queue sharing work; they need no inbound
// endpoints and no Keycloak (they never sign anyone in), but they open credentials, so they get the vault key.
builder.AddProject<Projects.Roster_Runner>("runner")
    .WithReference(rosterDb)
    .WaitForCompletion(migrator)
    .WithEnvironment("Vault__Key", vaultKey)
    .WithReplicas(2);

// Identity provider. The Aspire integration is preview (design doc D10). The port is pinned because the realm's redirect
// URIs and the browser's cookies depend on it. Realms/roster-realm.json reads the client secret and the seeded admin
// password from the environment.
IResourceBuilder<KeycloakResource> keycloak = builder.AddKeycloak("keycloak", port: 8080)
    .WithDataVolume()
    .WithRealmImport("./Realms")
    .WithEnvironment("ROSTER_API_CLIENT_SECRET", apiClientSecret)
    .WithEnvironment("ROSTER_ADMIN_PASSWORD", rosterAdminPassword);

// The API: signs people in with Keycloak, serves the web app, enqueues work for the runners and streams events.
IResourceBuilder<ProjectResource> api = builder.AddProject<Projects.Roster_Api>("api")
    .WithReference(keycloak)
    .WaitFor(keycloak)
    .WithEnvironment("Keycloak__ClientSecret", apiClientSecret)
    .WithReference(rosterDb)
    .WaitForCompletion(migrator)
    .WithEnvironment("Vault__Key", vaultKey);

// The web app: Vite's dev server, which proxies /api (sign-in and the event stream included) to the API it references.
// AddViteApp runs `npm install` and `npm run dev` and registers the http endpoint; it is pinned to 5173 because the
// Keycloak realm's redirect URIs name that port.
builder.AddViteApp("web", "../Roster.Web")
    .WithReference(api)
    .WaitFor(api)
    .WithEndpoint("http", endpoint => endpoint.Port = 5173);

builder.Build().Run();
