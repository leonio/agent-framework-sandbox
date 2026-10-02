# Knowledge (RAG corpus)

Markdown in this folder is chunked by `## ` heading, embedded and stored in the `knowledge` vector collection at start-up.
The root-cause agent gets the best matches injected automatically (Agent Framework `TextSearchProvider`).

- `runbooks/`: how to recognise and mitigate known failure modes. Keep a `## Likely causes` and a `## Mitigation` section in each; the agents cite them.
- `postmortems/`: past incidents. In production these would be synced from Confluence (see `Tools/ConfluenceClient.cs`), so every published postmortem makes the next triage smarter.

This README is not indexed (only the two sub-folders are).
