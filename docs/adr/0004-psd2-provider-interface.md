# PSD2 bank sync behind a swappable provider interface

The old Nordigen integration (used until 2024) broke because the API changed; Nordigen is now GoCardless Bank Account Data, and its free tier is closed to new signups and being wound down. Bank sync is implemented behind a provider interface (`IListTransactionsService`) with the provider chosen at configuration time: first attempt is a grandfathered GoCardless account, fallback is Enable Banking's free "Restricted Production" tier (real data, self-linked accounts, EU banks). Manual consent re-auth every ~90 days is accepted for a personal tool.

**Consequences**: pricing must be re-checked before committing to GoCardless paid tiers; consents expire and re-linking is manual; swapping providers must not require touching expense logic.
