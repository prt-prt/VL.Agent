# Session Context

Last updated: 2026-06-24 after live mailbox latency benchmarking, adding a
bounded `nodeQuery` cache, and extending `select`/`setBounds` target resolution
to unselected live graph `UniqueId`s in the model lookup path.

## Purpose

`agentic-vl` explores agentic development workflows for vvvv gamma: static project
understanding, live editor state inspection, and narrow undo-integrated edits
through public vvvv APIs.

## Current Architecture

1. Static read-only intelligence:
   - `vl-map` project index
   - dependency graph
   - package/version drift
   - file-format diagnostics
2. HDE-hosted live bridge:
   - `VL.Agent.HDE.vl`
   - `AgentHost`
   - `EditorWatcher`
   - `CommandProcessor`
3. Narrow writes:
   - `vvvv_set_pin_value`
   - experimental `vvvv_apply_graph_transaction`
4. Future work:
   - safe structural graph transactions
   - live probes via `ILiveElement` / `ILiveDataHub`
   - HDE agent cockpit UI
   - replaceable backend adapter instead of a broad MCP-only surface

## MCP Strategy

MCP is an adapter, not the internal architecture. Keep the tool surface coarse and
small. Use MCP resources for large read-only context such as schemas, handoff
docs, test checklists, and live editor snapshots. Use `vvvv_context_query` for
compact editor-state reads before falling back to raw snapshots.

## Verified

- Windows, 2026-06-19, vvvv gamma 7.2:
  - `vl-probe` reflected public APIs.
  - `vl-map` indexed real projects.
  - `EditorWatcher` wrote `.agent/editor-state.json`.
  - `vvvv_editor_state` read snapshots through MCP.
  - `vvvv_set_pin_value` compiled and used the request/result loop.
  - `AgentHost` ran from an `.HDE.vl` editor extension.
- macOS, 2026-06-20:
  - .NET SDK 10.0.301 installed at `/Users/philipp/.dotnet`.
  - `vl-mcp` builds.
  - `tools/smoke-test.sh` passes, including MCP resources and
    `vvvv_context_query` missing-snapshot behavior.

## Open Windows Checks

Use `docs/WINDOWS_TESTING.md` as the running checklist.

Highest priority:

- Continue from the current graph transaction slices: `addPad`,
  created-node-pin `connect`, model-resolved `setBounds`, and model-resolved
  `select`.
- Windows-verify the new `select` and `setBounds` model-resolution path against
  arbitrary unselected live graph `UniqueId` targets.
- Add support for non-primitive output pads or another way to expose/render
  texture outputs for benchmark verification.
- Add higher-level recipe/help-patch discovery for common graph patterns. Live
  `nodeQuery` now finds node symbols and pins, but the Skia-line benchmark still
  needs a compact way to discover the renderer/fill/composition/vector-building
  recipe without guessing. Cold `nodeQuery` resolver scans can take seconds, so
  recipes or a prebuilt node catalog would improve both reliability and speed.
- Improve structural transaction atomicity beyond the up-front resolver and
  dry-run checks: failures during later structural commits can still leave
  earlier node creation committed, though results now report `partial:true`.
- Inventory safe APIs for remaining structural ops: `disconnect` and `annotate`.

Recently verified / implemented:

- `addNode` now places operation and process nodes in a focused patch through the
  public model path (`ModelExtensions.AddNode`) instead of the failed
  internal-`CreateRed` route. `operation:Math:+` and `process:Animation:LFO`
  both resolved against `DevEnvHost.Instance.LatestCompilation` via
  `SymbolExtensions.GetResolver` + `Resolver.GetCandidates`, were finalized with
  `NodeRefHelpers.NormalizeNodeReferenceOnCreation`, and appeared visibly in
  vvvv gamma 7.2. Same-transaction `select` worked for created node aliases, and
  the editor snapshot reported `VL.Model.Node` selections (`OperationNode: +`,
  `ProcessNode: LFO`).
- `connect` now resolves pins on nodes created earlier in the same transaction
  via `<alias>:<PinName>`. Live verification created `process:Animation:LFO` and
  `operation:Math:+`, then connected `lfo_a:Phase -> plus_a:Input` in the same
  graph transaction.
