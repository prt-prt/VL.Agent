# vl-mcp

MCP stdio server for `agentic-vl`.

It speaks newline-delimited JSON-RPC 2.0 directly and logs only to stderr so stdout
stays a clean protocol stream. It reuses `vl-map` for static indexing and reads the
live editor snapshot written by `bridge/VL.Agent`.

## Tools

| Tool | Status | What it does |
|---|---|---|
| `vvvv_index_project` | read-only | Indexes a vvvv project directory and returns definitions, dependencies, document graph, version drift, missing document deps, duplicate IDs, and parse failures. |
| `vvvv_editor_state` | read-only | Reads `.agent/editor-state.json`, which is written by the in-vvvv `EditorWatcher` node. |
| `vvvv_set_pin_value` | write | Drops a request for the in-vvvv `CommandProcessor` node to set one input pin value through vvvv's undo-integrated solution API. |

`vvvv_set_pin_value` requires a running `CommandProcessor` node in the patch and a
`UniqueId` from `vvvv_editor_state`.

Node insertion/paste is intentionally not exposed. Calling `SessionNodes.Paste`
from a patch `Update` was tested and can break the graphical patch editor render
loop with `Collection was modified; enumeration operation may not execute`.

## Build

```powershell
dotnet build tools\vl-mcp\vl-mcp.csproj -c Release
```

Point MCP clients at the built exe:

```text
C:\Users\3e8\projects\agentic-vl\tools\vl-mcp\bin\Release\net10.0\vl-mcp.exe
```

Do not point MCP clients at `dotnet run`; build output on stdout will corrupt the
JSON-RPC stream.

## Example MCP Config

```json
{
  "mcpServers": {
    "vvvv-agent": {
      "command": "C:\\Users\\3e8\\projects\\agentic-vl\\tools\\vl-mcp\\bin\\Release\\net10.0\\vl-mcp.exe"
    }
  }
}
```

Launch the MCP client from the vvvv project directory. By default the server reads:

```text
<project>/.agent/editor-state.json
```

and writes requests/results under:

```text
<project>/.agent/requests/
<project>/.agent/results/
```

`VVVV_AGENT_STATE` or per-call `path`/`projectPath` arguments can override the
defaults where supported.

## Live-State Loop

1. In vvvv, reference `bridge/VL.Agent/VL.Agent.csproj`.
2. Drop an `EditorWatcher` node into the project patch and leave `path` empty.
3. Call `vvvv_editor_state` from the MCP client to see loaded documents, selection,
   compiler messages, and per-element `UniqueId` values.
4. To test a write, also drop a `CommandProcessor` node with `path` empty and call
   `vvvv_set_pin_value`.

## Protocol

Implemented methods:

- `initialize`
- `tools/list`
- `tools/call`
- `ping`

Notifications are ignored. All diagnostics go to stderr.
