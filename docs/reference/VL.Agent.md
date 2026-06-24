# VL.Agent

Code that runs inside vvvv gamma. This is the live-editor side of `agentic-vl`.

## `VL.Agent`

C# node library in the `Agent` category, built for `net8.0-windows` against the
installed vvvv gamma 7.2 assemblies.

Verified on Windows with vvvv gamma 7.2 on 2026-06-19.

`VL.Agent.HDE.vl` is the preferred host. It runs the bridge in the editor/HDE
runtime, outside the user patch being edited.

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

Supported operations:

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

Experimental batch operation:

```json
{
  "op": "graphTransaction",
  "transaction": {
    "schemaVersion": 1,
    "label": "Tune selected parameters",
    "ops": [
      {
        "op": "setPin",
        "target": "<UniqueId>:Input",
        "value": 42,
        "type": "Int32"
      }
    ],
    "validate": true
  }
}
```

The current transaction slice supports `dryRun`, `validate`, batched `setPin`,
`addNode`, `addPad`, `connect`, model-resolved `setBounds`, and model-resolved
`select`. Structural graph edits are still experimental: use `dryRun=true` first
and treat `partial:true` results as a signal that earlier structural steps may
have committed before a later failure.

The `CommandProcessor` implementation is split into focused partial files:

- `CommandProcessor.cs` - public process-node surface and frame update loop.
- `CommandProcessor.Mailbox.cs` - request envelope, mailbox transport, result
  writing, and trace metadata.
- `CommandProcessor.Commands.cs` - top-level command handlers.
- `CommandProcessor.NodeQuery.cs` - live node-browser resolver.
- `CommandProcessor.GraphNodes.cs` - node/pad creation and alias pin setting.
- `CommandProcessor.GraphConnect.cs` - connect planning, model endpoint lookup,
  and link verification.
- `CommandProcessor.GraphSelection.cs` - selection and bounds edits.
- `CommandProcessor.GraphEnvironment.cs` - active canvas, type mapping, and
  deferred paste.
- `CommandProcessor.LiveValues.cs` - selected live values, coercion,
  validation diagnostics, and reflection helpers.

`CommandProcessor` can still be dropped into a normal patch for quick tests, but
that mode is fragile. If the user patch pauses or throws during `Update`, the
processor stops too and cannot apply recovery commands.

### `AgentHost`

Process node intended for `.HDE.vl` editor extensions. It wraps `EditorWatcher`
and `CommandProcessor`, resolves the `.agent` directory from the loaded non-HDE
user document, and keeps processing in the editor runtime.

Use the included scaffold:

```text
VL.Agent.HDE.vl
```

This is the recommended architecture for live writes.

Experimental operation:

```json
{
  "op": "paste",
  "snippet": "<Canvas ... />",
  "x": 300,
  "y": 300,
  "experimental": true
}
```

The old direct paste experiment showed that calling `SessionNodes.Paste(...)` from
`CommandProcessor.Update()` can mutate the editor graph while the patch editor is
rendering, causing `Collection was modified; enumeration operation may not
execute`.

The current paste path requires `experimental=true`, captures the current UI
`SynchronizationContext`, posts the actual paste until after `Update` returns, and
writes the result asynchronously.

Paste also supports `pauseRuntime` and `leaveRuntimePaused`. These are useful only
when the command processor runs in `AgentHost` / HDE mode: the user runtime can be
paused while the editor-runtime bridge continues to process requests.

## MCP Loop

```text
AgentHost -> .agent/editor-state.json -> vl-mcp -> MCP client
MCP client -> .agent/requests/*.json -> AgentHost -> .agent/results/*.json
```

See `tools/vl-mcp/README.md`.

## Build

```powershell
dotnet build VL.Agent\VL.Agent.csproj
```

If vvvv is installed elsewhere, override `VvvvInstall`:

```powershell
dotnet build VL.Agent\VL.Agent.csproj -p:VvvvInstall="C:\Program Files\vvvv\vvvv_gamma_7.2-win-x64"
```

## Public API Finding

`VL.HDE.API` exposes editor state globally through channels/observables. This made
the bridge simple: a node can read `LoadedDocuments`, `CurrentSelection`, and
compiler messages without wiring those services through user patches.

For the full API validation, see:

```text
docs/research/windows-api-validation-findings.md
```
