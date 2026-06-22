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
- [x] Any unsupported structural op (`disconnect`, `annotate`, etc.) prevents
  partial application and appears in `unsupported`.
- [x] Compiler diagnostics after apply are included when `validate` is true.

### `addNode`

- [x] `dryRun=true` with a valid operation node returns `ok:true`,
  `checkedOps > 0`, `appliedOps:0`.
- [x] One valid operation node creates a visible node on the active editor
  canvas (`operation:Math:+`).
- [x] One valid process node creates a visible node on the active editor canvas
  (`process:Animation:LFO`).
- [x] `addNode` plus `select` can select the transaction-created node alias.
- [x] The live editor snapshot reports selected created nodes as `VL.Model.Node`
  (`OperationNode: +`, `ProcessNode: LFO`).
- [x] `addNode` with an unresolved symbol returns diagnostics and does not apply
  for a single-op transaction.
- [x] Multiple `addNode` ops are atomic when a later node fails to resolve.
- [x] Browser-display variant symbols returned by `vvvv_node_query` can be used
  for `addNode` (`process:Animation:LFO (MinMax)`,
  `operation:Vector2:Vector (Join)`, `process:Layers:Line`).

### `addPad`

- [x] `dryRun=true` with a valid `addPad` returns `ok:true`, `checkedOps > 0`,
  `appliedOps:0`.
- [x] One valid `addPad` creates a visible typed IOBox on the active editor
  canvas.
- [x] The apply result returns the `alias` mapped to the created pad `UniqueId`.
- [x] Multiple `addPad` ops apply without losing the active canvas context.
- [x] `addPad` creation is undo-integrated in vvvv.
- [x] `addPad` with unsupported type returns diagnostics and does not partially
  apply.
- [x] `addPad` plus `setPin` in one transaction is rejected with diagnostics and
  does not partially apply.

### `setBounds`

- [x] `dryRun=true` with a selected node or pad `UniqueId` returns `ok:true`,
  `checkedOps > 0`, `appliedOps:0`.
- [x] One valid `setBounds` moves a selected node visibly on the active canvas.
- [x] One valid `setBounds` moves a selected pad/IOBox visibly on the active
  canvas.
- [x] `setBounds` with an unselected target returns diagnostics and does not
  apply.
- [x] `setBounds` with invalid bounds shape returns diagnostics and does not
  apply.
- [x] Multiple `setBounds` ops apply without losing selection/live model context.
- [x] `setBounds` is undo-integrated in vvvv.
- [x] Multiple `setBounds` ops are grouped into one undo step.

### `select`

- [x] `select` can select an alias created earlier in the same transaction.
- [x] `select` can reselect a full `UniqueId` target that is already selected in
  the live editor.
- [ ] `select` can resolve arbitrary unselected live graph `UniqueId` targets.
- [ ] Confirm whether selection updates should be undo-integrated.

### `connect`

- [x] `connect` can link two pads created earlier in the same transaction.
- [x] `connect` can link pins on nodes created earlier in the same transaction
  using `<alias>:<PinName>` (`lfo_a:Phase -> plus_a:Input`).
- [x] `connect` rejects a transaction-created node pin direction/type mismatch
  before mutation (`LFO:Phase -> Rectangle:Position` reports
  `Float32 -> Vector2`).
- [x] `connect` commits links by replacing the updated containing patch and
  reports `connected` only after commit/post-commit verification.
- [x] `connect` can link unselected existing nodes by full `UniqueId` using the
  current model solution, without requiring the target nodes to be selected.
- [ ] `connect` can link a created pad alias to a selected node input pin.
- [ ] `connect` can link a selected node output pin to a created pad alias.
- [ ] `connect` can link existing pads by full `UniqueId`.
- [ ] `connect` is undo-integrated as one logical graph transaction.
- [x] Failed structural transactions report `partial:true` when earlier edits
  were committed, and do not claim failed links in `connected`.
- [ ] Failed structural transactions do not leave partially applied prior ops
  across `addNode`/`addPad`/`connect`.

