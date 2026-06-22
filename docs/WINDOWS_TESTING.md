# Windows Testing Checklist

Running tab for checks that need a Windows machine with vvvv gamma installed.

Current baseline:

- Target vvvv: gamma 7.2
- Bridge target: `net8.0-windows`
- Tools target: `net10.0`
- Preferred host: `VL.Agent.HDE.vl` with `AgentHost`

## Environment

- [ ] Confirm vvvv gamma install path, default:
  `C:\Program Files\vvvv\vvvv_gamma_7.2-win-x64`
- [ ] Confirm .NET SDK 10 is available:
  `dotnet --info`
- [ ] Build standalone tools:
  `dotnet build tools\vl-mcp\vl-mcp.csproj -c Release`
- [ ] Build `VL.Agent`:
  `dotnet build VL.Agent\VL.Agent.csproj`
- [ ] If vvvv is elsewhere, rebuild bridge with:
  `-p:VvvvInstall="C:\path\to\vvvv_gamma_7.2-win-x64"`

## HDE AgentHost

- [ ] Open/install `VL.Agent.HDE.vl` as an editor extension.
- [ ] Open a normal user `.vl` project in another tab.
- [ ] Confirm `AgentHost` resolves the user project, not the `.HDE.vl` document.
- [ ] Confirm it writes:
  `<project>\.agent\editor-state.json`
- [ ] Confirm request/result dirs are created or used:
  `<project>\.agent\requests\`
  `<project>\.agent\results\`
- [ ] Pause the user runtime and confirm `AgentHost` still processes requests.
- [ ] Trigger a user patch exception and confirm `AgentHost` still writes snapshots.

## MCP Smoke Tests

- [x] Point MCP client at built `vl-mcp.exe`, not `dotnet run`.
- [x] Run `tools/list` and confirm these tools are listed:
  - `vvvv_index_project`
  - `vvvv_editor_state`
  - `vvvv_context_query`
  - `vvvv_set_pin_value`
  - `vvvv_apply_graph_transaction`
  - `vvvv_paste`
- [x] Run `resources/list` and confirm these resources are listed:
  - `agentic-vl://schema/graph-transaction`
  - `agentic-vl://docs/session-context`
  - `agentic-vl://docs/windows-testing`
  - `agentic-vl://docs/graph-transaction-protocol`
  - `agentic-vl://editor/state`
- [x] Run `vvvv_editor_state` and confirm snapshot freshness is reasonable.
- [x] Run `vvvv_context_query` with `kind=summary` and confirm compact counts,
  selection, and compiler messages match the raw snapshot.
- [x] Select a node/pad in vvvv and confirm `Selection[0].UniqueId` appears.
- [x] Confirm compiler messages appear in the snapshot when a patch has an error.

## `vvvv_set_pin_value`

- [x] Set a simple numeric IOBox/pad value by `UniqueId`.
- [ ] Set a Boolean input.
- [ ] Set a String input.
- [ ] Set `Float32` and `Float64` explicitly with type hints.
- [ ] Confirm edits are undo-integrated in vvvv.
- [x] Confirm invalid `UniqueId` returns a structured error.
- [ ] Confirm invalid pin name returns a structured error or compiler message.

## `vvvv_apply_graph_transaction` First Slice

Use `schemas/graph-transaction.schema.json` as the contract.
Use `examples/graph-transactions/` as copyable payloads.

### Dry Run

- [x] `dryRun=true` with a valid `setPin` target returns:
  - `ok: true`
  - `checkedOps > 0`
  - `appliedOps: 0`
- [x] `dryRun=true` with invalid target syntax returns diagnostics.
- [x] `dryRun=true` with unparseable `UniqueId` returns diagnostics.
- [x] `validate=false` suppresses default compiler validation.
- [x] Explicit `validate` op with `checks: ["compile"]` reports compiler messages.
- [x] Explicit `validate` op with `checks: ["runtimeMessages"]` reports runtime messages.

### Apply

- [x] One valid `setPin` op changes the pin value.
- [x] Multiple valid `setPin` ops apply in one transaction.
- [x] Multiple `setPin` ops produce one undo step, if vvvv groups the accumulated
  `Confirm(...)` as expected.
- [x] Any invalid op prevents the whole transaction from applying.
- [x] Any unsupported structural op (`addNode`, `addPad`, `connect`, etc.) prevents
  partial application and appears in `unsupported`.
- [x] Compiler diagnostics after apply are included when `validate` is true.

### Target Format

- [x] Confirm `<UniqueId>:<PinName>` is a usable endpoint format for all tested pins.
- [ ] Decide whether aliases should be accepted before structural ops exist.
- [ ] Decide whether target should change to `{ "uniqueId": "...", "pin": "..." }`
  for stronger schema validation.

