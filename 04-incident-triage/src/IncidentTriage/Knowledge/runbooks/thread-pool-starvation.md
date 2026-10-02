# Runbook: .NET thread pool starvation

Owner: Platform team. Applies to all ASP.NET Core services.

## Symptoms
- Latency rises steadily for all endpoints while CPU stays low.
- `ThreadPool.ThreadCount` keeps climbing; queue length grows; health checks time out.

## Likely causes
- Sync-over-async: `.Result`, `.Wait()` or `GetAwaiter().GetResult()` on a hot path, often added in a recent change.
- Blocking I/O (file or HTTP) on request threads.

## Mitigation
- Roll back the change that introduced blocking calls.
- Temporarily raise `ThreadPool.SetMinThreads` to ride it out (not a fix).
