# vl-mcp — MCP server for the vvvv-agent

Exposes the vvvv-agent's intelligence to MCP clients such as **Claude Code**. It
speaks the MCP **stdio** transport (newline-delimited JSON-RPC 2.0) directly — no SDK
dependency — and reuses `vl-map`'s indexer.

## Tools

| Tool | What it does |
|---|---|
| `vvvv_index_project` | Statically index a vvvv project dir (`projectPath`) → definitions, dependencies, document graph, version drift, dangling refs, dup IDs. Returns the full index JSON. |
| `vvvv_editor_state` | Read the live editor snapshot written by the in-vvvv `EditorWatcher` node (loaded docs, selection with element ids, compiler messages) and report its age. Optional `path` override. |
| `vvvv_set_pin_value` | Set an input pin's value on an element (`uniqueId`, `pin`, `value`, optional `type`), undo-integrated. Drops a request the in-vvvv `CommandProcessor` node applies, then returns its result. |

`vvvv_index_project` / `vvvv_editor_state` are **read-only**; `vvvv_set_pin_value` **mutates**
the patch (via the undo-safe `ISolution` API) and needs the `CommandProcessor` node running.

## Build

```bash
cd tools/vl-mcp
dotnet build -c Release      # -> bin/Release/net10.0/vl-mcp.exe
```

Point the MCP client at the **built exe**, not `dotnet run` — `dotnet run` can emit
build text on stdout, which corrupts the JSON-RPC stream.

## Wire it into Claude Code

Run `claude mcp add` or drop a `.mcp.json` in your vvvv project (see
`mcp.example.json` here) — just point it at the built exe:

```json
{
  "mcpServers": {
    "vvvv-agent": {
      "command": "C:\\Users\\3e8\\projects\\agentic-vl\\tools\\vl-mcp\\bin\\Release\\net10.0\\vl-mcp.exe"
    }
  }
}
```

**No paths to configure.** Launch Claude Code in your vvvv project directory; the
server defaults to indexing that directory and reading `.agent/editor-state.json`
inside it — the same convention the `EditorWatcher` node writes to. (`$VVVV_AGENT_STATE`
or per-call `path`/`projectPath` args still override if you need them.)

Then in Claude Code: `vvvv_index_project` (defaults to your project) and
`vvvv_editor_state` to see what you currently have selected/open in vvvv.

## The live-state loop (zero config)

1. In vvvv, drop a **`EditorWatcher`** node (category `Agent`, from `bridge/VL.Agent`)
   into your project's patch and leave its `path` empty.
2. It auto-writes to `<project>/.agent/editor-state.json` whenever selection /
   messages / open documents change.
3. Claude Code, launched in that same project, calls `vvvv_editor_state` and sees your
   live editor context — including each selected element's `UniqueId`.
4. To **edit**, also drop a **`CommandProcessor`** node (same package, leave `path` empty).
   Then `vvvv_set_pin_value` (with a `UniqueId` from the snapshot) changes a pin and the
   change is undoable in vvvv.

## Protocol notes

Implements `initialize`, `tools/list`, `tools/call`, `ping`. Echoes the client's
`protocolVersion`. All logging goes to stderr; stdout carries only protocol messages.
