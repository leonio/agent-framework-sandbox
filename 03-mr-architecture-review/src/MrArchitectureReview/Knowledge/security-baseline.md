## Secrets
Keywords: secret, password, apikey, api key, connection string, token
No secrets in source or appsettings.json. Use user-secrets locally and Key Vault references in
deployed environments.

## Endpoint authorisation
Keywords: Authorize, AllowAnonymous, endpoint, controller, RequireAuthorization
All endpoints require authorisation by default (fallback policy). `[AllowAnonymous]` needs a
comment explaining why, and a security review.

## Logging
Keywords: log, logger, LogInformation, PII, card, email
Never log full request bodies, card numbers, tokens or e-mail addresses. Use the redaction
helpers in `Shared/Logging`.
