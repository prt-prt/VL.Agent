# Agent Client Protocol (ACP)

Reference: <https://agentclientprotocol.com/get-started/introduction>

ACP standardizes the side of the pipe `VL.Agent` does not build yet. `vl-mcp`
makes vvvv consumable *by* an agent. ACP describes how an *editor hosts* an
agent: the editor is a JSON-RPC 2.0 client that spawns the agent as a
subprocess, renders its streaming session, answers its permission requests, and
serves it file access. It is LSP-for-agents, and it does not compete with MCP.

## Role mapping

| ACP role | Would be | Speaks |
|---|---|---|
| Client | vvvv / `VL.Agent.HDE.vl` / `AgentHost` | `session/request_permission`, `fs/read_text_file`, `fs/write_text_file`, `terminal/*` |
| Agent | Claude Code, Gemini CLI, Goose, … | `initialize`, `session/new`, `session/load`, `session/prompt`, `session/cancel`, `session/set_mode` |

Baseline is `initialize`, `session/prompt`, and permission handling. Session
loading, file system access, and terminals are capability-gated. Paths are
absolute, line numbers are 1-indexed, properties are `camelCase`, and
discriminators are `snake_case`. Custom extensions use `_meta` fields and
underscore-prefixed method names.

## The two protocols compose

An ACP client passes a list of MCP servers to the agent when it creates a
session. A vvvv-hosted ACP client could therefore auto-wire `vl-mcp` into
whatever agent the user picks. That removes the manual `mcpServers` config step
`setup/README.md` currently generates and makes `VL.Agent` agent-vendor-neutral
without further work. `vl-mcp` stays exactly what it is; ACP reuses MCP's JSON
representations deliberately.

## Implications for this repository

1. **Native agent panel without vendor lock-in.** One ACP client inside the HDE
   replaces N per-vendor integrations.
2. **Standard permission UX for writes.** `session/request_permission` is the
   formalized version of what `vvvv_set_pin_value` and
   `vvvv_apply_graph_transaction` need — approve/deny rendered in the editor,
   keeping the undo-integrated safety story but making it visible.
3. **Unsaved editor buffers instead of disk.** ACP clients implement
   `fs/read_text_file` / `fs/write_text_file` so agents see live buffers. This is
   the cleaner channel for the live-state-vs-disk split that
   `.agent/editor-state.json` works around today.
4. **Structural fix for mailbox latency.** ACP is the "JSON-RPC over stream"
   candidate in `agent-bridge-optimization-roadmap.md`, and `session/update`
   notifications are the standardized shape of that note's context-push
   direction. The existing `AgentCommand` dispatcher boundary is the seam.
   Remote agents over WebSocket are still under development upstream.
5. **Graph edits stay a custom contract.** ACP tool-call rendering is
   text-file/diff/terminal-centric. Graph transactions surface through tool-call
   content plus `_meta` extensions. Visual verification frames can ship as image
   content in tool-call updates, which ACP supports natively.

## Cost

Official libraries are Rust and TypeScript; there is no first-party .NET
library, so the client would be implemented against the JSON schema. The wire
format is JSON-RPC 2.0 over stdio, which `vl-mcp` already speaks, and the client
half of ACP is the smaller surface: spawn the process, implement a handful of
methods, render updates.

## Direction

Keep `vl-mcp` as the tool layer. Shape the near-term transport work — pushed
command lifecycle events, then named pipe or WebSocket — so the same dispatcher
can later back an ACP client host in the HDE. Prefer adopting ACP's session,
streaming, and permission semantics over designing an equivalent bespoke push
protocol.
