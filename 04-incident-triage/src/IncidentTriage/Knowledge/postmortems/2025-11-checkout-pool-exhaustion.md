# Postmortem: checkout-api connection pool exhaustion during flash sale (OPS-1412)

## Summary
During the November flash sale checkout-api returned HTTP 503 for 22 minutes. SqlClient connection pool exhaustion: a new "save cart" path opened a connection per cart item and did not dispose it on the error path.

## Root cause
A code change in CartRepository opened a SqlConnection inside a per-item loop without `using`. Under flash-sale traffic connections leaked faster than the pool recycled them.

## Lessons
- Data-access changes need a load test before peak events.
- We had no alert on pool saturation; added one afterwards.
- Raising Max Pool Size (tried first) only delayed the outage by a few minutes.
