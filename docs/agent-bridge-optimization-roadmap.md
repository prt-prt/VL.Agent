# Agent Bridge Optimization Roadmap

This note records the current direction for improving agency, responsiveness, and
operation speed without losing the safety of the existing HDE-hosted bridge.

## Baseline

The current hot path is:

1. MCP client calls `vl-mcp` over stdio JSON-RPC.
2. `vl-mcp` writes a JSON request to `<project>/.agent/requests`.
3. `VL.Agent` `CommandProcessor` picks the request up on the vvvv frame loop.
4. The command is applied through public vvvv APIs.
5. `CommandProcessor` writes a JSON result to `<project>/.agent/results`.
6. `vl-mcp` polls for that result and returns MCP text content.

This mailbox transport is simple and robust, but it adds avoidable latency and
does not support pushed context or streaming feedback.

## Groundwork Added

Live requests now use a versioned command envelope while still accepting legacy
request shapes:

```json
{
  "schemaVersion": 1,
  "requestId": "guid",
  "traceId": "guid",
  "op": "nodeQuery",
  "transport": "fileMailbox",
  "createdAtUtc": "2026-06-24T...",
  "deadlineMs": 5000,
  "payload": {
    "op": "nodeQuery",
    "query": "LFO",
    "limit": 1
  }
}
```

Results are augmented with:

- `requestId`
- `traceId`
- `op`
- `trace.schemaVersion`
- `trace.envelope`
- `trace.transport`
- `trace.requestFileName`
- `trace.deadlineMs`
- `trace.createdAtUtc`
- `trace.pickedUpAtUtc`
- `trace.processedAtUtc`
- `trace.resultWrittenAtUtc`
- `trace.mailboxWaitMs`
- `trace.processingMs`
- `trace.resultWriteDelayMs`
- `trace.roundTripMs`

`CommandProcessor` now normalizes requests into an internal `AgentCommand` and
dispatches that command separately from the mailbox file reader. This is the
boundary a future named-pipe or WebSocket transport should reuse.

## Measurement

Use the mailbox latency probe while vvvv is running with `AgentHost`:

```powershell
powershell -ExecutionPolicy Bypass -File bench\run-mailbox-latency.ps1 -AgentDir .agent -Count 20
```

The first supported probe op is `nodeQuery`, because it is read-only while still
exercising the live bridge, active canvas, and live compilation resolver.

Interpretation:

- `mailboxWaitMs` approximates time spent waiting for vvvv to pick up the request
  on its frame loop.
- `processingMs` approximates command handling time inside the bridge.
- `roundTripMs` is the bridge-level request/result duration.
- The script's `elapsedMs` also includes the probe's polling interval.

## Next Transport Candidates

### Named Pipe

Best Windows-first candidate. Low overhead, local-only, and suitable for a
persistent command session. The pipe reader must enqueue onto the vvvv-safe
editor/update context before mutating graph state.

### WebSocket

Best inspectable/debuggable candidate. Supports pushed context events, visual
probe streaming, and non-.NET clients easily. Slightly more operational surface
than a named pipe.

### JSON-RPC over Stream

Keep the current MCP-style message model but move the live bridge from file
polling to a persistent stream. This can run over named pipes or WebSockets.

## Context Push Direction

The snapshot file should become a compatibility/debug artifact. The optimized
path should maintain an in-memory session state inside the bridge and push:

- loaded document changes
- active canvas changes
- selection changes
- compiler diagnostics
- runtime messages
- command lifecycle events
- visual probe frames or frame metadata

`vvvv_context_query` can keep returning compact slices, but those slices should
come from the cached session state instead of reparsing a file on every request.

## Visual Verification Contract

Future visual probes should target this shape first, before committing to a
specific transport:

```json
{
  "frameId": 1234,
  "timestampUtc": "2026-06-24T...",
  "source": "Renderer/OffScreen",
  "width": 512,
  "height": 512,
  "hash": "sha256-or-fast-hash",
  "changedPixels": 18492,
  "isBlank": false,
  "thumbnailPath": ".agent/frames/latest.png"
}
```

Start with low-rate PNG thumbnails and frame hashes. Add full-resolution capture
only on demand. Prefer vvvv-native render target or texture capture over generic
computer-use screen automation. Use screen automation only as a fallback for
dialogs, menus, and API gaps.

## Near-Term Implementation Order

1. Gather mailbox latency numbers with the new trace fields.
2. Add pushed command lifecycle events to the internal dispatcher.
3. Prototype a named-pipe transport that feeds the existing `AgentCommand`
   dispatcher.
4. Prototype a WebSocket transport if pushed context or visual frames need easier
   inspection.
5. Add a `FrameProbe` / texture thumbnail node behind the visual verification
   contract.
6. Build a recipe layer for common patch patterns so the agent emits fewer
   low-level graph operations.