### Target Format

- [x] Confirm `<UniqueId>:<PinName>` is a usable endpoint format for all tested pins.
- [x] Accept transaction-local aliases for `addPad` + `select`.
- [x] Accept transaction-local aliases for `addPad` + `connect`.
- [x] Accept transaction-local aliases for created-node `setPin` targets.
- [x] Accept full unselected live node `UniqueId` endpoints for `connect`.
- [ ] Decide whether target should change to `{ "uniqueId": "...", "pin": "..." }`
  for stronger schema validation.

## Structural Graph Mutation Research

- [ ] Inventory public HDE/session APIs for:
  - [x] add node
  - [x] connect pins
  - disconnect pins
  - set bounds/position
  - select/focus elements
- [x] Inventory public model API for add pad/IOBox:
  `Patch.AddPad(Point2, Canvas, TypeReference, CompileTimeValue, bool)` is
  available in vvvv gamma 7.2.
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

These two scenarios are implemented as a repeatable, timed speed benchmark under
`bench/` (run with `bench/run-bench.ps1`, see `bench/README.md`). The checklists
below remain the manual acceptance criteria; the runner measures wall-clock time
and leaves a session transcript to inspect for efficiency.

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

### 2026-06-22, vvvv gamma 7.2

- After reloading vvvv, the request/result loop processed `vvvv_set_pin_value`
  again: Counter `Increment` changed from `6` to `7`.
- `addPad` dry run succeeded with `ok:true`, `checkedOps:1`, `appliedOps:0`,
  and no diagnostics.
- One real `addPad` succeeded with `ok:true`, `appliedOps:1`, returning
  `created.threshold = SFU9A1UdmjFMZmRewwvBEp VTt0TE7JaRbLQVE9jYRhnX`.
- A multiple-`addPad` transaction succeeded with `appliedOps:2`, returning
  `created.gain = SFU9A1UdmjFMZmRewwvBEp A4jAadjcP1NPsi8XnQUWeG` and
  `created.enabled = SFU9A1UdmjFMZmRewwvBEp NOVOWoOKeLcLU8EjQ419cF`.
- Rejection paths returned `ok:false` and `appliedOps:0` for unsupported type
  (`Vector2`), mixed `addPad` + `setPin`, missing `alias`, and missing `name`.
- Static `vl-map` did not show the new pads because `test.vl` was not saved; the
  live transaction result is the source of truth for unsaved structural edits.
- Manual UI confirmation: the created IOBoxes appeared on the active canvas, and
  vvvv Undo removed the created pad(s) as expected.
- `setBounds` first slice added after `addPad`: selected-live-target only, full
  `UniqueId` target only. With Counter selected, dry run returned `ok:true`,
  `checkedOps:1`, `appliedOps:0`.
- `setBounds` applied to selected Counter
  `SFU9A1UdmjFMZmRewwvBEp UhSG2UbN7IqL942w84CAEP` with bounds
  `[520,260,85,19]`, returning `ok:true` and `appliedOps:1`.
- `setBounds` rejection paths returned `ok:false` and `appliedOps:0` for an
  unselected target and invalid bounds shape.
- Manual UI confirmation: the selected Counter visibly moved and vvvv Undo
  restored its previous bounds.
- With three IOBoxes selected (`CR8Vdqwn1uONB4FnGdgoz3`,
  `QOcpcwSVrbSMW3fOtFJjdK`, `IfO1GPC77UfNNqc6ep5AQS`), `setBounds` dry run
  returned `ok:true`, `checkedOps:3`, `appliedOps:0`; apply returned `ok:true`,
  `checkedOps:3`, `appliedOps:3`, and the live snapshot still reported all three
  pads selected afterwards.
- Manual UI confirmation: the three IOBoxes moved visibly, and vvvv Undo restored
  them step by step, not as one grouped undo operation.
- After changing `setBounds` to accumulate all replacements into one solution and
  call `MakeCurrent(...)` once, the three-IOBox move to x `700` returned
  `ok:true`, `appliedOps:3`; manual UI confirmation showed one vvvv Undo restored
  all three pads together.
