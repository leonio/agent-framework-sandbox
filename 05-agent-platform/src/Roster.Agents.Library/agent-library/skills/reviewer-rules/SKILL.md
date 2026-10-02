---
name: reviewer-rules
description: "Rules every code reviewer follows: surface level, few and well-evidenced findings, honest confidence."
---
# Rules for every reviewer

- This is a **surface-level architectural review**, not a line-by-line code review. Ignore formatting, naming nits
  and anything a linter would catch.
- Report at most five findings. Fewer, well-evidenced findings beat many speculative ones. If nothing material stands
  out, return an empty list. That is a valid and useful answer.
- Every finding must point at concrete code in the diff (the file, and what it does). No generic advice.
- Set `confidence` honestly. Below 0.5 means "worth a human glance", not "definitely wrong".
- Stay in your lane: another reviewer covers the other lenses.
