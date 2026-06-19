# Session context

Last updated: 2026-06-19 on Windows with vvvv gamma 7.2.

## Purpose

`agentic-vl` is a prototype/research repo for agentic development workflows in
vvvv gamma: static project understanding, live editor state inspection, and narrow
undo-integrated edits through public vvvv APIs.

## Completed Work

- `tools/vl-probe`: metadata-only API dumper for vvvv assemblies.
- `research/windows-api-validation-findings.md`: validated editor/session/model API
  surface against installed gamma 7.2.
- `tools/vl-map`: static project indexer for `.vl`, `.cs`, `.csproj`, and `.sdsl`.
- `bridge/VL.Agent`: in-vvvv node library.
  - `WriteEditorSnapshot`
  - `EditorWatcher`
  - `CommandProcessor`
  - `AgentHost`
- `bridge/VL.Agent.HDE.vl`: minimal HDE/editor-extension host for `AgentHost`.
- `tools/vl-mcp`: MCP stdio server exposing:
  - `vvvv_index_project`
  - `vvvv_editor_state`
  - `vvvv_set_pin_value`
- `research/vvvv-paste-snippet-format.md`: empirical clipboard XML capture for a
  copied Stride Box node.

## Verified Status

- `EditorWatcher` writes `.agent/editor-state.json` and reflects live selection and
  compiler messages.
- `vvvv_editor_state` reads that snapshot through MCP.
- `vvvv_index_project` indexes projects through MCP.
- `CommandProcessor` and `vvvv_set_pin_value` compile and use the intended
  request/result file loop.
- The bridge and MCP server build successfully.
- Patch-local hosting is architecturally fragile: if the user patch runtime pauses
  or throws during `Update`, `EditorWatcher` / `CommandProcessor` stop too. The
  preferred direction is `AgentHost` hosted from `bridge/VL.Agent.HDE.vl` so the
  control plane runs in the editor/HDE runtime.

## Important Safety Finding

The original direct paste path is unsafe; the current paste path is experimental.

Calling `SessionNodes.Paste(modelSnippet, PointF)` from `CommandProcessor.Update()`
was able to insert nodes, but it can also destabilize the graphical patch editor
with:

```text
InvalidOperationException: Collection was modified; enumeration operation may not execute.
```

The likely cause is editor graph mutation while the Skia graph editor is rendering,
plus user-runtime hotswap/instantiation of newly inserted nodes. `vvvv_paste`
requires `experimental=true` and supports `pauseRuntime` / `leaveRuntimePaused`.
Those runtime-pause options are useful only when hosted from the HDE/editor layer;
patch-local hosting can pause the command processor itself.

Future node insertion should use `AgentHost` in the HDE/editor layer, and still
needs a proper editor-command context or another safe editor-mediated boundary.

## Current Architecture Thesis

1. Static read-only intelligence first:
   - project index
   - dependency graph
   - package drift
   - file-format diagnostics
   - C# and shader review
2. Live editor state bridge:
   - loaded documents
   - selection
   - compiler/runtime messages
   - stable element IDs and `UniqueId`
   - hosted outside the user patch runtime via HDE where possible
3. Narrow undo-integrated edits:
   - pin value changes through `CurrentSolution.*.Confirm(...)`
4. More invasive patch edits only after a safe command execution context is proven.

## Next Work

- Live-test `vvvv_set_pin_value` more broadly across value types and IOBox/pad cases.
- Add a `vl-lint` pass for `.vl` invariants and C# node conventions.
- Validate `bridge/VL.Agent.HDE.vl` in vvvv as the default agent host.
- Build a safe editor-command based insertion path for nodes and links.
- Surface live values/telemetry through `ILiveDataHub` where practical.
- Add a reproducible bridge smoke-test workflow.

## Repo Notes

- `tools/` targets `net10.0` by default and can target `net8.0` with
  `-p:AgenticVlDev=true` for local dev.
- `bridge/VL.Agent` targets `net8.0-windows`.
- `testbed/`, `.agent/`, `bin/`, `obj/`, and local scratch files are ignored.
- `vvvv-sdk/` is a historical public SDK submodule for context, not current gamma
  internals.
