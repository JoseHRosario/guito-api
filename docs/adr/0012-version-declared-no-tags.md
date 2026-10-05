# Version stamping: declared version, no release ceremony

Every deployed build reports its version — the API stamps every deployment with `<version>+<UTC timestamp>.<short SHA>` and serves it from the `X-Api-Version` response header on **all** responses (`/healthz` stays a plain probe; the header is the single delivery channel). The purpose (José's acceptance test): opening any deployed app — staging or prod — shows the exact release being tested or used, with **zero doubt and zero manual steps**.

**The version is declared in the repo, not derived from git tags.** The single source of truth is `<Version>` in `src/guito-api/guito-api.csproj` (Angular equivalent: `package.json` "version"). The deploy reads it, fails closed if it is missing, and appends build metadata itself (`deploy/deploy.sh`) — so same-day builds and repeated staging deploys of the same release are distinguishable by metadata alone. There are **no git tags, no pre-release suffixes, no build counters, and no manual release ceremony**: a merge to master *is* the release, and prod reports exactly the declared version of that commit.

An earlier iteration (issue #75, superseded by #77) derived the base semver from the last `v*` tag plus a `-beta.N` suffix. It failed the acceptance test: a feature under test in staging displayed the *previous* release's number with a `-beta` suffix — noise for the tester, and it made tagging a manual step José explicitly removed from the loop.

**Who bumps the version**: the agent, when implementing an issue — the issue's *Proposed version* (a mandatory template field) is committed onto the feature branch as a visible one-line diff, so the bump is reviewable in the PR and José keeps oversight without doing anything. Two features in flight with the same declared version is fine; metadata disambiguates their builds.

**No config-table storage** (considered and rejected when the scheme was first designed): the version is an immutable property of the build artifact; storing it in a database decouples the reportable value from the binary that reports it — the exact drift this feature exists to prevent.

**Consequences**: `deploy/version.sh`-style tag parsing is deleted; CI needs no tag/history checkout; the fail-closed rule survives (an undeclared version aborts the deploy). Future consumers (the UI's Settings page) read the header, never a version constant in the repo.
