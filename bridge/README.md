# bridge/ — the in-vvvv runtime bridge

Code that runs **inside** vvvv gamma (as opposed to `tools/`, which runs on the dev
machine). This is the half of the vvvv-agent that can see and touch the live editor.

## `VL.Agent`

A C# node library (category **Agent**) that exports live editor state so an external
coding agent can read it. Built against the assemblies of the installed gamma 7.2
(via `HintPath`) so the build matches the exact runtime that loads it.

### Status

- ✅ **Compiles** against gamma 7.2 (`net8.0-windows`), 0 errors.
- ✅ **Runtime-verified in vvvv (2026-06-19)** — loaded into the editor, `WriteEditorSnapshot`
  produces JSON, and the `selection` array updates live as the patch selection changes.

### Key finding that shaped the design

`VL.HDE.API` is a **static** class — editor state (`LoadedDocuments`,
`CurrentSelection`, `LatestMessagesFromCompiler`, `HoveredElement`, …) is reachable
globally, so a node needs no wiring to obtain it. (The metadata dump in `vl-probe`
did not mark static members; this only surfaced when the compiler rejected
`API` as a parameter type — a good reminder to compile against the real assemblies.)

### Nodes

- **`WriteEditorSnapshot(path) → status`** — writes a JSON snapshot of loaded
  documents (path/name/changed/readonly), the current selection, and the latest
  compiler messages (severity/what/why) to `path`. One-shot; trigger from a patch.
- **`EditorWatcher`** (`[ProcessNode]`, pins: `path`, `enabled` → `resolvedPath`, `status`)
  — the same snapshot, rewritten automatically whenever editor state changes
  (selection / messages / open documents). **Leave `path` empty**: it auto-derives
  `<project>/.agent/editor-state.json` from the document this node lives in (via
  `NodeContext` → `AppHost.GetDocumentPath`), matching where the MCP server looks. Set
  `path` only to override.

Selection entries are resolved through `ILiveElement` into structured data:
`ElementId` (stable base62 id), `MergeId`, `DocumentId`, `UniqueId` (the parseable
token used for writes), `Name`, `Symbol`, `Kind`, `IsUnused`, and per-element `Messages`.

### Write path

- **`CommandProcessor`** (`[ProcessNode]`, pins: `path`, `enabled` → `status`, `lastResult`)
  — watches `<project>/.agent/requests/*.json`, applies each edit on the editor main loop,
  and writes a matching result to `<project>/.agent/results/`. Runs on the main thread
  (required for solution mutations). Leave `path` empty for the project default.
  - v1 op `setPinValue`: `{ "op":"setPinValue", "uniqueId":"…", "pin":"…", "value":42, "type":"Int32" }`
    → `SessionNodes.CurrentSolution.SetPinValue(uid, pin, value).Confirm(Default)` — **undo-integrated**.
  - The external agent never edits the patch directly; it drops a request (via the MCP
    `vvvv_set_pin_value` tool) and this node applies it. Use the `UniqueId` from a
    selected element in the snapshot to address the target.

### Live-state loop into Claude Code

`EditorWatcher` (push) → JSON file → `tools/vl-mcp` `vvvv_editor_state` (pull) → Claude
Code. See `tools/vl-mcp/README.md`.

### Build

```bash
cd VL.Agent
dotnet build -c Release        # -> bin/Release/net8.0-windows/VL.Agent.dll
```

If your vvvv install is elsewhere, override `VvvvInstall` in the `.csproj` (or pass
`-p:VvvvInstall=...`).

### Test recipe (in vvvv — to be run with a human)

1. In vvvv, add this project as a dependency: **Quad menu → Dependencies → Edit
   manually**, or add the package repository, then reference `VL.Agent.csproj`
   (source/editable package so vvvv compiles it live).
2. In a patch, create the **`WriteEditorSnapshot`** node (category `Agent`).
3. Feed an output path (e.g. `C:\temp\vvvv-editor.json`) and bang the operation.
4. Confirm the JSON appears and reflects the open documents / current selection.

### Next bridge increments (validated APIs, not yet built)

- Undo-safe writes: `IDevSession.Current.CurrentSolution.SetPinValue(...).Confirm(...)`.
- Push diagnostics onto elements: `SessionNodes.AddMessage(...)`.
- A continuous (push) snapshot via the `API.*` channels/observables instead of a bang.

See `research/windows-api-validation-findings.md` for the full confirmed surface.
