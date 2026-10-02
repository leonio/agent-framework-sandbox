# Postmortem: search-api latency after catalogue reindex (OPS-1601)

## Summary
search-api p99 latency rose to 4 s for 35 minutes after the weekly catalogue reindex flushed the Redis ProductCache. Every request missed the cache and went to Elasticsearch.

## Root cause
The reindex job called FLUSHDB on the product cache instead of invalidating changed keys only. Cache stampede on a cold cache.

## Lessons
- Reindex should invalidate incrementally and pre-warm top products.
- TTL jitter was added to avoid synchronised expiry.