- `nodeQuery` queries the active patch resolver and returns compact
  operation/process candidates with exact `addNode` symbols and input/output pin
  names/types. Verified examples: `LFO` returns `process:Animation:LFO`;
  `Skia Line` returns `process:Layers:Line`.
- The Skia-line benchmark prompt now requires bounded trial-and-error. A run
  attempted `LFO -> Vector2 joins -> Skia Line -> Renderer (OffScreen)` and
  exposed useful failures instead of stopping early: dry-run/apply resolver
  mismatch for Vector2 nodes and non-atomic partial `addNode` creation.
- `addNode` now resolves all planned node symbols up front for dry-run and apply,
  preventing the mixed valid+invalid `addNode` partial-creation case. Browser
  display variants such as `process:Animation:LFO (MinMax)`,
  `operation:Vector2:Vector (Join)`, and `process:Layers:Line` are resolved via
  the live scope and `ReferenceToSymbol.ToNodeReference(...)`, which keeps
  dry-run and apply aligned for the tested symbols.
- `setPin` can target transaction-created aliases with `<alias>:<PinName>`.
  Alias pins are validated against the resolved node definition before apply and
  applied after node/pad creation, before links/selects.
- The `skia-line` benchmark run (`20260622-155351-skia-line`) created some
  correct nodes but did not produce a functional patch. Root causes found in the
  raw results:
  - `connected` was populated before link commit, so a failed link transaction
    could look more successful than it was.
  - `Patch.GetOrAddLink(...)` must be committed by replacing the returned link's
    updated `ContainingPatch`; replacing the `Link` itself produced
    `This operation does not apply to an empty instance`.
  - The benchmark accepted a semantically wrong fallback
    `LFO:Phase -> Rectangle:Position` (`Float32 -> Vector2`) because created-node
    connect dry-runs did not validate pin direction/type.
- `connect` now validates transaction-created node endpoints against resolved
  pin definitions during dry-run/apply. Verified bad route:
  `process:Animation:LFO (MinMax)` `Phase` -> `process:Source:Rectangle`
  `Position` fails before mutation with `type mismatch Float32 -> Vector2`.
- `connect` now commits `link.ContainingPatch` sequentially and only appends to
  result `connected` after `MakeCurrent(...)` and best-effort post-commit link
  verification. Verified good route:
  `LFO (MinMax):Phase -> Vector2:Vector (Join):X -> Source.Rectangle:Position`
  returned `ok:true`, `partial:false`, two `connected` entries, and selected the
  three created nodes.
- `connect` now resolves existing unselected nodes/pads by full `UniqueId`
  through `DevEnvHost.Instance.CurrentSolution`, with selected-live-element
  lookup only as fallback. Verified by repairing older partial nodes with a
  connect-only transaction and no selection.
- `select` and `setBounds` now share the model element lookup path used by
  `connect`: selected live elements are used first, then
  `DevEnvHost.Instance.CurrentSolution`, `SessionNodes.CurrentSolution`, and the
  active patch model. This removes the earlier selected-only limitation in code;
  live Windows verification is still pending.
- Live mailbox latency probe on 2026-06-24 against vvvv/`AgentHost`:
  pre-cache repeated `nodeQuery LFO` p50 was `elapsedMs=1929`,
  `roundTripMs=1898`, `mailboxWaitMs=980`, `processingMs=912`. After adding the
  bounded `nodeQuery` cache, warm repeated `nodeQuery LFO` p50 dropped to
  `elapsedMs=134`, `roundTripMs=48`, `mailboxWaitMs=48`, `processingMs=0`.
  Cold `nodeQuery "Skia Line"` still showed `processingMs=2627` on the first
  request before cache hits dropped processing to `0`.
- `bench/run-bench.ps1` could not launch the nested Codex scenario in this
  desktop shell because the packaged WindowsApps `codex.exe` alias returned
  `Access is denied`; direct mailbox probes remain usable for bridge timing.

## Safety Rules

- Prefer observe-before-mutate.
- Prefer HDE-hosted `AgentHost` over patch-local bridge nodes.
- Keep structural graph mutation experimental until safe editor-command semantics
  are proven.
- Do not hand-edit `.vl` XML unless the change is small, conservative, and backed
  by file-format knowledge.
