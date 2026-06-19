# vvvv-agent — Windows API validation findings

Date: 2026-06-19
Environment: Windows 11, vvvv gamma **7.2** (`C:\Program Files\vvvv\vvvv_gamma_7.2-win-x64`), .NET SDK 10.0.100.
Method: metadata-only reflection (`MetadataLoadContext`) over the shipped assemblies — **no vvvv code executed**. Reproducible via `tools/vl-probe`.

> This resolves the standing blocker in `SESSION_CONTEXT.md`: every "must investigate on Windows" item below is now answered against the real, installed API surface rather than guessed.

## TL;DR

The architecture's risky tiers are **largely de-risked**. vvvv exposes a clean, public, channel/observable-based API that covers reading editor/runtime state, navigating the editor, injecting patch model, and annotating patches. The immutable patch model already provides the typed mutation operations the notes proposed building from scratch.

Probe stats: 787 public types scanned across VL.Lang (420), VL.Core (358), VL.HDE (6), VL.Core.Commands (3); 341 flagged bridge-relevant.

## Confirmed APIs, mapped to the architecture tiers

### Tier 2 — Editor bridge: **CONFIRMED**

`VL.HDE.API` (the editor extension surface) exposes, as channels/observables:

| Need (from notes) | Confirmed member |
|---|---|
| Hovered node | `IChannel<object> HoveredElement` |
| Selection | `IChannel<Spread<object>> CurrentSelection`, `IObservable<SelectionNotification> Selection`, `GetSelectedElementsInActiveCanvas()` |
| Loaded documents | `IReadOnlyDictionary<string, Document> LoadedDocuments` / `LoadedAndCompiledDocuments` |
| Active canvas | `IChannel<ILiveCanvas> ActiveLiveCanvasStream` |
| Installed packages | `Spread<PackageDescription> InstalledVLPackages(bool)` |
| Key folders (cartography) | `SketchesFolder`, `UserPackagesFolder`, `UserDocumentFolder`, `AutoBackupFolder`, … |

### Tier 4 — Runtime + compile telemetry: **CONFIRMED**

- `VL.HDE.API.Messages : IObservable<ImmutableHashSet<Message>>`
- `LatestMessagesFromAllRuntimes`, `LatestMessagesFromCompiler` — runtime exceptions and compile errors as observables.
- `ILiveElement` exposes per-element `IEnumerable<Message> Messages` and `IObservable<object> DataStream` (live pin/data values).

### Session control / navigation: **CONFIRMED** (`VL.Lang.PublicAPI.IDevSession`)

- `CurrentSolution : ISolution`, `Current : IDevSession` — workspace entry point.
- `OpenDocument(s)` / `CloseDocument` / `CloseDocumentOfNode` — document lifecycle.
- `ShowPatchOfNode(...)` — drive the editor to a node (agent "show me X").
- `RegisterNode(IVLObject)`, `Commands : ICommandList`.
- `SessionNodes` is the node-exposed wrapper of the same surface (these are reachable as VL nodes).

### Bidirectional annotation: **CONFIRMED**

`SessionNodes.AddMessage(UniqueId elementId, string, MessageSeverity)`, `AddPersistentMessage(Message)`, `ToggleMessage(...)` — the agent can push diagnostics/annotations **onto specific patch elements** in the live editor.

### Tier 3 — Patch mutation: **MUCH lower risk than assumed**

Two independent paths exist, no hand-rolled `.vl` XML generation required:

1. **Editor-mediated, undo-safe:** `IDevSession.Paste(string modelSnippet, PointF location)` injects a serialized model snippet into the active patch at a location — the editor owns undo/redo. This is the conservative, reversible mutation path the notes wanted.
2. **Programmatic immutable model:** `VL.Model.Patch` already exposes the proposed operation IR as fluent builders:
   - `AddPad`, `AddSlot`, `AddProxy`, `AddPinAndProxy`, `GetOrAddLink(...)`, `GetOrAddFeedbackLink`, `GetOrAddReferenceLink`, `AddSubPatch`, `Add/RemoveParticipatingElement(s)`, `With*`.
   - `VL.Model.Node`: `AddPin`, `WithPins`, `WithPatches`, `WithBounds`, `WithPosition`, … plus a deep read model (`IsProcessDefinition`, `InnerCanvas`, `NodeReference`, `PatchTopology`, etc.).
   - `VL.Model.Pin`: `WithValue`, `WithName`, `WithVisibility`, `CanBeSink/CanBeSource`, `IsConnected/IsInput/IsOutput`, …
   - `VL.Model.Document`: `DeserializeFrom(Async)`, `SerializeTo`, `SaveAsync`, `ReloadAsync`, `WithPatch`, `GetOrAdd*Dependency` (file / nuget / platform / node-factory).

