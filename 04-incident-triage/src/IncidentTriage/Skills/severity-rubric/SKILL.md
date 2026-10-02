---
name: severity-rubric
description: Company severity levels Sev1-Sev4 with concrete criteria. Use when assigning or checking an incident's severity.
---
# Severity rubric

Pick the HIGHEST level whose criteria match. When torn between two, pick the higher one; it can be downgraded later.

| Level | Criteria (any one is enough) | Response |
|---|---|---|
| **Sev1** | Full outage of a customer-facing service; data loss or corruption; security breach; payments failing for all customers | Page on-call immediately, incident commander, status page |
| **Sev2** | Partial outage or >5% error rate on a customer-facing flow (checkout, login, payments); SLO burn rate > 10x; a single large customer fully blocked | Page on-call, incident channel |
| **Sev3** | Degraded performance within SLO limits; elevated latency on non-critical paths; an internal tool down | Ticket, fix within the sprint |
| **Sev4** | Cosmetic issues, a single-user problem with a workaround, noisy alert with no user impact | Backlog |

## Hints
- Customer reports about money ("charged twice", "payment failed") are at least Sev2.
- HTTP 5xx on checkout or payment endpoints is at least Sev2.
- Latency alerts without errors are usually Sev3 unless an SLO is breached.
