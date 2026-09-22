# Dual authentication: Google PKCE for humans, API key for agents

The UI authenticates with Google (OAuth 2.0 with PKCE); the API validates the Google ID token via a Lambda authorizer. AI agents and CLI tooling also need to invoke the API without a browser, so the API additionally accepts a long-lived personal API key sent as an `X-Api-Key` header, validated by a second API Gateway authorizer.

**Considered Options**: AWS Cognito for both humans and machines (rejected — extra moving parts and cost for a single-user tool; Google sign-in stays provider-neutral); API key for everything (rejected — the UI keeps Google sign-in).

**Consequences**: two authorizer paths must be maintained; the API key is a powerful personal credential and must live in AWS Secrets Manager / the CLI's local secret store, never in the repo.
