# vl-mcp

MCP stdio server for `agentic-vl`.

It speaks newline-delimited JSON-RPC 2.0 directly and logs only to stderr so stdout
stays a clean protocol stream. It reuses `vl-map` for static indexing and reads the
live editor snapshot written by `VL.Agent`.

## Tools

| Tool | Status | What it does |
|---|---|---|
| `vvvv_index_project` | read-only | Indexes a vvvv project directory and returns definitions, dependencies, document graph, version drift, missing document deps, duplicate IDs, and parse failures. |
| `vvvv_editor_state` | read-only | Reads `.agent/editor-state.json`, which is written by the in-vvvv `EditorWatcher` node. |
| `vvvv_context_query` | read-only | Returns compact live editor slices (`summary`, `documents`, `selection`, `compilerMessages`, or `raw`) and static `vl-map` graph slices (`projectGraph`, `patchGraph`, or `nodeContext`) so agents can make decisions without pulling the full index by default. |
| `vvvv_node_query` | read-only/live | Queries the active patch's live symbol resolver for node-browser candidates. Returns compact `addNode` symbols plus input/output pin names and types. |
| `vvvv_set_pin_value` | write | Drops a request for the in-vvvv `CommandProcessor` node to set one input pin value through vvvv's undo-integrated solution API. |
| `vvvv_apply_graph_transaction` | experimental write | Sends a GraphTransaction batch. Supports `dryRun`, `validate`, batching `setPin` operations into one undo-integrated confirm, experimental `addNode`/`addPad` creation on the active canvas, `connect` for created aliases and live `UniqueId` endpoints, `setBounds` for selected live nodes/pads, and first-slice `select`. |
| `vvvv_paste` | experimental write | Drops a clipboard-style Canvas snippet for deferred paste into the active canvas. Requires `experimental=true`; supports `pauseRuntime` and `leaveRuntimePaused`. |

`vvvv_set_pin_value` requires a running `CommandProcessor` node in the patch and a
`UniqueId` from `vvvv_editor_state`.

`vvvv_apply_graph_transaction` uses `schemas/graph-transaction.schema.json`.
Use `vvvv_node_query` before placing unfamiliar nodes; it is scoped to the
current active patch and latest live compilation, so it reflects installed
packages and project dependencies.
For now, `setPin` targets must be written as `<UniqueId>:<PinName>`. A transaction
with multiple `setPin` ops is accumulated and committed with one `Confirm(...)`.
`addNode` creates operation/process nodes on the active editor canvas and returns
created alias-to-`UniqueId` mappings. `addPad` creates typed IOBoxes on the
active editor canvas. `connect` can link transaction-created pad aliases,
transaction-created node pins written as `<alias>:<PinName>`, existing pads, and
existing node pins written as `<NodeUniqueId>:<PinName>`. For created nodes,
connect dry-runs validate pin direction and pin type from the resolved live node
definition, so insert conversion/join nodes instead of connecting incompatible
types directly.
`setBounds` currently requires the target node or pad to be selected in the live
editor and uses a full `UniqueId` target. `select` can select aliases created
earlier in the same transaction, or reselect full `UniqueId` targets that are
already live-resolved through the current editor selection. Use `dryRun=true` to
check target parsing, type coercion, node symbol resolution, endpoint direction,
endpoint type compatibility, and validation without applying the solution.
Copyable payloads live in
`examples/graph-transactions/`.

### Static Graph Context

`vvvv_context_query` can now expose the parser graph that `vl-map` extracts from
`.vl` XML. This is the MCP-facing slice of the codebase-memory idea: agents can
ask for a project graph, a patch-local graph, or inbound/outbound context around
one node/pad without reparsing XML themselves.

Examples:

```json
{ "kind": "projectGraph", "projectPath": "C:\\path\\to\\project" }
```

```json
{
  "kind": "patchGraph",
  "projectPath": "C:\\path\\to\\project",
  "documentPath": "VL.Agent.HDE.vl",
  "limit": 100
}
```

```json
{
  "kind": "nodeContext",
  "projectPath": "C:\\path\\to\\project",
  "documentPath": "VL.Agent.HDE.vl",
  "nodeId": "FbFB0RjCD4sLvbKqmS5ykS"
}
```

`documentPath` accepts the exact project-relative path or a unique suffix.
`nodeContext` works with node ids and pad ids from `patchGraph`. Hidden links are
omitted by default; pass `includeHidden=true` when inspecting exposed-patch-pin or
routing links.

