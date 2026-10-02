IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// Generated on first run and kept in this project's user secrets. They must not change between runs: Keycloak's data
// volume keeps the realm exactly as it was imported the first time, secret and seeded admin password included.
IResourceBuilder<ParameterResource> apiClientSecret = builder.AddParameter(
    "roster-api-client-secret", new GenerateParameterDefault { MinLength = 32, Special = false }, secret: true, persist: true);
IResourceBuilder<ParameterResource> rosterAdminPassword = builder.AddParameter(
    "roster-admin-password", new GenerateParameterDefault { MinLength = 16, Special = false }, secret: true, persist: true);

// Identity provider. The Aspire integration is preview (design doc D10). The port is pinned because the realm's redirect
// URIs and the browser's cookies depend on it. Realms/roster-realm.json reads the two values above from the environment.
IResourceBuilder<KeycloakResource> keycloak = builder.AddKeycloak("keycloak", port: 8080)
    .WithDataVolume()
    .WithRealmImport("./Realms")
    .WithEnvironment("ROSTER_API_CLIENT_SECRET", apiClientSecret)
    .WithEnvironment("ROSTER_ADMIN_PASSWORD", rosterAdminPassword);

builder.AddProject<Projects.Roster_Api>("api")
    .WithReference(keycloak)
    .WaitFor(keycloak)
    .WithEnvironment("Keycloak__ClientSecret", apiClientSecret);

builder.Build().Run();
