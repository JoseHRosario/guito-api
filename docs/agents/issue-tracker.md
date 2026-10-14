# Issue tracker: GitHub

Read before issue/PR creation, edits, triage, ticket publication or wayfinding.
Repository: `JoseHRosario/guito-api`. Use `gh` with explicit repository/endpoints;
verify `git remote -v` and bot account `xungameireles` before writes. Root
[AGENTS](../../AGENTS.md) owns worktree, branch, attribution and merge rules.

## Bodies, forms and labels

Read `.github/ISSUE_TEMPLATE/feature.yml` or `bug.yml` before creating the issue.
Scripted creation does **not** inherit web-form fields or labels: construct the
complete body and pass existing labels explicitly. Features need What, Acceptance
criteria checkboxes, Out of scope, Open questions and Proposed version; bugs need
What broke, Steps to reproduce, Acceptance criteria and Suspected cause/affected
area. The bug form currently omits Proposed version, so add that policy-required
section explicitly. Blank issues are allowed for work fitting neither form.
#149 expressly preserves runtime versions for documentation-only restructuring.

Write multiline bodies/comments to UTF-8 files; do not loop over shell heredocs or
inline-escape Markdown. Check labels with `gh label list --repo JoseHRosario/guito-api`;
create missing labels only within authorized triage scope, then read back the result.

```bash
gh issue create --repo JoseHRosario/guito-api --title "..." --label feature --body-file issue-body.md
gh api repos/JoseHRosario/guito-api/issues/149
gh api --paginate repos/JoseHRosario/guito-api/issues/149/comments
gh issue list --repo JoseHRosario/guito-api --state open --json number,title,body,labels
gh issue comment 149 --repo JoseHRosario/guito-api --body-file comment.md
```

Use explicit `--json` selections for `gh issue/pr view`; implicit views/edits may
hit deprecated projectCards queries. REST alternatives: PATCH `/issues/N` or
`/pulls/N` with `-F body=@complete-body.md`. Body PATCH replaces the entire body;
read it first, preserve other owners' changes and verify exact stored text after
writing. It is not a closure command. Verify labels, assignees and comments by
read-back too. Never rewrite an issue body incidentally while closing it.

## PRs and closure

Ordinary single-repo implementation PRs end with `Closes #N` for the intended local
issue, closing on merge to default `master`, not a staging branch. Cross-repo
references use `JoseHRosario/guito-api#N` (or the full URL), never a bare number in
a UI PR targeting an unrelated UI issue. Component PRs for cross-repo #149 use
`Refs JoseHRosario/guito-api#149`, **no closing keyword**; José closes the tracking
issue after repository and local-skill deliverables plus verification are complete.
Agents report completion/evidence and await owner review/merge; do not close a
product issue to substitute for unmerged/unverified work. Manual closure of a
research/triage ticket needs the owner's agreed disposition.

PRs as a request/triage surface: **no**. Read PRs for implementation/review, not as
new feature requests. GitHub shares issue/PR numbering; resolve resource type via
REST (`pull_request` on `/issues/N`) or explicit JSON views, not guessed numbers.

## Wayfinding

A map issue holds Notes / Decisions-so-far / Fog; children are tickets. Validate
existing `wayfinder:map` and `wayfinder:<type>` labels (research/prototype/grilling/task).
Link native sub-issues through GitHub's supported endpoint; if unavailable, keep
an explicit child task list in the map and `Part of #<map>` on each child. Do not
put the epic's child inventory on unrelated prerequisites.

Native blocking edges use numeric database IDs, not issue numbers or node IDs:
`POST repos/<owner>/<repo>/issues/<child>/dependencies/blocked_by -F issue_id=<id>`.
Read `.id` from the blocker and reverify open dependencies; where unavailable use
`Blocked by: #N` with live blocker states. Frontier: open unassigned children in map
order without open blockers. Claim via explicit assignee only within authorized
scope. Resolve by posting evidence/context pointers, then owner-approved closure;
implementation tickets still require the PR/verification lifecycle above.
