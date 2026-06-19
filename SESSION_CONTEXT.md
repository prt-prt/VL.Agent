# Session context for continuation on Windows

## Project intent

This repo is for exploring AI-assisted / agentic development in the vvvv gamma ecosystem. The target user is an expert vvvv user who is also familiar with agentic coding tools such as Claude Code, Codex, or pi.

The desired tool is currently imagined as a text-first development assistant similar to coding agents, but adapted to vvvv's visual/live ecosystem. It should help with complex patching projects and debugging behaviour, not just generate code.

## User requirements captured so far

- Produce proposed architectures and eventually prototypes for a `vvvv-agent`.
- Output research and reports as nice HTML artifacts.
- Focus on expert users / vvvv expert community.
- Keep the design modular and incrementally integrable.
- Feasibility within the actual vvvv ecosystem is the most important requirement.
- Current macOS session cannot run vvvv; future continuation will happen on Windows.

## Work completed

Research phase (macOS):
1. Cloned public `vvvv-sdk` (historical vvvv45/public SDK, not current gamma source).
2. `research/vvvv-agent-architecture-notes.md`, `deck/vvvv-agent-pitchdeck.html`,
   `research/vvvv-ecosystem-deep-dive.html`.

Build phase (Windows, 2026-06-19) — **first working version of the system**:
3. `tools/vl-probe` — metadata-only API dumper; validated the gamma 7.2 surface
   (→ `research/windows-api-validation-findings.md`). Verified.
4. `tools/vl-map` — static project cartographer (definitions, deps, graph, version
   drift, dangling refs, dup IDs). Verified against `testbed/dodecahedron-vl`.
5. `bridge/VL.Agent` — in-vvvv node library; `WriteEditorSnapshot(path)` exports live
   editor state to JSON. **Runtime-verified in vvvv (2026-06-19)**: selection updates
   live. The full architecture is now proven end-to-end on real hardware.
6. `testbed/dodecahedron-vl` — real project as validation corpus (origin detached,
   gitignored). `CLAUDE.md` — agent guide for the repo.

### Status update
- Read path live-verified end-to-end through Claude Code (MCP `vvvv_editor_state`):
  selection resolves to structured `ElementId`/`UniqueId`/`Kind`/`Symbol`.
- Write path built: MCP `vvvv_set_pin_value` → `.agent/requests/` → `CommandProcessor`
  node → `SessionNodes.CurrentSolution.SetPinValue(uid,pin,value).Confirm(Default)` →
  `.agent/results/`. Both ends compile + the request/result round-trip is verified
  headlessly. **In-vvvv mutation not yet live-tested** (needs CommandProcessor node in a
  patch + a reconnect of the MCP server so the new tool loads).

### Immediate next steps
- Live-test `vvvv_set_pin_value` against a real node pin in vvvv; iterate on value type
  coercion (Int32/Float32/…) and whether IOBox/pad values need a different path.
- Then: `SessionNodes.AddMessage(...)` annotations; surface live pin values via
  `ILiveDataHub`/`DataStream`; duplicate-document-Id detection in vl-map.

## Current architectural thesis

A viable vvvv-agent should be layered:

1. Read-only intelligence first:
   - project index,
   - graph summaries,
   - dependency map,
   - error/log analysis,
   - C# and shader review.
2. Safe file-level edits:
   - C# node libraries,
   - `.csproj`, `.sdsl`, docs/help patches,
   - `.vl` XML only through conservative transformations.
3. Editor bridge:
   - `.HDE.vl` extension or C# sidecar exposes selected/hovered patch context, diagnostics, node/pin metadata.
4. Runtime telemetry bridge:
   - frame/runtime errors,
   - public channels,
   - debug probes,
   - test patches.
5. Transactional patch editing:
   - typed operation IR such as `AddNode`, `AddPad`, `SetPin`, `Connect`, `Disconnect`, `AddDependency`, etc.
   - validate before applying;
   - support undo/rollback.

## High-confidence prototype candidates

- `vl-map`: static project indexer for `.vl`, `.cs`, `.sdsl`, `.csproj`, `.nuspec`, help/test patches.
- `vl-lint`: static validator for `.vl` file format rules and C# node/library conventions.
- `vvvv-agent-bridge.HDE.vl`: editor extension concept exposing selected/hovered node context.
- `VL.Agent.Probes`: channel-based runtime probes/debug publishing nodes.
- `vl-test-init`: generate VL.TestFramework scaffolding and smoke tests.

## Windows API validation (2026-06-19) — RESOLVED via `tools/vl-probe`

Validated against installed gamma 7.2 by metadata reflection (no vvvv code run). Full writeup: `research/windows-api-validation-findings.md`. Key confirmations:

- **Session API**: `VL.Lang.PublicAPI.IDevSession` (`Current`, `CurrentSolution`, `OpenDocument`, `ShowPatchOfNode`, `Paste(modelSnippet, location)`) + `SessionNodes` wrapper.
- **Editor state**: `VL.HDE.API` exposes `HoveredElement`, `CurrentSelection`/`Selection`, `LoadedDocuments`, `ActiveLiveCanvasStream`, `InstalledVLPackages`, key folders — all as channels/observables.
- **Telemetry**: `VL.HDE.API.Messages` / `LatestMessagesFromAllRuntimes` / `LatestMessagesFromCompiler`; `ILiveElement.DataStream` + per-element `Messages`.
- **Live introspection depth**: deep — `VL.Model.Document/Patch/Node/Pin` fully readable.
- **Patch mutation**: `VL.Model.Patch/Node/Pin` already provide the proposed IR as fluent builders (`AddPad`/`AddPin`/`GetOrAddLink`/`With*`); editor-mediated undo-safe path = `IDevSession.Paste`. Agent should target these, not hand-written `.vl` XML.
- **Annotation back into editor**: `SessionNodes.AddMessage`/`AddPersistentMessage` on a `UniqueId`.

## Still open (needs RUNTIME test, not metadata)

- Reachability of `VL.HDE.API` + `IDevSession.Current` from an `.HDE.vl`/C# node, and whether channels tick.
- Exact `IDevSession.Paste` `modelSnippet` string format.
- Whether programmatic `Document`/`Patch` edits + `SaveAsync` round-trip live (vs. only the `Paste` path being safe).
- VL.TestFramework pack location/version on this install.
- Package resolver/cache/editable package behaviour in practice.
- VL.Stride/Fuse internal runtime and shader graph details.

## Relevant vvvv concepts already summarized in artifacts

- `.vl` XML document structure: Document, Patch, Canvas, Node, Pin, Pad, Link, ProcessDefinition, Fragment, Slot.
- NodeReference / Choice system.
- Process node lifecycle: Create / Update / Dispose.
- C# import model: public API + `[assembly: ImportAsIs]` / `[ImportNamespace]` / `[ImportType]`.
- `[ProcessNode]` controls lifecycle, not visibility.
- Source project reference vs binary DLL/NuGet reference workflows.
- Public channels via `IChannelHub`, `[CanBePublished]`, hierarchical paths, subscriptions.
- `.HDE.vl` editor extensions and `VL.HDE` / `VL.Lang` dependencies.
- VL.TestFramework with NUnit.
- SDSL shader suffix node generation (`_TextureFX`, `_DrawFX`, `_ComputeFX`, `_ShaderFX`).

## Repo state note

`vvvv-sdk/` is intended to be tracked as a Git submodule pointing at `https://github.com/vvvv/vvvv-sdk` commit `1ac285b608bf7631878b7444bd57edcdc7ea3e9a`, not vendored into this repo.
