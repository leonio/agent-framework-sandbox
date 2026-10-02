# Keycloak realm for local runs

`roster-realm.json` is imported by Keycloak on start (`WithRealmImport("./Realms")` in `AppHost.cs`). JSON has no
comments, so this file explains it.

## What it sets up

| Part | Value | Why |
|---|---|---|
| Realm | `roster` | One realm for the app. The `master` realm stays Keycloak's own admin realm. |
| Registration | on, email as username, no email verification | Anyone running locally can make an account from the sign-in page. Turn `registrationAllowed` off to close it. |
| Password reset | off | There is no mail server locally. |
| Realm roles | `admin`, `member` | The two permission roles. Keycloak's built-ins (`offline_access`, `uma_authorization`) are listed only so the default-role composite can name them. |
| Default role | `default-roles-roster` = `member` + built-ins + account console | Every new account is a member and can manage its own profile and password in the account console. |
| Client | `roster-api`, confidential, code flow, PKCE S256 required | The API signs people in on behalf of the web app (backend-for-frontend). No implicit flow, no password grant, no service account. |
| Redirect URIs | `http://localhost:5280/api/auth/signin-oidc`, `http://localhost:5173/api/auth/signin-oidc` | The API's own port and the web app's dev server port (step 7), whose proxy forwards `/api`. Post-logout URIs match. |
| Protocol mapper | realm roles into a flat `roles` claim, in the id token too | Keycloak's default puts roles under `realm_access.roles` in the access token only; the API reads `roles` from the id token. |
| Seeded user | `admin@roster.local`, roles `admin` + default | So there is an admin from the first run. Its id is fixed, so its `sub` claim survives a volume reset. |

## Placeholders

Two values are `${...}` placeholders that Keycloak fills from its environment at import time:

- `${ROSTER_API_CLIENT_SECRET}`: the `roster-api` client secret.
- `${ROSTER_ADMIN_PASSWORD}`: the seeded admin's password.

The AppHost generates both on the first run, saves them in its user secrets (`dotnet user-secrets list` in
`src/Roster.AppHost`, or the Parameters section of the Aspire dashboard), and passes them to Keycloak and the API. Nothing
secret is committed.

## Changing the realm

Keycloak imports a realm only if it does not exist yet, and the AppHost keeps Keycloak's data in a volume. So after
editing this file, remove the volume (the Aspire dashboard names it; `docker volume ls | grep keycloak`) and start again.
Users created through registration go with it; the seeded admin comes back with the same id.

`WithRealmImport` is for local development only. A deployed Keycloak needs the realm baked into an image or applied by a
seeding job (see the Aspire Keycloak docs).
