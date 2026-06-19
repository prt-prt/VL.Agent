# vl-mcp — MCP server for the vvvv-agent

Exposes the vvvv-agent's intelligence to MCP clients such as **Claude Code**. It
speaks the MCP **stdio** transport (newline-delimited JSON-RPC 2.0) directly — no SDK
dependency — and reuses `vl-map`'s indexer.

## Tools

| Tool | What it does |
|---|---|
| `vvvv_index_project` | Statically index a vvvv project dir (`projectPath`) → definitions, dependencies, document graph, version drift, dangling refs, dup IDs. Returns the full index JSON. |
| `vvvv_editor_state` | Read the live editor snapshot written by the in-vvvv `EditorWatcher` node (loaded docs, selection with element ids, compiler messages) and report its age. Optional `path` override. |

The two halves mirror the architecture: **pull** project structure on demand, **push**
live editor state via the bridge.

## Build

```bash
cd tools/vl-mcp
dotnet build -c Release      # -> bin/Release/net10.0/vl-mcp.exe
```

Point the MCP client at the **built exe**, not `dotnet run` — `dotnet run` can emit
build text on stdout, which corrupts the JSON-RPC stream.

## Wire it into Claude Code

Either run `claude mcp add` or drop a `.mcp.json` in your vvvv project (see
`mcp.example.json` here). Example:

```json
{
  "mcpServers": {
    "vvvv-agent": {
      "command": "C:\\Users\\3e8\\projects\\agentic-vl\\tools\\vl-mcp\\bin\\Release\\net10.0\\vl-mcp.exe",
      "env": {
        "VVVV_AGENT_STATE": "C:\\Users\\3e8\\AppData\\Local\\vvvv-agent\\editor-state.json"
      }
    }
  }
}
```

Then in Claude Code: `vvvv_index_project` with your project path, and `vvvv_editor_state`
to see what you currently have selected/open in vvvv.

## The live-state loop

1. In vvvv, drop a **`EditorWatcher`** node (category `Agent`, from `bridge/VL.Agent`)
   and set its `path` to the same file as `VVVV_AGENT_STATE` above.
2. It rewrites the snapshot whenever selection / messages / open documents change.
3. Claude Code calls `vvvv_editor_state` and sees your live editor context —
   including the `ElementId`/`MergeId` of selected nodes, which the (upcoming) write
   tools will use to edit pins.

## Protocol notes

Implements `initialize`, `tools/list`, `tools/call`, `ping`. Echoes the client's
`protocolVersion`. All logging goes to stderr; stdout carries only protocol messages.
