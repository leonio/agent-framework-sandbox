---
name: git-workflow
description: How this team names branches, writes commits and structures merge request descriptions.
---
# Git workflow

- Branch: `feature/<ticket-key>-<kebab-summary>`, e.g. `feature/DEMO-123-todo-cli`.
- Commits: Conventional Commits - `feat:`, `fix:`, `test:`, `docs:`, `chore:`.
  One logical change per commit; the generated app is usually
  `feat: scaffold <app>` + `test: add <app> tests` + `docs: add README`.
- Merge request title: `<ticket-key>: <imperative summary>`.
- Merge request body sections, in this order:
  1. **Summary** - two or three sentences.
  2. **Acceptance criteria** - checklist copied from the spec, ticked if covered.
  3. **Review** - verdict and any open findings.
  4. **How to test** - exact commands.
