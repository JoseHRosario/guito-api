# Token source of truth: Figma MCP sync (REST variables API is Enterprise-gated)

Follows ADR 0006 (Figma-driven UI with design tokens). ADR 0006 fixed the *destination* of the tokens (Figma variables → `design/tokens.json` → style-dictionary → daisyUI theme) but predates the discovery that changed the *transport*:

**Decision**: the token sync from Figma to the repo goes through the **Figma remote MCP** (OAuth 2.1, `https://mcp.figma.com/mcp`). At implement time the agent reads the design file's variables via MCP and writes `design/tokens.json` in the style-dictionary shape; `npm run tokens` compiles it as before. The mapping from Figma variable names to token paths lives in `tools/tokens/figma.json`, whose `fileKey` points at the Guito design file (`UoIK5MnIqDrgfHqMmBZoYk`, José's duplicate of "Simple Design System (Community)").

**Why not the REST pull** (`tools/tokens/pull-figma.mjs`, verified live 2026-09-28): Figma's Variables REST API requires the `file_variables:read` scope, which is **Enterprise-plan-only**. On José's Professional plan the PAT gets a 403 naming that scope, and no scope selection on a non-Enterprise plan fixes it. The PAT pull leg is dead permanently on this plan; the script stays in the repo with a comment noting the gate.

**Considered options**:
- REST `variables/local` pull via PAT — rejected, Enterprise-gated scope (verified 403).
- Manual plugin export — rejected, adds a manual step and loses the agent-in-the-loop auditability of a committed diff.
- Seed values only (no Figma sync) — rejected, defeats ADR 0006's single-source-of-truth goal.

**Consequences**:
- Token sync requires the Figma MCP connection (OAuth; PATs are rejected on the MCP endpoint too — OAuth only).
- The design file's duplicate is the writable source; the Community original is never edited.
- `design/tokens.json` is regenerated on design approval, not continuously — code never drifts styling without a Figma-approved token change.
- If the plan ever moves to Enterprise, the REST pull could be revisited; until then MCP is the only sanctioned path.
