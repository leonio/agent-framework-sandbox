# Runbook: Database connection pool exhaustion

Owner: Platform / Data team. Applies to all services using SqlClient / ADO.NET connection pooling (checkout-api, orders-worker).

## Symptoms
- Errors: `System.InvalidOperationException: Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool.`
- HTTP 503 / 500 from API endpoints that hit the database; request latency climbs to the connection timeout (15-30 s).
- Pool metrics show active connections at Max Pool Size with waiters queued; database CPU is often *normal*.

## Likely causes
- Connection leak introduced by a recent deploy: a code path opens a SqlConnection / DbContext per item in a loop, or does not dispose it (`using` / `await using` missing), so connections stay checked out until the pool is exhausted.
- N+1 database calls: a loop over order line items issuing one query per line item, holding connections far longer under load.
- Max Pool Size lowered in a configuration change, or traffic spike above what the pool size supports.
- Slow queries or blocking locks holding connections open (check the database for long-running transactions).

## Diagnosis
1. Check recent deploys of the service (`recent_commits`) for changes to repositories / data-access code.
2. Search the code for `new SqlConnection` or `OpenAsync` inside loops and for missing `using`.
3. Compare pool size in configuration between the last good and current release.

## Mitigation
- Roll back the most recent deploy of the affected service if it touched data-access code.
- As a stop-gap, restart instances to release leaked connections (buys minutes, not a fix).
- Temporarily raise Max Pool Size only if the database has headroom; never as the permanent fix.
- Add a pool-exhaustion alert on `active connections / max pool size > 0.9 for 5 minutes`.
