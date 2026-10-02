---
name: postmortem-blameless
description: Template and language rules for blameless postmortems published to the Engineering Confluence space.
---
# Blameless postmortem template

Use exactly these sections, in this order:

```
# Postmortem: <incident title>
> Status: DRAFT ...
## Summary            (2-3 sentences: what happened, impact, root cause)
## Impact             (who/what was affected, for how long, severity)
## Timeline           (UTC, one line per event, "(inferred)" when not from a source)
## Root cause         (the accepted hypothesis; if unconfirmed, say so)
## Evidence           (bullets with sources: R1, runbook:..., code:..., commit:...)
## Mitigation         (what was / should be done to stop the bleeding)
## Review history     (each hypothesis round: accepted/rejected and the reviewer's reason)
## Action items       (table: Action | Type (Prevent/Detect/Mitigate) | Owner (TBD))
```

## Language rules
- Blameless: "the change introduced", "the alert did not fire", never "X forgot".
- Prefer specifics: numbers, timestamps, file names.
- Lessons are about systems and processes: missing tests, missing alerts, unclear runbooks.
- If a reviewer rejected an earlier hypothesis, keep it in Review history: the reasoning is useful to future readers.
