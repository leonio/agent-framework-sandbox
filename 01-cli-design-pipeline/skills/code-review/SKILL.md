---
name: code-review
description: Lightweight checklist for reviewing a small .NET codebase against its spec.
---
# Code review checklist

Rate each finding `High`, `Medium` or `Low`.

| Area | Ask | Typical severity |
| --- | --- | --- |
| Spec | Is every acceptance criterion implemented? | High |
| Correctness | Off-by-one, null handling, culture-sensitive parsing/formatting | High |
| Input | Is user input validated with a helpful message? | Medium |
| Errors | Are exceptions caught at the edge and reported, not swallowed? | Medium |
| Structure | Is logic separated from I/O so it can be tested? | Medium |
| Modern C# | records, pattern matching, collection expressions, no needless `var x = new List<T>(); x.Add(...)` | Low |
| Docs | Does the README say how to run it? | Low |

Keep findings actionable: *file, what is wrong, what to do instead*.
