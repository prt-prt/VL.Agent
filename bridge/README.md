# bridge

Code that runs inside vvvv gamma. This is the live-editor side of `agentic-vl`.

## `VL.Agent`

C# node library in the `Agent` category, built for `net8.0-windows` against the
installed vvvv gamma 7.2 assemblies.

Verified on Windows with vvvv gamma 7.2 on 2026-06-19.

## Nodes

### `WriteEditorSnapshot`

One-shot operation that writes a JSON snapshot of the current editor state to a
path.

Snapshot contents:

- loaded documents
- current selection
- compiler messages
- element IDs and `UniqueId` values for selected elements
- per-element messages where available

### `EditorWatcher`

Process node that rewrites the same snapshot whenever editor state changes.

Pins:

- `path`
- `enabled`
- `resolvedPath`
- `status`

Leave `path` empty to use:

```text
<project>/.agent/editor-state.json
```

The project directory is derived from the document containing the node.

### `CommandProcessor`

Process node that watches:

```text
<project>/.agent/requests/*.json
```

and writes matching results to:

```text
<project>/.agent/results/
```

Supported operation:

```json
{
  "op": "setPinValue",
  "uniqueId": "<document-id> <element-id>",
  "pin": "Input",
  "value": 42,
  "type": "Int32"
}
```

The node applies this via:

```csharp
SessionNodes.CurrentSolution
    .SetPinValue(uid, pin, value)
    .Confirm(SolutionUpdateKind.Default);
```

This makes the edit undo-integrated in vvvv.

Defensive operation:

- `"paste"` requests are rejected. The experiment showed that calling
  `SessionNodes.Paste(...)` from `CommandProcessor.Update()` can mutate the editor
  graph while the patch editor is rendering, causing
  `Collection was modified; enumeration operation may not execute`.

## MCP Loop

```text
EditorWatcher -> .agent/editor-state.json -> vl-mcp -> MCP client
MCP client -> .agent/requests/*.json -> CommandProcessor -> .agent/results/*.json
```

See `tools/vl-mcp/README.md`.

## Build

```powershell
dotnet build bridge\VL.Agent\VL.Agent.csproj
```

If vvvv is installed elsewhere, override `VvvvInstall`:

```powershell
dotnet build bridge\VL.Agent\VL.Agent.csproj -p:VvvvInstall="C:\Program Files\vvvv\vvvv_gamma_7.2-win-x64"
```

## Public API Finding

`VL.HDE.API` exposes editor state globally through channels/observables. This made
the bridge simple: a node can read `LoadedDocuments`, `CurrentSelection`, and
compiler messages without wiring those services through user patches.

For the full API validation, see:

```text
research/windows-api-validation-findings.md
```
