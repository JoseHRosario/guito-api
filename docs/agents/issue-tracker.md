# Issue tracker: GitHub

Issues and specs for this repo live as GitHub issues. Use the `gh` CLI for all operations.

## Conventions

- **Create an issue**: `gh issue create --title "..." --body "..."`. Use a heredoc for multi-line bodies.
- **Read an issue**: `gh issue view <number> --comments`, filtering comments by `jq` and also fetching labels.
- **List issues**: `gh issue list --state open --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'` with appropriate `--label` and `--state` filters.
- **Comment on an issue**: `gh issue comment <number> --body "..."`
- **Apply / remove labels**: `gh issue edit <number> --add-label "..."` / `--remove-label "..."`
- **Close**: `gh issue close <number> --comment "..."`

Infer the repo from `git remote -v`; `gh` does this automatically when run inside a clone.

## Repo-specific conventions

- **Branch naming**: `feature/<issue#>-<slug>` / `bug/<issue#>-<slug>` (issue number first). Never push to `master`.
- **PR body must end with `Closes #N`** so GitHub closes the issue on merge into `master` (staging merges don't close — that's deliberate).

## Issue templates

Both repos define GitHub issue forms under `.github/ISSUE_TEMPLATE/`. Create issues using them — `gh issue create --template` opens the form interactively; when scripting, construct the body from the template's fields so issues stay uniform. The form's `labels:` are applied automatically on issue creation.

### Feature (`feature.yml`, label `feature`)

Body fields:

- **What** (required): one paragraph — the user-visible outcome.
- **Acceptance criteria** (required): verifiable, one `- [ ]` checkbox per criterion.
- **Out of scope**: explicitly excluded, so scope creep has a name.
- **Open questions**: decisions still to settle before implementation.

### Bug (`bug.yml`, label `bug`)

Body fields:

- **What broke** (required): one paragraph — expected vs actual behavior.
- **Steps to reproduce** (required): minimal, verifiable steps from a clean state.
- **Acceptance criteria** (required): how we verify it's fixed — one `- [ ]` checkbox per criterion.
- **Suspected cause / affected area** (optional): area of code, config, or infra you suspect.

### guito-ui only

`feature.yml` adds a **Figma frame** field: link to the frame the work traces to (Guito App page); omit for non-UI work.

A scripted create looks like:

```bash
gh issue create --title "..." --label feature --body "$(cat <<'EOF'
## What

<one paragraph>

## Acceptance criteria

- [ ] ...

## Out of scope

- ...

## Open questions

- ...
EOF
)"
```

Blank issues are enabled (`config.yml`) — use them only for work that fits neither form (e.g. infra/docs chores).

## Pull requests as a triage surface

**PRs as a request surface: no.** _(Set to `yes` if this repo treats external PRs as feature requests; `/triage` reads this flag.)_

When set to `yes`, PRs run through the same labels and states as issues, using the `gh pr` equivalents:

- **Read a PR**: `gh pr view <number> --comments` and `gh pr diff <number>` for the diff.
- **List external PRs for triage**: `gh pr list --state open --json number,title,body,labels,author,authorAssociation,comments` then keep only `authorAssociation` of `CONTRIBUTOR`, `FIRST_TIME_CONTRIBUTOR`, or `NONE` (drop `OWNER`/`MEMBER`/`COLLABORATOR`).
- **Comment / label / close**: `gh pr comment`, `gh pr edit --add-label`/`--remove-label`, `gh pr close`.

GitHub shares one number space across issues and PRs, so a bare `#42` may be either: resolve with `gh pr view 42` and fall back to `gh issue view 42`.

## When a skill says "publish to the issue tracker"

Create a GitHub issue.

## When a skill says "fetch the relevant ticket"

Run `gh issue view <number> --comments`.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single issue with **child** issues as tickets.

- **Map**: a single issue labelled `wayfinder:map`, holding the Notes / Decisions-so-far / Fog body. `gh issue create --label wayfinder:map`.
- **Child ticket**: an issue linked to the map as a GitHub sub-issue (`gh api` on the sub-issues endpoint). Where sub-issues aren't enabled, add the child to a task list in the map body and put `Part of #<map>` at the top of the child body. Labels: `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`). Once claimed, the ticket is assigned to the driving dev.
- **Blocking**: GitHub's **native issue dependencies**, the canonical, UI-visible representation. Add an edge with `gh api --method POST repos/<owner>/<repo>/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-db-id>`, where `<blocker-db-id>` is the blocker's numeric **database id** (`gh api repos/<owner>/<repo>/issues/<n> --jq .id`, _not_ the `#number` or `node_id`). GitHub reports `issue_dependencies_summary.blocked_by` (open blockers only, the live gate). Where dependencies aren't available, fall back to a `Blocked by: #<n>, #<n>` line at the top of the child body. A ticket is unblocked when every blocker is closed.
- **Frontier query**: list the map's open children (`gh issue list --state open`, scoped to the map's sub-issues / task list), drop any with an open blocker (`issue_dependencies_summary.blocked_by > 0`, or an open issue in the `Blocked by` line) or an assignee; first in map order wins.
- **Claim**: `gh issue edit <n> --add-assignee @me`, the session's first write.
- **Resolve**: `gh issue comment <n> --body "<answer>"`, then `gh issue close <n>`, then append a context pointer (gist + link) to the map's Decisions-so-far.