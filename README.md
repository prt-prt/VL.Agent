# agentic-vl

Exploration repo for agentic development workflows in the vvvv gamma ecosystem.

This project investigates what a `vvvv-agent` could look like: a text-first, expert-facing assistant for understanding, debugging, testing, and eventually safely modifying complex vvvv projects.

## Current artifacts

- `research/windows-api-validation-findings.md` — **API surface validated against installed vvvv gamma 7.2 (2026-06-19)**; confirms the editor-bridge, telemetry, and patch-mutation tiers against real types.
- `research/vvvv-ecosystem-deep-dive.html` — detailed HTML analysis of the vvvv ecosystem and its components.
- `research/vvvv-agent-architecture-notes.md` — working notes on feasible vvvv-agent architecture.
- `deck/vvvv-agent-pitchdeck.html` — earlier pitchdeck-style architecture proposal.
- `tools/vl-probe/` — metadata-only API dumper (.NET 10, `MetadataLoadContext`) that produced the findings above. Reproducible, runs no vvvv code.
- `testbed/dodecahedron-vl/` — a real vvvv project used as a validation corpus (cloned, origin detached, gitignored — not tracked by this repo).
- `SESSION_CONTEXT.md` — concise continuation context for picking this up on another machine.
- `vvvv-sdk/` — Git submodule pointing to the public vvvv SDK clone used for historical/source-code context (not initialized locally; historical vvvv45 only).

## Important caveat

The cloned `vvvv-sdk` repository appears primarily to contain historical vvvv45/public SDK sources, not the current vvvv gamma internals. It is still useful as architectural context, especially around host/runtime/factory/command patterns, but current gamma details should be validated on Windows in vvvv itself.

## Next recommended work on Windows

Done (2026-06-19): items (2) Session API surface and the static half of (3) are answered — see `research/windows-api-validation-findings.md`. Remaining:

1. **In-vvvv reachability spike**: minimal `.HDE.vl`/C# node that obtains `VL.HDE.API` + `IDevSession.Current` at runtime, logs hovered/selection/messages, and writes a message onto the hovered element. (Proves the bridge actually wires up.)
2. Capture the `IDevSession.Paste` `modelSnippet` format empirically (copy a node, inspect clipboard) for safe agent-driven insertion.
3. `vl-map`: static indexer for `.vl`/`.cs`/`.sdsl`/`.csproj`, validated against `testbed/dodecahedron-vl`.
4. `vl-lint`: file-format + C# node-convention checks.
5. VL.TestFramework smoke-test harness (locate its pack on this install first).
