## HTTP clients
Keywords: HttpClient, http, client, api, outbound
Never construct `HttpClient` directly in application code. Register typed clients with
`IHttpClientFactory` (`services.AddHttpClient<T>()`) so handlers are pooled and DNS changes are honoured.

## Controllers stay thin
Keywords: controller, ControllerBase, endpoint, MapPost, MapGet, route
Controllers and minimal-API handlers translate HTTP to application calls and back. Business rules
and data access live in services under `src/Application` and repositories under `src/Infrastructure`.

## Payment providers
Keywords: payment, provider, stripe, paypal, adyen, gateway
Each payment provider implements `IPaymentGateway` and is registered as a keyed service
(`AddKeyedScoped<IPaymentGateway, StripeGateway>("stripe")`). Adding a provider must not require
editing existing provider code.

## Data access
Keywords: sql, SqlCommand, query, Dapper, DbContext, database
Use EF Core or Dapper with parameters. Raw SQL built by string concatenation or interpolation is
not permitted outside `/migrations`, where scripts are static and reviewed by the DBA group.
