# agentic-vl

Prototype tooling for agentic development workflows in
[vvvv gamma](https://vvvv.org/): static project indexing, live HDE/editor state,
and narrow undo-integrated edits.

The stable layer is read-only analysis. Live mutation is experimental and should
stay small, transactional, and hosted from the HDE/editor runtime where possible.

## Current Shape

- `tools/vl-map/` - static indexer for `.vl`, `.cs`, `.csproj`, and `.sdsl`.
- `tools/vl-mcp/` - MCP stdio adapter for indexing, editor snapshots, pin edits,
  and experimental graph transactions.
- `tools/vl-probe/` - metadata-only vvvv gamma API dumper.
- `VL.Agent/` - C# node library loaded into vvvv.
- `VL.Agent.HDE.vl` - preferred HDE/editor-extension host for `AgentHost`.
- `deployment/` - draft NuGet package definition for the future community package.
- `.github/workflows/` - CI and draft package-artifact automation.
- `help/` - placeholder vvvv Help Browser metadata and future help patches.
- `package/` - staging area for distribution-only assets such as bundled tool binaries.
- `schemas/graph-transaction.schema.json` - first graph transaction contract.
- `examples/graph-transactions/` - copyable transaction payloads.
- `bench/` - repeatable, timed VL.Agent speed benchmark (Codex-driven scenarios).
- `docs/` - concise project context, Windows test checklist, reference notes,
  research artifacts, and the agent cockpit proposal.

## Verified Baseline

Validated on Windows against vvvv gamma 7.2 on 2026-06-19:

- `vl-probe` reflected vvvv assemblies without executing vvvv code.
- `vl-map` indexed real `.vl` projects.
- `EditorWatcher` wrote live editor snapshots with selection and compiler messages.
- `AgentHost` ran from an `.HDE.vl` editor extension.
- `vvvv_set_pin_value` used `CurrentSolution.SetPinValue(...).Confirm(...)`.

Validated on macOS on 2026-06-20:

- .NET SDK 10.0.301 installed under `/Users/philipp/.dotnet`.
- `tools/vl-mcp` builds.
- `tools/smoke-test.sh` passes.

## Quick Start

Build standalone tools:

```powershell
dotnet build tools\vl-probe\vl-probe.csproj -c Release
dotnet build tools\vl-map\vl-map.csproj -c Release
dotnet build tools\vl-mcp\vl-mcp.csproj -c Release
```

Build the vvvv-loaded bridge on Windows:

```powershell
dotnet build VL.Agent\VL.Agent.csproj
```

If vvvv is installed elsewhere:

```powershell
dotnet build VL.Agent\VL.Agent.csproj -p:VvvvInstall="C:\path\to\vvvv_gamma_7.2-win-x64"
```

Run the macOS-safe smoke check:

```shell
DOTNET=/Users/philipp/.dotnet/dotnet tools/smoke-test.sh
```

Stage the Windows tool bundle for the draft NuGet layout:

```shell
DOTNET=/Users/philipp/.dotnet/dotnet deployment/publish-tools.sh
```

## Live Editor Loop

1. Reference `VL.Agent/VL.Agent.csproj` from a vvvv project.
2. Open/install `VL.Agent.HDE.vl` as an editor extension.
3. Launch the MCP client from the user project directory.
4. Read `vvvv_editor_state`.
5. Use `vvvv_set_pin_value` for narrow pin edits.
6. Use `vvvv_apply_graph_transaction` for experimental batched `setPin` and
   validation transactions.

Default shared location:

```text
<project>/.agent/editor-state.json
<project>/.agent/requests/
<project>/.agent/results/
```

## Important Safety Finding

Direct `SessionNodes.Paste(...)` from `CommandProcessor.Update()` can race the
Skia patch editor render loop and trigger:

```text
InvalidOperationException: Collection was modified; enumeration operation may not execute.
```

Future structural graph mutation should run through a proven HDE/editor command
context or another safe undo-integrated API boundary.

## Useful Docs

- [Project context](docs/SESSION_CONTEXT.md)
- [Windows testing checklist](docs/WINDOWS_TESTING.md)
- [Packaging roadmap](docs/packaging-roadmap.md)
- [Release checklist](docs/release-checklist.md)
- [VL.Agent reference](docs/reference/VL.Agent.md)
- [Graph transaction protocol](docs/graph-transaction-protocol.md)
- [Agent cockpit architecture](docs/agent-cockpit-architecture.html)
- [Windows API validation](docs/research/windows-api-validation-findings.md)
