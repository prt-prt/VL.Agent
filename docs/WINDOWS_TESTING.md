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

- [ ] Point MCP client at built `vl-mcp.exe`, not `dotnet run`.
- [ ] Run `tools/list` and confirm these tools are listed:
  - `vvvv_index_project`
  - `vvvv_editor_state`
  - `vvvv_set_pin_value`
  - `vvvv_apply_graph_transaction`
  - `vvvv_paste`
- [ ] Run `vvvv_editor_state` and confirm snapshot freshness is reasonable.
- [ ] Select a node/pad in vvvv and confirm `Selection[0].UniqueId` appears.
- [ ] Confirm compiler messages appear in the snapshot when a patch has an error.

## `vvvv_set_pin_value`

- [ ] Set a simple numeric IOBox/pad value by `UniqueId`.
- [ ] Set a Boolean input.
- [ ] Set a String input.
- [ ] Set `Float32` and `Float64` explicitly with type hints.
- [ ] Confirm edits are undo-integrated in vvvv.
- [ ] Confirm invalid `UniqueId` returns a structured error.
- [ ] Confirm invalid pin name returns a structured error or compiler message.

## `vvvv_apply_graph_transaction` First Slice

Use `schemas/graph-transaction.schema.json` as the contract.
Use `examples/graph-transactions/` as copyable payloads.

### Dry Run

- [ ] `dryRun=true` with a valid `setPin` target returns:
  - `ok: true`
  - `checkedOps > 0`
  - `appliedOps: 0`
- [ ] `dryRun=true` with invalid target syntax returns diagnostics.
- [ ] `dryRun=true` with unparseable `UniqueId` returns diagnostics.
- [ ] `validate=false` suppresses default compiler validation.
- [ ] Explicit `validate` op with `checks: ["compile"]` reports compiler messages.
- [ ] Explicit `validate` op with `checks: ["runtimeMessages"]` reports runtime messages.

### Apply

- [ ] One valid `setPin` op changes the pin value.
- [ ] Multiple valid `setPin` ops apply in one transaction.
- [ ] Multiple `setPin` ops produce one undo step, if vvvv groups the accumulated
  `Confirm(...)` as expected.
- [ ] Any invalid op prevents the whole transaction from applying.
- [ ] Any unsupported structural op (`addNode`, `addPad`, `connect`, etc.) prevents
  partial application and appears in `unsupported`.
- [ ] Compiler diagnostics after apply are included when `validate` is true.

### Target Format

- [ ] Confirm `<UniqueId>:<PinName>` is a usable endpoint format for all tested pins.
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
