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

1. Cloned public `vvvv-sdk` into `vvvv-sdk/`.
   - It appears primarily historical vvvv45/public SDK, not current gamma source.
   - Useful files observed:
     - `vvvv45/src/integration/VVVV.VLIntegration/src/RuntimeHost.cs`
     - `vvvv45/src/integration/VVVV.VLIntegration/src/NodeFactory.cs`
     - `common/src/core/Core/Commands/Command.cs`
2. Created `research/vvvv-agent-architecture-notes.md`.
3. Created `deck/vvvv-agent-pitchdeck.html`.
4. Created `research/vvvv-ecosystem-deep-dive.html`.

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

## Key missing information to investigate on Windows

- Exact current `VL.Lang` Session API surface.
- Whether editor-safe patch mutations can be performed from `.HDE.vl` or C# with undo integration.
- How much live pin/node/runtime state is introspectable.
- Exact current gamma host/compiler/session APIs, if accessible.
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