## Structural Graph Mutation Research

- [ ] Inventory public HDE/session APIs for:
  - add node
  - add pad/IOBox
  - connect pins
  - disconnect pins
  - set bounds/position
  - select/focus elements
- [ ] Test whether `VL.Model.Patch` fluent builders can be applied live through
  `CurrentSolution` or another undo-integrated path.
- [ ] Test whether `Document.WithPatch(...).SaveAsync()` round-trips and hot-reloads.
- [ ] Test whether structural edits can be grouped into one undo step.
- [ ] Test whether editor-side paste is stable when run from `AgentHost` with the
  user runtime paused.

## Live Probes

- [ ] Subscribe to selected `ILiveElement.DataStream`.
- [ ] Read values for N frames without flooding the request/result loop.
- [ ] Confirm `LatestMessagesFromAllRuntimes` is accessible from `AgentHost`.
- [ ] Confirm `LatestMessagesFromCompiler` is accessible from `AgentHost`.
- [ ] Test reading public channels through `IChannelHub`.
- [ ] Test adding temporary probe/annotation messages with `SessionNodes.AddMessage`.

## Scenario Benchmarks

Particle system:

- [ ] Agent can create/tune particle parameters via transaction.
- [ ] Probe particle count or output bounds.
- [ ] Detect runtime exceptions and frame-time issues.

Skia + OSC:

- [ ] Agent can create/tune OSC-controlled parameters via transaction.
- [ ] OSC inputs update public channels or pads.
- [ ] Skia output changes visibly when test values change.
- [ ] Missing OSC paths or stale values are diagnosable.

## Notes From Windows Runs

Append findings here with absolute dates and vvvv version.

### 2026-06-21, vvvv gamma 7.2

- `VL.Agent.HDE.vl` compiles and runs when placed under `VL.Agent/` and referenced
  from `test.vl`; earlier auto-resolution failed with "project dir could not be
  resolved" when the HDE document was not part of the user project.
- `vl-mcp` `net10.0` build advertises `vvvv_context_query`,
  `vvvv_apply_graph_transaction`, and MCP resources. The stale `net8.0` binary did
  not include these newer tools.
- `vvvv_editor_state`, `agentic-vl://editor/state`, and static
  `vvvv_context_query` graph slices (`projectGraph`, `patchGraph`, `nodeContext`)
  returned expected data.
- `vvvv_context_query` returns fresh live selection and compiler messages from
  `AgentHost`. Message fields required reading public fields on `VL.Lang.Message`,
  not just properties.
- `vvvv_set_pin_value` and `vvvv_apply_graph_transaction` successfully changed
  selected Counter node input pins (`Increment`, `Default`) by `UniqueId` and pin
  name.
- Direct `vvvv_set_pin_value` confirmed visually: selected Counter `Increment`
  changed to `52`.
- Type-hinted graph transaction requests were accepted for existing node pins:
  `Float32` on LFO `Period` and `Boolean` on LFO `Pause`. Those pins are connected
  to IOBoxes in `test.vl`, so this confirms request/coercion path acceptance but
  still needs an unconnected visible test pin for visual behavior.
- Explicit graph transaction validation works: `checks:["compile"]` reports
  existing compiler diagnostics, `checks:["runtimeMessages"]` is accessible and
  returned no diagnostics for the current clean runtime state.
- Batched graph transaction changed two Counter pins in one request
  (`Increment=14`, `Default=3`), returning `ok:true` and `appliedOps:2`.
- A graph transaction apply with default validation enabled refused to apply while
  compiler diagnostics were present, returning `appliedOps:0` and the compiler
  diagnostics.
- Batched graph transaction undo grouping confirmed: after setting two Counter
  pins to `41` and `42`, one vvvv Undo reverted both values together.
- IOBox/pad value mutation works for selected pads via
  `VL.Model.ModelExtensions.ReplaceDescendent(...)` +
  `ModelExtensions.MakeCurrent(...)`. Verified through `vvvv_set_pin_value` with
  full `UniqueId` and with MCP `elementId + documentPath`: visible IOBox values
  changed immediately (`100`, `101`, `102` in the smoke run).
- Mixed graph transactions with a selected pad `Value` and a selected Counter
  pin now read back correctly in an immediate follow-up `vvvv_context_query`
  after the MCP write settle delay (`Value=105`, `Increment=81` in the smoke run).
- `SessionNodes.CurrentSolution` is a `VL.Model.SolutionRecorder` in the HDE host.
  It records/applies node pin edits, but is not inspectable via
  `GetDescendent(UniqueId)` for before/after verification.