- `select` first slice added after `setBounds`: transaction-created aliases and
  already-selected live `UniqueId` targets only. `addPad` + `select` with
  `validate:false` returned `ok:true`, `appliedOps:2`, created
  `selected_probe = SFU9A1UdmjFMZmRewwvBEp VoMRCI3A1tjOpDGUr1oh4n`, and the next
  editor snapshot reported that pad selected as `VL.Lang.PublicAPI.LiveDataHub`.
- A follow-up `select` using the selected pad's full `UniqueId` returned
  `ok:true`, `appliedOps:1`, and the editor snapshot still reported that pad
  selected.
- `connect` first slice added after `select`: transaction-created pad aliases,
  selected pad `UniqueId`s, and selected node pins by `<NodeUniqueId>:<PinName>`.
  The first live `addPad + addPad + connect` attempt created both pads but failed
  during link creation because the apply path assumed endpoint proxy/canvas
  objects were non-null; this exposed that structural transactions are not fully
  atomic yet.
- After making `connect` resolve the undo canvas defensively, a fresh
  `addPad + addPad + connect + select` transaction returned `ok:true`,
  `appliedOps:4`, created `source2 =
  SFU9A1UdmjFMZmRewwvBEp VScvH2byI78PbwODT2DKhy`, created `sink2 =
  SFU9A1UdmjFMZmRewwvBEp KL9hjWFQM89LnsLyIEFeV2`, and returned one connected
  link (`Id 1749->Id 1750`). The next editor snapshot reported both created
  pads selected.
- Later on 2026-06-22, `connect` was extended beyond this first slice: existing
  node endpoints now resolve from `DevEnvHost.Instance.CurrentSolution` by full
  `UniqueId`, so follow-up repair transactions no longer require those nodes to
  be selected.
- `addNode` dry run for `operation:Math:+` returned `ok:true`,
  `checkedOps:1`, `appliedOps:0`.
- One real `addNode` for `operation:Math:+` returned `ok:true`,
  `appliedOps:1`, created
  `plus1 = SFU9A1UdmjFMZmRewwvBEp B3KkTLtiZXrMTBteQL8sap`.
- `addNode + select` for `operation:Math:+` returned `ok:true`,
  `appliedOps:2`, created and selected
  `plus_selected = SFU9A1UdmjFMZmRewwvBEp JwriwqFsTaBMnn4ouyDPr5`. The next
  editor snapshot reported the selection as `VL.Model.Node`,
  `OperationNode: +`.
- A single invalid `addNode` symbol (`operation:Math:DefinitelyNotANode`)
  returned `ok:false`, `appliedOps:0`, and a resolver diagnostic without
  creating a node.
- A mixed valid + invalid `addNode` transaction exposed a structural atomicity
  bug: it returned `ok:false` and `appliedOps:0`, but still reported
  `created.partial_good = SFU9A1UdmjFMZmRewwvBEp PXoiWP0eKVYMPuqAYWXynm`.
  Manual visual confirmation showed that partial node was created.
- `process:Animation:LFO` dry run returned `ok:true`; real
  `addNode + select` returned `ok:true`, `appliedOps:2`, created and selected
  `lfo_selected = SFU9A1UdmjFMZmRewwvBEp VTE5rv5TH9KPskBYvannhb`. The editor
  snapshot reported `VL.Model.Node`, `ProcessNode: LFO`.
- Manual visual confirmation: four new nodes were visible in the editor from the
  test sequence, three `Math +` nodes and one `LFO` node.
- First `skia-line` benchmark run completed through `bench/run-bench.ps1` in
  29.1 seconds with exit code 0 and no graph transaction mutations
  (`loopOps:0`). The spawned agent used two compact `vvvv_context_query` reads
  and stopped correctly: with no selected live targets and current `connect`
  limited to transaction-created pads plus selected live pads/node pins, building
  the animated Skia graph needs connecting pins on transaction-created nodes.
