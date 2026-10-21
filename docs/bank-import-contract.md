# Bank import contract

## Scope and status

Owner-approved design from #158. #159 implements canonical identity, current-record persistence and import immutability for matched records. #160 (windows/date output), #161 (interactive context), #162 (scheduler) and guito-ui#77 (presentation) remain distinct deliveries. This document describes source contracts, not proof of live migration/deployment or permission to mutate production.

## Domain and persistence invariants

- A Bank Transaction represents an actual bank movement, not one observation during a fetch. Stored movements are retained indefinitely; disappearance from a later response never authorizes deletion.
- Resolve canonical account identity from the primary provider `identification_hash`; session-specific account UID/session ID are current retrieval locators. Do not merge accounts by IBAN/name or arbitrary secondary hashes. Legacy account/movement identifiers and Expense links remain valid.
- Scope reliable `entry_reference` to canonical account identity. Do not include mutable dates, description or amount formatting in that identity; `transaction_id` has no uniqueness/stability guarantee.
- Preserve supplied identity and source-date fields separately on the current record. No per-import observation archive or receipt ledger.
- Missing/conflicting/reused references cannot prove movement identity. Preserve ambiguous genuine movements and explicit uncertainty; content likeness is not permission to merge or delete. See the implementation policy below for deterministic fallback behavior and its limitations.
- Safely identified unmatched records may refresh bank-supplied fields without resetting `ignored`/`needsReview`. A record with matched state or an Expense link is excluded from **every** import mutation, including category-suggestion writes. Enforce exclusions in final SQL predicates so concurrent Match is protected.
- Explicit user Unmatch is unchanged and can return a row to Needs review. Import never edits the linked Expense.
- No guessed legacy identity backfill: a missing original reference cannot be reconstructed from a stored digest or merchant/date resemblance. Existing matched dates are not silently backfilled.

## HTTP contract pinned before dependent consumers change

### #159 — unchanged wire

Existing authenticated routes and request/response shapes remain unchanged. In particular:

- `POST /BankTransaction/sync` returns `{ "fetched": <count>, "new": <count> }`. `fetched` counts eligible booked debits observed; `new` counts newly inserted movements, not updates to an existing canonical record.
- Existing `GET /BankTransaction` retains its current `bookingDate` meaning and ordering in #159. Bank IDs remain immutable Int64 values; no renumbering or change to link/review contracts.
- Bank/Expense matching and Create-and-match continue using existing contracts. No scheduler route, client-side distinct filter or user-presence-header change in this slice.

### #160 and guito-ui#77 — coordinated additive date contract

Pin these exact names before implementation:

| JSON property | Type | Meaning |
|---|---|---|
| `bookingDate` | `YYYY-MM-DD` string | Existing stored bank booking date; keep its meaning, do not repurpose the property. |
| `transactionDate` | nullable `YYYY-MM-DD` string | Provider transaction date; legacy values may be null. |
| `valueDate` | nullable `YYYY-MM-DD` string | Provider value date, independent of transaction/booking date. |
| `effectiveDate` | `YYYY-MM-DD` string | `transactionDate ?? bookingDate`, never an audit/import timestamp. Legacy matched records retain their previously stored date, without provider-driven backfill. |

Apply those fields consistently to Bank list/detail representations used by Bank and creation context. #160 orders Bank reads by effective date descending then immutable numeric Bank ID descending; UI #77 displays/groups/sorts by `effectiveDate` and uses it for Bank-derived create prefills. Expense-history dates remain Expense occurrence; never mix them with Bank dates. The UI must not infer transaction identity from any date or collapse equal-looking rows.

The provider fields are semantically distinct even when particular dates coincide. For the confirmed OpenAI case, homebanking movement date is 10 October 2026 and value date 12 October; the observed provider transaction date was 12 October and booking date 18 October. José explicitly chose provider transaction date for Bank presentation. This is not a universal equivalence of transaction and value dates.

## Fetch, trigger and failure policy — later issues

