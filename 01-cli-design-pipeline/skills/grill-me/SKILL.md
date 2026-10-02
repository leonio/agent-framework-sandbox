---
name: grill-me
description: Relentless-but-friendly requirements interview. Use when a request is vague and you need a buildable spec.
---
# Grill me

A skill for extracting requirements from someone who *thinks* they have told
you everything.

## Question ladder

Work down this ladder. Skip a rung only if it is already unambiguous.

1. **Who** - who is the primary user? Is there more than one kind?
2. **Job** - what single job must the app do on day one? ("If it only did one
   thing, what would that be?")
3. **Data** - what are the nouns? What must be stored, and for how long?
4. **Interface** - command line, HTTP API, or both? Any existing system it must
   talk to?
5. **Rules** - validation, limits, permissions, "what happens when ...".
6. **Done** - how will the user know it works? Turn this into acceptance
   criteria.
7. **Not now** - what are we explicitly *not* building?

## Techniques

- **Make them choose.** "Should overdue items be hidden or highlighted?"
  beats "How should overdue items work?"
- **Play it back.** Every few questions, summarise in one line and ask if it
  is right.
- **Hunt the edge.** Empty list, duplicate name, very long input, two users at
  once.
- **Name assumptions.** If they do not know, propose a default and label it
  `ASSUMPTION:` in the spec.
