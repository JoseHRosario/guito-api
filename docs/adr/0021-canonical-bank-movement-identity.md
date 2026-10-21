# Canonical bank identities, current movements and import-frozen Matches

Status: accepted design; implementation #159. Supersedes ADR-0013's date/content identity recipe, not its Aurora/Data API choice. Companion #160 owns date presentation and success windows; #161/#162 own manual context and scheduling.

A bank movement is not an import observation. Keep one current stored movement rather than archiving each response, resolving accounts across consent renewal through the provider's primary `identification_hash` and movements through account-scoped reliable `entry_reference`. Session account UIDs and `transaction_id` are fetch locators, while dates/descriptions/amount formatting can change; none establishes durable identity. Preserve original identity fields separately rather than discarding them into a content hash. The provider explicitly warns some banks omit or reuse references, so preserve ambiguity and never claim content similarity proves one movement.

Import updates may refresh safely identified unmatched movements while preserving Ignored decisions. A Match freezes its Bank record against every import write, including delayed category suggestions; the final database write must enforce this guard, not an earlier read. Explicit user Unmatch remains available. Forward migrations retain legacy IDs, links and original dates without guessed identity mappings or automatic duplicate removal. This trades perfect automatic cleanup for not destroying genuine purchases or corrupting financial links.

## Rejected alternatives

- Date/content composite as universal identity: changing booking dates generated distinct keys for the owner-confirmed OpenAI movement; identical genuine purchases can conversely share content. Merely substituting transaction date recreates the same defect.
- `transaction_id` or session-bound account UID as durable identity: provider contracts permit changed IDs across retrieval/renewal.
- Unconditional upsert by any supplied reference: missing or reused references make this unsafe. No fallback can guarantee perfect deduplication without reliable upstream identity.
- UI-only distinct filtering: conceals persisted competing IDs/links and risks hiding genuine purchases.
- Import-observation archive: owner chose one current movement, not historical snapshots.

## Dates and delivery boundaries

Keep source `booking_date`, `transaction_date` and `value_date` distinct. The approved future Bank date is transaction date with booking fallback, never import time; already-matched dates remain unchanged. #159 prepares nullable source fields but keeps the current HTTP date semantics unchanged. The exact future additive contract is in [the bank-import contract](../bank-import-contract.md). The OpenAI repair remains separately reviewed and production-write approval-gated under #163.

## Source guarantees and limits

[Enable Banking Transaction reference](https://enablebanking.com/docs/api/reference/#transaction) describes account-scoped `entry_reference`; [the FAQ](https://enablebanking.com/docs/faq/) qualifies missing/duplicate references and documents account UID renewal plus primary identification-hash matching. A reference occurring once in one response does not prove historical stability. Neither official source endorses the old date/content composite as a universal identity guarantee.