So the "internal IR: AddNode/AddPad/SetPin/Connect/Disconnect/AddDependency" module is **already the public model API** — the agent should target it rather than reinvent it.

3. **Live, undo-integrated solution edits** — `VL.Lang.PublicAPI.ISolution` (reached via `IDevSession.Current.CurrentSolution`) is the safest write primitive of all:
   - `ISolution SetPinValue(nodeId, pin, value)` — immutable, returns the next solution.
   - `PinGroupBuilder ModifyPinGroup(nodeId, pinGroup, isInput)` — add/remove pins on a group.
   - `void Confirm(SolutionUpdateKind)` — **commits the accumulated edit through the editor's own undo system.**

   Pattern: `var s = session.CurrentSolution; s.SetPinValue(id, "Value", 42).Confirm(SolutionUpdateKind.X);`. This is observe → propose → `Confirm` with native undo — exactly the transactional model the architecture wanted, already public.

### Access root (how a node/extension reaches all of the above)

- `VL.Core.AppHost.Current` / `.Global` / `.CurrentOrGlobal` → `.Services : ServiceRegistry` → `GetService(type)`. `ServiceRegistry` also has static `Current`/`Global`.
- `VL.Lang.PublicAPI.IDevSession.Current` → `CurrentSolution`, `Commands`, document ops.
- From inside vvvv these are also exposed as **nodes** (`SessionNodes`, the `VL.HDE.API` node), so a bridge can take them as **input pins** instead of resolving them itself — sidestepping any reachability question.

### Live element model (deep introspection of selected/hovered)

`VL.Lang.PublicAPI` exposes `ILiveElement`, `ILiveNodeApplication`, `ILiveLink`, `ILiveProperty`, `ILiveFragment`, `ILiveDataHub`, plus info views `INodeInfo`/`ILinkInfo`/`IElementInfo`/`IDataHubInfo`. `ILiveElement.DataStream : IObservable<object>` gives live per-element values. So selection/hover yields not just IDs but live, observable runtime state.

### Command/transaction infra (`VL.Core.Commands`)

`Command.Create(Action[, isEnabled])`, `CommandList.Create/Combine/Reserve/TryExecute`, `CommandBinding{Keys,ICommand}` — for binding agent actions to the editor command system + shortcuts.

## What still needs an in-vvvv runtime test (not answerable by metadata)

1. **Reachability**: confirm an `.HDE.vl` extension (or C# node) can actually obtain the `VL.HDE.API` instance and `IDevSession.Current` at runtime, and that the channels tick. Metadata proves the surface exists, not the wiring.
2. **`Paste` snippet format**: what exact serialized `modelSnippet` string `IDevSession.Paste` accepts (presumably the clipboard `.vl`-fragment XML). Needs an empirical capture.
3. **Mutation persistence path**: whether editing `Document`/`Patch` programmatically and `SaveAsync()` round-trips cleanly and shows up live, vs. only the `Paste`/editor path being safe.
4. **VL.TestFramework** assembly location/version on this install (not at install root; likely a separate pack) — needed for the test-harness module.

## Recommended next steps (revised)

1. **In-vvvv reachability spike** — minimal `.HDE.vl` (or C# node) that grabs `VL.HDE.API` + `IDevSession.Current`, logs `HoveredElement`/`CurrentSelection`/`Messages`, and calls `AddMessage` on the hovered element. Proves the bridge end-to-end. (Closes open question #1.)
2. **`vl-map`** static indexer — now informed by the real `VL.Model.Document`/`Patch` shape; can later cross-check against `LoadedDocuments` from the live API.
3. **`Paste` snippet capture** — copy a node in vvvv, inspect the clipboard payload, to learn the `modelSnippet` grammar for safe agent-driven insertion.
4. Validate all of the above against the **`testbed/dodecahedron-vl`** project (22 `.vl` files, `Spatial-AV.vl` entry point).

## Reproducing

```
cd tools/vl-probe
dotnet run -c Release           # defaults to gamma 7.2
# options: --install <dir> --targets VL.Lang.dll,VL.HDE.dll,... --out <dir>
```
Outputs `api-full.md` (every public type) and `api-bridge-relevant.md` (keyword-filtered) under `bin/.../probe-output/`.
