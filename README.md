# agentic-vl

Exploration repo for agentic development workflows in the vvvv gamma ecosystem.

This project investigates what a `vvvv-agent` could look like: a text-first, expert-facing assistant for understanding, debugging, testing, and eventually safely modifying complex vvvv projects.

## Current artifacts

- `research/vvvv-ecosystem-deep-dive.html` — detailed HTML analysis of the vvvv ecosystem and its components.
- `research/vvvv-agent-architecture-notes.md` — working notes on feasible vvvv-agent architecture.
- `deck/vvvv-agent-pitchdeck.html` — earlier pitchdeck-style architecture proposal.
- `SESSION_CONTEXT.md` — concise continuation context for picking this up on another machine.
- `vvvv-sdk/` — Git submodule pointing to the public vvvv SDK clone used for historical/source-code context.

## Important caveat

The cloned `vvvv-sdk` repository appears primarily to contain historical vvvv45/public SDK sources, not the current vvvv gamma internals. It is still useful as architectural context, especially around host/runtime/factory/command patterns, but current gamma details should be validated on Windows in vvvv itself.

## Next recommended work on Windows

1. Open `research/vvvv-ecosystem-deep-dive.html` and verify assumptions against a running vvvv gamma install.
2. Investigate the exact `VL.Lang` Session API surface from inside vvvv.
3. Prototype a minimal `.HDE.vl` bridge that can expose selected/hovered node context.
4. Prototype `vl-map`: static indexing for `.vl`, `.cs`, `.sdsl`, `.csproj`, help/test/package files.
5. Prototype `vl-lint`: file-format checks and C# node convention checks.
6. Add a VL.TestFramework smoke-test harness once running on Windows.
