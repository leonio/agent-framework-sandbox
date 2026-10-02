---
name: log-forensics
description: How to read application logs and stack traces during an incident to find the first meaningful error rather than the loudest one.
---
# Log forensics

1. **Find the first error, not the most frequent.** Cascading failures produce thousands of downstream errors (timeouts, 503s) after one upstream cause. Sort by time and look at the first ERROR/WARN after the last known-good moment.
2. **Look just before the first error** for a deploy, config change, feature-flag flip, cron job or traffic spike. `deploy ... started` log lines and recent commits are prime suspects.
3. **Stack traces:** the root cause is usually the innermost exception (`---> ` / `Caused by:`), and the first frame in *our* code (not framework code) is where to look.
4. **Resource exhaustion patterns**
   - `Timeout expired ... obtaining a connection from the pool` (.NET SqlClient) / `HikariPool ... Connection is not available` (Java): connection pool exhaustion. Check for leaked connections (missing `using`/`await using`), N+1 query loops, or pool size changes.
   - `OutOfMemoryException`, GC pauses, container OOMKilled: memory leak or unbounded cache.
   - Thread-pool starvation (.NET): sync-over-async (`.Result`, `.Wait()`), rising latency with low CPU.
5. **Cache patterns:** a sudden drop in hit ratio followed by latency spikes on the backing store suggests a stampede (mass expiry, cache flush, reindex).
6. **Correlate with the citation format** `R<n>` for report lines so reviewers can check your reading.
