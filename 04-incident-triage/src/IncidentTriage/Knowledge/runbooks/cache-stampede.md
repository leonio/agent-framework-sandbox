# Runbook: Cache stampede / cold cache

Owner: Search team. Applies to search-api (Redis ProductCache) and any read-through cache.

## Symptoms
- Cache hit ratio drops sharply (e.g. from 0.9 to below 0.2).
- p99 latency spikes on read endpoints; the backing store (Elasticsearch / SQL) CPU climbs.
- Often starts right after a deploy, a cache flush, or a catalogue reindex job.

## Likely causes
- Catalogue reindex job flushed or invalidated the whole product cache at once, so every request misses and hits the backing store (cache stampede).
- Many keys created with the same TTL expire at the same moment (synchronised expiry) after a bulk load.
- Redis evicting keys under memory pressure (`evicted_keys` rising, `maxmemory` reached).

## Mitigation
- Pause or throttle the reindex job; re-enable after the cache warms.
- Add jitter to cache TTLs and use request coalescing (single-flight) for misses.
- Scale the backing store read replicas temporarily.
