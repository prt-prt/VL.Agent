# agentic-vl

Prototype tooling for agentic development workflows in the
[vvvv gamma](https://vvvv.org/) ecosystem.

The project explores what a text-first `vvvv-agent` could look like: an assistant
that can index vvvv projects, inspect live editor state, explain errors, and apply
small undo-integrated edits through vvvv's public APIs.

This is research/prototype code. The read-only tooling is the stable part; live
mutation is intentionally narrow and experimental.

## What Is Here

- `tools/vl-probe/` - metadata-only public API dumper for an installed vvvv gamma.
- `tools/vl-map/` - static project indexer for `.vl`, `.cs`, `.csproj`, and `.sdsl`.
- `tools/vl-mcp/` - MCP stdio server exposing project indexing and live editor state.
- `bridge/VL.Agent/` - in-vvvv C# node library that writes live editor snapshots and
  applies a limited request-file command loop.
- `bridge/VL.Agent.HDE.vl` - minimal editor-extension host for the agent bridge.
- `research/` - API validation notes, architecture notes, and paste-format findings.
- `deck/` - early pitch/deck artifact.
- `vvvv-sdk/` - historical public vvvv SDK submodule for context only.

## Verified Status

Validated on Windows against vvvv gamma 7.2 on 2026-06-19.

- `vl-probe` builds and reflects vvvv assemblies without executing vvvv code.
- `vl-map` builds and indexes real `.vl` projects, including dependencies, definitions,
  document graph, version drift, duplicate IDs, and dangling document dependencies.
- `EditorWatcher` runs inside vvvv and writes `<project>/.agent/editor-state.json`
  with loaded documents, selection, and compiler messages.
- `AgentHost` can run from an `.HDE.vl` editor extension so the agent control plane
  stays in the editor runtime instead of the user patch runtime.
- `vl-mcp` serves `vvvv_index_project`, `vvvv_editor_state`, and
  `vvvv_set_pin_value` over stdio JSON-RPC.
- `CommandProcessor` can process `setPinValue` requests through
  `SessionNodes.CurrentSolution.SetPinValue(...).Confirm(...)`.
- Experimental dev-only paste is exposed as `vvvv_paste` with `experimental=true`.
  On 2026-06-19, a deferred UI-context paste smoke test returned `ok:true` in
  `test.vl` without compiler messages, but inserted the Stride Box snippet as a
  selected unused `Dummy` node in that minimal patch.

### Safety Note

Programmatic node insertion via direct `SessionNodes.Paste(...)` from the running
`CommandProcessor.Update()` was tested and is **not safe** in this prototype. It
can race the patch editor render pass and trigger:

```text
InvalidOperationException: Collection was modified; enumeration operation may not execute.
```

The deeper architecture issue is that `EditorWatcher` / `CommandProcessor` should
not live in the patch being edited: if that user runtime pauses or throws, the
agent can no longer observe or recover. Prefer hosting `AgentHost` from
`bridge/VL.Agent.HDE.vl`, which runs in the HDE/editor layer and targets the loaded
user document.

`vvvv_paste` remains experimental and requires `experimental=true`. It can pause
the user runtime before paste via `pauseRuntime=true`, and can leave it paused via
`leaveRuntimePaused=true` so inserted runtime-heavy nodes do not execute
immediately.

## Quick Start

Build everything:

```powershell
dotnet build tools\vl-probe\vl-probe.csproj -c Release
dotnet build tools\vl-map\vl-map.csproj -c Release
dotnet build tools\vl-mcp\vl-mcp.csproj -c Release
dotnet build bridge\VL.Agent\VL.Agent.csproj
```

On a machine without the .NET 10 SDK, build the tools in local dev mode:

```powershell
dotnet build tools\vl-mcp\vl-mcp.csproj -c Release -p:AgenticVlDev=true
```

Index a project:

```powershell
dotnet run --project tools\vl-map\vl-map.csproj -c Release -- --project "C:\path\to\vvvv-project"
```

Use the MCP server by pointing your MCP client at the built executable:

```text
tools\vl-mcp\bin\Release\net10.0\vl-mcp.exe
```

Do not point MCP clients at `dotnet run`; build output on stdout corrupts the MCP
stdio stream.

For local MCP development, point the client at:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\vl-mcp\dev.ps1
```

The wrapper rebuilds the `net8.0` dev target before starting the server and sends
build logs to stderr so stdout remains valid MCP JSON-RPC.

## Live Editor Loop

1. Reference `bridge/VL.Agent/VL.Agent.csproj` from a vvvv project.
2. Prefer opening/installing `bridge/VL.Agent.HDE.vl` as an editor extension and
   letting its `AgentHost` node resolve the active user project automatically.
3. Launch the MCP client from the same project directory.
4. Call `vvvv_editor_state` to read loaded documents, selection, element IDs, and
   compiler messages.
5. For narrow write tests, use `vvvv_set_pin_value` with a `UniqueId` from the
   editor snapshot.
6. For paste experiments only, call `vvvv_paste` with a self-contained Canvas
   snippet, `experimental=true`, and usually `pauseRuntime=true`.

The default shared location is:

```text
<project>/.agent/editor-state.json
<project>/.agent/requests/
<project>/.agent/results/
```

## Repository Notes

- Tooling under `tools/` targets `net10.0` by default and can target `net8.0`
  with `-p:AgenticVlDev=true` for local development.
- Code loaded into vvvv targets `net8.0-windows`.
- `testbed/`, `.agent/`, `bin/`, `obj/`, and local scratch files are ignored.
- `test.vl` is local scratch and intentionally ignored.

## Next Work

- Harden and live-test `vvvv_set_pin_value` across more pin/value types.
- Add a safe editor-command based insertion path for nodes and links.
- Add `vl-lint` checks for `.vl` file-format invariants and C# node conventions.
- Surface live values/telemetry through `ILiveDataHub` where feasible.
- Build a small reproducible automated smoke-test harness for the bridge.
