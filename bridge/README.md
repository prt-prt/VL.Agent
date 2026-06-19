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
  compiler messages (severity/what/why) to `path`. Trigger it from a patch (e.g. on
  a bang) and an external agent can read live editor state from the file.

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