- Initial import requests the current month; subsequent import starts seven days before the last successful account import through today. A checkpoint advances only after a complete account fetch/persistence, including all continuation pages. Partial completion/retry must not create identity duplicates or skip unprocessed pages.
- Manual interactive Sync forwards only genuine user context under the provider's documented PSU contract. Scheduled/agent diagnostics do not fabricate presence. Keep human/agent authentication independent.
- Nightly import-only start is 23:00 Europe/Lisbon with daylight-saving handling. Up to three additional transient retries within the same Lisbon evening, stop before local midnight. Rate limits stop the run; reconnection errors require user action.
- The bank's quota reset scope/timezone is unverified; midnight is an operational cutoff, not a reset guarantee. One import may involve multiple accounts/pages/requests. No claim of four fresh requests next morning.
- #162/#77 own the exact minimal outcome-status contract before integration; no outcome DTO is silently invented by #159.

## Migration and acceptance gates

Forward-only migration preserves all existing financial rows and links. Never merge accounts/movements by guessed content during schema application. Existing source-date/identity values stay nullable where no evidence exists. Applied history is not rewritten.

Hermetic tests use the existing HTTP boundary, real service/repository composition and fake external transports. Assert date corrections do not create a new reliably identified movement, genuine identical purchases remain separate, renewed consent preserves identity/flags, ambiguous references are safe, and import writes cannot mutate matched records or reset Ignored. Fake SQL tests do not prove real PostgreSQL DDL, locks or MVCC; live evidence is reported separately.

The OpenAI repair (#163) has its own before/after preview, current-state guards, atomic execution and readback: retain Bank51, bind verified identity, effective date 12 October, remove confirmed Bank89/120 only; Expense97 and all other candidates unchanged. Owner design approval is **not** execution approval. No production schema/data execution, staging replacement, provider fetch or credential/IAM change is authorized by this document.

## Implementation policy and evidence

#159 uses `012_bank_identity.sql`: additive nullable account/movement identity fields, separate fetch UID and transaction/value dates, plus indexes. It does not rewrite legacy financial rows or infer reference mappings. A newly identified account gets an immutable `canonical:` key derived from its primary identification hash; later consent refreshes update the fetch UID/session while retaining that key. Existing unresolved accounts retain their UID and remain separate until an explicitly reviewed mapping is available; this slice does not automatically heal legacy duplicates.

For a primary-identified account, an entry reference occurring once across the complete account fetch is a candidate reliable identity. Existing evidence of an ambiguous reference or an amount/currency/direction conflict prevents that reference from automatically refreshing an old movement. Such conflicts are retained as ambiguity instead of treating a potentially separate purchase as a correction. Amount/currency/direction changes are therefore not silently applied to the old movement.

Missing account/reference identity and repeated references use deterministic **content-fingerprint occurrence slots**: equal observations across all pages are numbered separately, retaining their multiplicity. An unchanged complete response reuses those slots. This is not proof of movement identity: changed ambiguous dates/content can add unresolved rows; a shifted window or a reference changing between missing/unique/duplicated can alter the represented set. No algorithm can guarantee perfect deduplication without reliable source evidence. No archive of every fetch is stored, and historical ambiguous rows are never auto-deleted or guessed into a Match.

Final SQL refreshes update only current dates/remittance for reliable identities with compatible amount/currency/direction and no Match/link. Source transaction/value dates are stored but not exposed in #159. Category suggestions remain new-row-only and their final UPDATE also excludes linked/matched rows.

## Verification evidence

Implementation worktree: `feature/159-bank-identity`, original base `d7b4a17166dcf22265b1ec2b44c16a15ea259288`; declared version 0.28.0 follows #156's merged 0.27.0. Before publication, master `de98889701945afa5c84362ed7d177430fa4b299` was integrated, retaining the DynamoDB user-session implementation. The unpublished identity ADR was renumbered to ADR-0021 because merged ADR-0020 owns CloudFormation storage adoption. Migration 012 remains additive; #156 retired its unreleased Postgres session migration 011 when adopting DynamoDB. The initial corrected-booking-date HTTP regression failed before implementation and passed after it. Targeted regressions cover canonical account renewal, distinct same-content purchases, occurrence multiplicity across pages, missing/reused references, amount conflicts, Ignored preservation, legacy links, and Match-before-write/delayed-suggestion guards.

Hermetic fake-transport results and mutation checks are not live PostgreSQL DDL/MVCC evidence. No live bank-provider requests or financial cleanup were performed for #159. José subsequently requested PR publication after disclosure that a feature-branch push deploys shared staging and applies pending migrations. That authorizes publication, not production deployment or acceptance of the unresolved same-reference/same-amount purchase risk. The PR remains draft pending that identity-policy decision; deployment results are tracked separately in Actions.
