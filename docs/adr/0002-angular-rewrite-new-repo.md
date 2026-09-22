# Rewrite the UI in Angular, new repo `guito-ui`

The existing `guito-web-app` is Next.js 14. Angular is the owner's primary frontend stack (also used in Minerva), so the UI is rewritten in Angular 22 (signals, standalone components) in a **new repository `guito-ui`**; `guito-web-app` is archived and kept as reference only.

**Considered Options**: keeping/upgrading Next.js (rejected — stack consolidation matters more than reuse); rewriting in place (rejected — Next.js server-side artifacts would linger, and auth is moving fully into the API anyway).

**Consequences**: the UI becomes a pure static SPA with no server-side runtime, which is what makes the S3+CloudFront deployment (ADR-0001) work.
