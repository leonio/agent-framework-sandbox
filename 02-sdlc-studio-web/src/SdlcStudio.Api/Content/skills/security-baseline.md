---
title: Security baseline
description: A short OWASP-inspired checklist applied when designing and reviewing.
---
## Skill: security baseline
- Validate all input at the boundary; never trust client data.
- No secrets in code or config files; use a secret store.
- Parameterised data access only (no string-built SQL).
- Least privilege for every identity and token.
- Log security-relevant events without logging secrets or personal data.
