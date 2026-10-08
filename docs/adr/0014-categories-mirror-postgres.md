# ADR-0014: Categories mirror in Postgres (seeded from Sheets, declared future source of truth)

Date: 2026-10-08
Status: Accepted
Extends: ADR-0013 (Postgres/Aurora/Data API — Bank Transactions)

## Context

Issue #112 adds a Jev-suggested category to every newly synced bank transaction.
The suggestion is stored as a foreign key (`bank_transactions.suggested_category_id`),
which requires the `categories` entity to exist in Postgres. Today categories live
only in Google Sheets (Config tab, names only — ADR-0005), written by expense creation.

ADR-0013 scoped Postgres to Bank Transactions only and explicitly declared "no data
migration" for Expenses/Category. The suggestion FK forces a narrow exception: a
**mirror** of the category list, not a migration of the source of truth.

## Decision

1. **A `categories` mirror table lives in Postgres** (`id` identity PK, `name` UNIQUE,
   `description` nullable, `create_date`/`update_date` audit columns), seeded from the
   Sheets category list.
2. **Sheets remains the source of truth for category writes.** Expense creation keeps
   writing categories to Sheets. The Postgres mirror is *seeded*, not authoritative:
   the seed snapshot lives in the numbered migration script
   (`db/migrations/004_seed_categories.sql`, applied per environment by
   `deploy/db-apply.sh`) — a category added to Sheets after the seed needs a follow-up
   seed script (or the future Postgres-as-truth migration).
3. **Seeding never runs in the app.** An earlier draft seeded lazily at sync time;
   José rejected it (a per-sync read would pay Sheets cost on every sync). The sync
   service only READS the seeded table; an empty table simply means no suggestions
   (fail-open) until the script runs.
4. **Postgres-as-truth for categories is declared but NOT implemented.** When that
   migration happens (separate ADR), the FK `suggested_category_id` already points at
   the table that will become the source of truth, and Sheets degrades to a
   presentation layer (per José's stated direction).
5. The suggestion itself: during sync, for each NEW bank transaction, the Jev choice
   path (existing `ResolveCategoryAsync` plumbing) picks one category **name** from
   the mirror list; the sync service maps the name to the mirror id and stores it.
   Fail-open: Jev failure, an empty category list, or an unresolvable choice stores
   never a silent default. Transactions synced before this feature
   keep NULL (no backfill). A row with no remittance information has nothing to
   decide on: it is stored without a suggestion, no Jev call is made.

### Jev batching (investigated)

The Decisions API takes **one `state` per request**; multiple questions share a single
state, so per-transaction suggestions are separate requests. OpenRouter's own cookbook
batches by running requests concurrently, not by packing items into one call. The sync
service therefore resolves suggestions with bounded concurrency (4 in flight). Cost is
trivial: Jev output tokens are free and input is ~$0.042/M tokens (a suggestion state is
a few hundred tokens).

## Consequences

- `GET /BankTransaction` returns the suggested category (id + name) per pending row.
- ADR-0013's "no data migration" claim is amended: a categories *mirror* exists in
  Postgres, still not the source of truth (see the superseded-pointer note there).
- A new Postgres repository joins the Bank Transactions aggregate for the mirror
  (`ICategoriesRepository`); category writes stay in Sheets.
- Jev category choices are keyed by name (the Sheets convention); the mirror's UNIQUE
  name constraint is what makes name→id resolution safe.
