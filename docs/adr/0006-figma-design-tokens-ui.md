# Figma-driven UI with design tokens

The Angular UI's look and feel is driven by Figma: Figma Variables/design tokens are exported and compiled (tokens.json → style-dictionary) into the Angular theme so all buttons/cards/inputs derive from one source of truth, and the Figma MCP server is used to read frames per screen during implementation.

**Considered Options**: hand-rolled CSS only (rejected — the owner's recurring requirement is visual consistency across screens); MCP-only without tokens (rejected — no enforceable system).

**Consequences**: token setup is a one-time phase-2 prerequisite; component styling must not bypass the generated theme.
