# Domain documentation

Read before domain modeling, contract changes or domain-oriented planning.
This is a single-context repository: [GLOSSARY.md](../../GLOSSARY.md) owns vocabulary;
[ADRs](../adr/) own decisions. A missing required glossary/link is a defect: report
and fix discovery, never silently proceed as though no contract exists.

Use glossary terms in issues, code, tests and proposals. Flag a real vocabulary gap
rather than inventing synonyms. Surface any ADR conflict explicitly; do not override
an accepted decision without an approved successor. Check the next unused ADR number;
never renumber merged historical ADRs. Read only decisions relevant to the task:

- Expense identity/date/amount/store: [ADR-0015](../adr/0015-postgres-expenses-and-lisbon-occurrence.md).
- Personal edit/delete: [contract](../expense-edit-delete-contract.md),
  [security](../expense-delete-security.md), ADR-0011's merged #146 aggregate extension.
- Bank/provider/storage: ADR-0004 and ADR-0013; Categories: ADR-0014.
- Auth: [authentication](../authentication.md), ADR-0003/0008; reporting: ADR-0017.
- Historical provenance: ADR-0016 and [import record](../historical-expense-import.md).

Bank review remains on open PR #147 at the #149 baseline. Consult its exact branch
and owner decisions when coordinating that work; do not treat proposed ADR-0018 or
its routes as merged. Cross-repo work reads the UI worktree's own root instructions
and glossary explicitly, not a fragile relative path to a neighboring checkout.
