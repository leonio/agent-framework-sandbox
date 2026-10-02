# Branching and commits

## Branches
Short-lived feature branches off `main`. Never commit directly to `main`.
Delete the branch after merge.

## Merge requests
Open as **draft** first so CI runs before reviewers are pinged. Link the work
item in the title. Keep merge requests under ~400 changed lines; split larger
work into checkpoints.

## Tracker hygiene
When a merge request is opened, comment on the work item with a link and move
it to *In Review*. Update the team wiki page for the component with a short
"What changed" note.
