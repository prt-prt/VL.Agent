# vl-mcp

MCP stdio server for `VL.Agent`. Speaks newline-delimited JSON-RPC 2.0 on stdout
and logs only to stderr. It reuses `vl-map` for static indexing and reads the live
editor snapshot written by the in-vvvv `VL.Agent` nodes.

## Tools

| Tool | Status | What it does |
|---|---|---|
| `vvvv_index_project` | read-only | Indexes a vvvv project directory (definitions, dependencies, document graph, version drift, missing deps, duplicate IDs, parse failures). |
| `vvvv_editor_state` | read-only | Reads `.agent/editor-state.json`, written by the in-vvvv `EditorWatcher` node. |
| `vvvv_context_query` | read-only | Returns compact live editor slices and static `vl-map` graph slices (`projectGraph`, `patchGraph`, `nodeContext`). |
| `vvvv_node_query` | read-only/live | Queries the active patch's live symbol resolver for node-browser candidates. |
| `vvvv_set_pin_value` | write | Drops a request for the in-vvvv `CommandProcessor` to set one pin value via vvvv's undo-integrated API. |
| `vvvv_apply_graph_transaction` | experimental | Sends a GraphTransaction batch. See `docs/graph-transaction-protocol.md`. |
| `vvvv_paste` | experimental | Deferred Canvas-snippet paste into the active canvas. |

Writes require a running `CommandProcessor`/`AgentHost` in vvvv and a `UniqueId`
from `vvvv_editor_state`.

## Build

```powershell
dotnet build src\VL.Agent.Mcp\VL.Agent.Mcp.csproj -c Release
```

Point MCP clients at the built exe, **not** `dotnet run` (build output on stdout
corrupts the JSON-RPC stream):

```json
{
  "mcpServers": {
    "vl-agent": {
      "command": "C:\\path\\to\\VL.Agent\\src\\VL.Agent.Mcp\\bin\\Release\\net10.0\\vl-mcp.exe"
    }
  }
}
```

## Runtime convention

Launch the client from the vvvv project directory. By default the server reads
`<project>/.agent/editor-state.json` and exchanges requests/results under
`<project>/.agent/requests/` and `<project>/.agent/results/`. `VVVV_AGENT_STATE`
or per-call `path`/`projectPath` arguments override the defaults.
