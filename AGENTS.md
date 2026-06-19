# agentic-vl - agent guide

This repo explores a text-first assistant for vvvv gamma projects. Treat the repo
as a prototype with a stable read-only analysis layer and a narrow experimental
live-edit layer.

## Ground Truth

- Use the local `vvvv-*` skills for vvvv concepts and file-format details.
- Use `research/windows-api-validation-findings.md` for API facts validated against
  vvvv gamma 7.2.
- vvvv-loaded code targets `net8.0-windows`; standalone tools target `net10.0`.

## Tools

- `tools/vl-probe` - metadata-only public API dumper for a vvvv install.
- `tools/vl-map` - static project indexer for `.vl`, `.cs`, `.csproj`, and `.sdsl`.
- `tools/vl-mcp` - MCP stdio server exposing:
  - `vvvv_index_project`
  - `vvvv_editor_state`
  - `vvvv_set_pin_value`

Point MCP clients at the built `vl-mcp.exe`, not `dotnet run`.

## Bridge

`bridge/VL.Agent` runs inside vvvv and provides:

- `WriteEditorSnapshot`
- `EditorWatcher`
- `CommandProcessor`

Default convention:

```text
<project>/.agent/editor-state.json
<project>/.agent/requests/
<project>/.agent/results/
```

`EditorWatcher` writes live editor state. `CommandProcessor` currently supports
`setPinValue` requests and rejects `paste` requests.

## Important Safety Finding

Do not re-enable MCP paste casually.

`SessionNodes.Paste(modelSnippet, location)` exists and the clipboard XML shape is
documented in `research/vvvv-paste-snippet-format.md`, but calling it from
`CommandProcessor.Update()` can mutate the editor graph while the Skia patch editor
is rendering. This caused:

```text
InvalidOperationException: Collection was modified; enumeration operation may not execute.
```

Future insertion work should run in a proper editor-command context or another
API boundary that is safe relative to graph rendering.

## Working Principles

1. Observe before mutating.
2. Prefer static tools and editor snapshots for analysis.
3. Use `CurrentSolution.*.Confirm(...)` for narrow undo-integrated edits.
4. Do not hand-edit `.vl` XML unless the change is small, conservative, and backed
   by file-format knowledge.
5. Keep `README.md`, `SESSION_CONTEXT.md`, and tool-specific READMEs aligned with
   the actual verified state.