## Resources

Large read-only context is exposed as MCP resources so it does not need to become
more tool descriptions:

| URI | What it returns |
|---|---|
| `agentic-vl://schema/graph-transaction` | `schemas/graph-transaction.schema.json` |
| `agentic-vl://docs/session-context` | `docs/SESSION_CONTEXT.md` |
| `agentic-vl://docs/windows-testing` | `docs/WINDOWS_TESTING.md` |
| `agentic-vl://docs/graph-transaction-protocol` | `docs/graph-transaction-protocol.md` |
| `agentic-vl://editor/state` | Latest `.agent/editor-state.json`, if present |

The direct paste path is intentionally not stable. Calling `SessionNodes.Paste`
from a patch `Update` was tested and can break the graphical patch editor render
loop with `Collection was modified; enumeration operation may not execute`.
`vvvv_paste` is a dev-only experiment that defers the paste through the UI
synchronization context and writes its result asynchronously. For runtime-heavy
snippets, run the bridge through `AgentHost` in an HDE extension and pass
`pauseRuntime=true`.

## Build

```powershell
dotnet build tools\vl-mcp\vl-mcp.csproj -c Release
dotnet build tools\vl-mcp\vl-mcp.csproj -c Release -p:AgenticVlDev=true
```

Point MCP clients at the built exe:

```text
C:\Users\prt\Documents\1_Projects\agentic-vl\tools\vl-mcp\bin\Release\net10.0\vl-mcp.exe
```

Do not point MCP clients at `dotnet run`; build output on stdout will corrupt the
JSON-RPC stream.

## Example MCP Config

```json
{
  "mcpServers": {
    "vvvv-agent": {
      "command": "C:\\Users\\prt\\Documents\\1_Projects\\agentic-vl\\tools\\vl-mcp\\bin\\Release\\net10.0\\vl-mcp.exe"
    }
  }
}
```

## Dev Mode

For local development on this machine, where SDK 10 may not be installed, use the
dev target and launcher:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\vl-mcp\dev.ps1
```

The launcher builds `net8.0` with `-p:AgenticVlDev=true`, writes build logs to
stderr, then starts:

```text
tools\vl-mcp\bin\Release\net8.0\vl-mcp.exe
```

Continuous compile checks without starting an MCP transport:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\vl-mcp\dev.ps1 -Watch
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

1. In vvvv, reference `VL.Agent/VL.Agent.csproj`.
2. Prefer `VL.Agent.HDE.vl` and its `AgentHost` node so the bridge runs in
   the editor runtime. Patch-local `EditorWatcher` / `CommandProcessor` nodes are
   only for quick tests.
3. Call `vvvv_context_query` with `kind=summary` for a compact read of loaded
   documents, selection, compiler messages, and per-element `UniqueId` values.
   Use `vvvv_editor_state` or `kind=raw` only when the full snapshot is needed.
   Use `kind=projectGraph`, `kind=patchGraph`, or `kind=nodeContext` when the
   decision depends on static `.vl` node/pad/link structure.
4. To test a write, also drop a `CommandProcessor` node with `path` empty and call
   `vvvv_set_pin_value`.
5. To test a transaction, call `vvvv_apply_graph_transaction` with
   `schemaVersion=1`, a label, and one or more `setPin`, `addNode`, `addPad`,
   `connect`, `setBounds`, `select`, or `validate` ops.
6. To test paste, call `vvvv_paste` with a self-contained Canvas snippet,
   coordinates, `experimental=true`, and usually `pauseRuntime=true`. Watch vvvv
   closely; this is not yet a stable editing primitive.

## Command Envelope and Tracing

Live write/resolver tools now submit a versioned request envelope to the mailbox:

```json
{
  "schemaVersion": 1,
  "requestId": "...",
  "traceId": "...",
  "op": "nodeQuery",
  "transport": "fileMailbox",
  "createdAtUtc": "...",
  "deadlineMs": 5000,
  "payload": {}
}
```

`CommandProcessor` still accepts the older direct request shape, but new MCP
requests use the envelope so the same command dispatcher can later be fed by a
named-pipe or WebSocket transport. Results include trace timing fields such as
`mailboxWaitMs`, `processingMs`, and `roundTripMs`.

## Protocol

Implemented methods:

- `initialize`
- `resources/list`
- `resources/read`
- `tools/list`
- `tools/call`
- `ping`

Notifications are ignored. All diagnostics go to stderr.
