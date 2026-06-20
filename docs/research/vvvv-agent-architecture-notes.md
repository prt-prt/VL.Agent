# vvvv-agent architecture exploration notes

Date: 2026-06-17

## Scope

Goal: propose feasible architectures for an agentic assistant for vvvv gamma aimed at expert patchers and agentic-coding users. Primary focus is complex project patching and behavioural debugging. Current session is on macOS, so runtime/editor validation is deferred to Windows.

## Sources used

- vvvv skill corpus: fundamentals, file format, editor extensions, custom nodes, node libraries, channels, troubleshooting, testing.

## Relevant vvvv facts

### vvvv gamma development model

- `.vl` files are XML documents and are version-controlable.
- vvvv is live: patches run while edited; C# source project references are compiled via Roslyn inside vvvv and hot-reloaded.
- C# node libraries become nodes through public API + `[assembly: ImportAsIs]`, `[ImportNamespace]`, or `[ImportType]`.
- `[ProcessNode]` governs lifecycle/state, not visibility.
- Channels (`IChannelHub`, public channels, `[CanBePublished]`) provide app-wide named reactive values.
- `.HDE.vl` editor extensions can register commands and windows; `VL.Lang` Session nodes can access hovered/selected nodes and pins.
- VL.TestFramework can load/compile/test `.vl` documents and execute entry points.

## Architectural hypothesis

A viable vvvv-agent should not begin by directly generating arbitrary `.vl` XML. It should be layered:

1. **Read-only intelligence first**: project index, graph summaries, dependency map, error/log analysis, C# and shader review.
2. **Safe file-level edits**: C# node libraries, `.csproj`, `.sdsl`, docs/help patches; `.vl` XML only through conservative transformations.
3. **Editor bridge**: an `.HDE.vl` extension or C# sidecar exposes selected/hovered patch context, compile diagnostics, node/pin metadata, screenshots/thumbnails if feasible.
4. **Runtime telemetry bridge**: subscribe to frame/runtime errors, public channels, debug probes, and test patches.
5. **Transactional patch editing**: propose and apply command-like operations (add node, set pin value, link pins, create pad) with validation and undo.

## Proposed system shape

### External agent core

A CLI/TUI similar to Claude Code/pi:

- reads project files,
- maintains an index of `.vl`, `.cs`, `.sdsl`, `.csproj`, `.nuspec`, help/test patches,
- invokes tools (`dotnet build`, VL.TestFramework on Windows, vvvv launcher, log parser),
- edits source with version-control discipline,
- talks to vvvv via a bridge.

### vvvv bridge

Possible bridge forms:

- **MVP bridge: file/log/test based**
  - no editor extension required,
  - launch vvvv/test framework, parse logs, inspect files.
- **HDE extension bridge**
  - `.HDE.vl` extension publishes selected/hovered patch context and receives commands,
  - communicates through local HTTP/WebSocket/NamedPipe/OSC/Redis/channel files.
- **C# service bridge**
  - node library registers services via `AssemblyInitializer`, exposes project/runtime introspection and agent operations.
- **Channel bridge**
  - uses public channels to expose debug state, model state, and agent requests/responses inside a running patch.

## Candidate modules

1. **Project cartographer**
   - Builds map of documents, dependencies, definitions, patches, C# nodes, packages.
   - Output: structured JSON + human-readable summaries.

2. **Patch graph analyzer**
   - Parses `.vl` XML to identify nodes, pins, links, pads, dependencies, regions.
   - Detects broken references, duplicate IDs, suspicious links, missing dependencies.
   - Later: enriched with live metadata from `VL.Lang` Session.

3. **C# node copilot**
   - Generates/reviews ProcessNode classes, library setup, imports, pin attributes, zero-allocation Update loops, disposal.
   - Very feasible now.

4. **Debugging assistant**
   - Correlates compile errors, runtime exceptions, vvvv logs, selected nodes, channel values, recent edits.
   - Recommends minimal experiments/test patches.

5. **Test harness generator**
   - Creates VL.TestFramework NUnit project and `.vl` test patches.
   - Runs compile/load/entrypoint checks on Windows.

6. **Patch mutation engine**
   - Internal IR: `AddNode`, `AddPad`, `SetPin`, `Connect`, `Disconnect`, `Group`, `AddDependency`, etc.
   - Validates IDs, namespaces, fragment references, link direction.
   - Applies XML edits or sends commands to editor bridge.

7. **Runtime probe library**
   - vvvv nodes to publish debug probes/channels: frame metrics, selected values, exception breadcrumbs, custom watchpoints.

## Feasibility tiers

### Tier 1 — high confidence, no deep editor API required

- C# node/library generation and review.
- `.csproj`/NuGet/package setup.
- Static `.vl` parser and validator.
- Project cartography.
- vvvv log/error parser.
- VL.TestFramework harness generation.

### Tier 2 — feasible with Windows/vvvv validation

- HDE extension that exports selected/hovered node context.
- Agent command palette in vvvv.
- Runtime telemetry via public channels.
- Compile/test loop around vvvv launch + logs.

### Tier 3 — research/risk

- Reliable arbitrary patch generation/modification.
- Full semantic node browser indexing outside vvvv.
- Agent-driven live patch editing with robust undo/redo.
- Visual/screenshot-aware patch refactoring.

## Recommended first prototypes

1. **`vl-map`**: static project indexer for `.vl`, `.cs`, `.sdsl`, `.csproj`.
2. **`vl-lint`**: file-format and C# node-pattern linter.
3. **`vvvv-agent-bridge.HDE.vl` concept**: selected-node/context exporter + command receiver.
4. **`VL.Agent.Probes` package**: channel-based runtime probes and debug publishing.
5. **`vl-test-init`**: generate VL.TestFramework project and smoke tests.

## Key design principles

- Prefer observation before mutation.
- Keep patch edits transactional, inspectable, and reversible.
- Use vvvv-native concepts: HDE extensions, Session API, public channels, VL.TestFramework.
- Keep external agent modular; vvvv bridge should be optional.
- Optimize for expert workflows: explain graph behaviour, suggest probes, create test harnesses, and patch safely.