- After updating `connect` endpoint resolution, a live transaction created
  `process:Animation:LFO` and `operation:Math:+`, connected
  `lfo_a:Phase -> plus_a:Input`, and returned `ok:true`, `appliedOps:4`,
  `connected:["Id 2747->Id 2763"]`. A missing-pin created node endpoint was
  rejected before apply with `created node endpoint requires :<PinName>`.
- A follow-up `skia-line` benchmark run after the created-node-pin `connect`
  fix completed in 85.7 seconds with exit code 0 and no graph transaction
  mutations (`loopOps:0`). The spawned agent no longer reported created-node
  pin connection as the blocker; it stopped on the next gap: it needs live
  node-browser symbol and pin discovery for Skia renderer/line nodes, or an
  already selected live target, to avoid inventing Skia symbols/endpoints.
- Added live `nodeQuery` over the active patch resolver. Querying `LFO` returned
  `process:Animation:LFO` with pins such as `Period`, `Phase`, and `Reset`.
  Querying `Skia Line` returned `process:Layers:Line` with `Point A`, `Point B`,
  `Paint`, and `Output: Layer`.
- A `skia-line` benchmark run with `vvvv_node_query` available completed in
  71.0 seconds with exit code 0 and no graph transaction mutations
  (`loopOps:0`). The spawned agent used node queries and verified `Animation:LFO`
  plus `Layers:Line`, then stopped on the next gap: it needs a discoverable Skia
  render-pipeline recipe/symbol set for texture rendering, white background fill,
  layer composition, and converting `Phase` to animated `Vector2` endpoints.
- After hardening the `skia-line` prompt to require bounded trial-and-error, the
  benchmark completed in 141.2 seconds with exit code 0 and real transaction
  attempts. The spawned agent tried `LFO -> Vector2 joins -> Skia Line ->
  Renderer (OffScreen)`. Dry runs accepted the graph, but real apply failed to
  resolve `operation:Vector2:Vector (Join)` / `operation:Vector2:Vector`. Because
  structural `addNode` is not atomic yet, two LFO nodes were partially created.
  This is now a useful benchmark signal: fix dry-run/apply resolver parity and
  structural atomicity before expecting reliable autonomous graph construction.
- `graphTransaction` now resolves all planned `addNode` symbols before apply.
  A mixed valid+invalid apply (`process:Animation:LFO` plus unresolved
  `operation:Vector2:Vector (Join)`) returned `ok:false`, `appliedOps:0`, and an
  empty `created` map before mutation.
- Created-node alias `setPin` works in the same transaction. Applying
  `process:Animation:LFO` plus `setPin lfo_alias_set:Period` returned
  `ok:true`, `appliedOps:3`, selected the created node, and reported the pin as
  unverified rather than failed.
- Browser-display variants now dry-run/apply through exact symbol references:
  `process:Animation:LFO (MinMax)` accepted `Period`, `Minimum`, and `Maximum`
  alias pins; `operation:Vector2:Vector (Join)` and `process:Paint:Stroke`
  dry-ran successfully after category-suffix matching; `process:Layers:Line`
  dry-ran with `Point A` and `Point B`.
- Latest `skia-line` benchmark run
  (`bench/runs/20260622-155351-skia-line.*`) completed in 185.1 seconds. The
  spawned agent created an animated texture attempt using `Animation:LFO (MinMax)`
  and `Source.Rectangle`; final summary reported no new compiler/runtime
  diagnostics, but visual inspection showed nodes were not wired into a
  functional patch.
- Follow-up investigation fixed the connect path. Bad created-node dry-run
  `LFO:Phase -> Rectangle:Position` now fails with `type mismatch Float32 ->
  Vector2`; valid route `LFO:Phase -> Vector (Join):X -> Rectangle:Position`
  applies with two connected links; and connect-only repair transactions can
  target older unselected nodes by full `UniqueId`.
  Remaining blocker for full Skia-line completion: `Texture` output pads are
  unsupported, so visual/output verification still needs another exposure path.
