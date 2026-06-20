# Session Context

Last updated: 2026-06-20 on macOS after Windows validation against vvvv gamma 7.2.

## Purpose

`agentic-vl` explores agentic development workflows for vvvv gamma: static project
understanding, live editor state inspection, and narrow undo-integrated edits
through public vvvv APIs.

## Current Architecture

1. Static read-only intelligence:
   - `vl-map` project index
   - dependency graph
   - package/version drift
   - file-format diagnostics
2. HDE-hosted live bridge:
   - `VL.Agent.HDE.vl`
   - `AgentHost`
   - `EditorWatcher`
   - `CommandProcessor`
3. Narrow writes:
   - `vvvv_set_pin_value`
   - experimental `vvvv_apply_graph_transaction`
4. Future work:
   - safe structural graph transactions
   - live probes via `ILiveElement` / `ILiveDataHub`
   - HDE agent cockpit UI
   - replaceable backend adapter instead of a broad MCP-only surface

## MCP Strategy

MCP is an adapter, not the internal architecture. Keep the tool surface coarse and
small. Use MCP resources for large read-only context such as schemas, handoff
docs, test checklists, and live editor snapshots. Use `vvvv_context_query` for
compact editor-state reads before falling back to raw snapshots.

## Verified

- Windows, 2026-06-19, vvvv gamma 7.2:
  - `vl-probe` reflected public APIs.
  - `vl-map` indexed real projects.
  - `EditorWatcher` wrote `.agent/editor-state.json`.
  - `vvvv_editor_state` read snapshots through MCP.
  - `vvvv_set_pin_value` compiled and used the request/result loop.
  - `AgentHost` ran from an `.HDE.vl` editor extension.
- macOS, 2026-06-20:
  - .NET SDK 10.0.301 installed at `/Users/philipp/.dotnet`.
  - `vl-mcp` builds.
  - `tools/smoke-test.sh` passes, including MCP resources and
    `vvvv_context_query` missing-snapshot behavior.

## Open Windows Checks

Use `docs/WINDOWS_TESTING.md` as the running checklist.

Highest priority:

- Build `VL.Agent/VL.Agent.csproj` against the installed vvvv gamma assemblies.
- Validate `VL.Agent.HDE.vl` as the default host.
- Validate `vvvv_context_query` against a live editor snapshot.
- Test `vvvv_apply_graph_transaction` dry-run and batched `setPin`.
- Confirm batched `setPin` becomes one undo step.
- Inventory safe APIs for structural ops: `addNode`, `addPad`, `connect`,
  `setBounds`, and `select`.

## Safety Rules

- Prefer observe-before-mutate.
- Prefer HDE-hosted `AgentHost` over patch-local bridge nodes.
- Keep structural graph mutation experimental until safe editor-command semantics
  are proven.
- Do not hand-edit `.vl` XML unless the change is small, conservative, and backed
  by file-format knowledge.
