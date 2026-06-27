# VL.Agent

Agentic development infrastructure for
[vvvv gamma](https://vvvv.org/): live HDE/editor context, static project indexing,
MCP tools, and narrow undo-integrated graph transactions.

The read-only analysis layer is the stable baseline. Live graph mutation remains
experimental and is intentionally routed through the HDE runtime.

## Repository layout

- `VL.Agent.vl` — package-facing root document.
- `VL.Agent.HDE.vl` — HDE extension hosting `AgentHost`.
- `src/VL.Agent/` — vvvv-loaded `net8.0-windows7.0` node library.
- `src/VL.Agent.Mcp/` — `vl-mcp` stdio MCP server.
- `src/VL.Agent.Map/` — `vl-map` static VL/C#/SDSL indexer.
- `src/VL.Agent.Probe/` — `vl-probe` metadata-only vvvv API dumper.
- `src/VL.Agent.Viewer/` — dependency-free static graph viewer.
- `schemas/` and `examples/` — graph-transaction contract and payloads.
- `help/` — vvvv Help Browser content.
- `deployment/` and `scripts/` — NuGet definition and release tooling.
- `tests/` — cross-platform tool smoke tests; Windows runtime checks belong here.

This follows the shape of
[`vvvv/VL.NewLibrary.Template`](https://github.com/vvvv/VL.NewLibrary.Template).

## Build

Build all standalone tools:

```powershell
dotnet build src\VL.Agent.Mcp\VL.Agent.Mcp.csproj -c Release
dotnet build src\VL.Agent.Probe\VL.Agent.Probe.csproj -c Release
```

Build the vvvv-loaded bridge. Its Release output is written to
`lib/net8.0-windows7.0/`:

```powershell
dotnet build src\VL.Agent\VL.Agent.csproj -c Release
```

Run the cross-platform smoke checks:

```shell
DOTNET=dotnet tests/smoke-tools.sh
```

## Live editor loop

1. Build or install `VL.Agent` in a vvvv package repository.
2. Install/open `VL.Agent.HDE.vl` as an editor extension.
3. Launch `vl-mcp` with the user project as its working directory.
4. Read compact context with `vvvv_context_query`.
5. Use `vvvv_set_pin_value` for narrow edits or
   `vvvv_apply_graph_transaction` for experimental batched changes.

The HDE bridge and MCP server exchange files under the user project:

```text
<project>/.agent/editor-state.json
<project>/.agent/requests/
<project>/.agent/results/
```

Point MCP clients at a built `vl-mcp.exe`, not `dotnet run`; build output on
stdout would corrupt the stdio JSON-RPC stream.

## Safety boundary

Direct `SessionNodes.Paste(...)` from `CommandProcessor.Update()` can race the
Skia patch editor render loop and raise `Collection was modified`. Keep paste
experimental. Structural edits should run through a proven HDE/editor command
context and use dry-run plus validation where available.

## Package status

The Windows package workflow builds the bridge and bundled tools, packs a
`VL.Agent` NuGet, verifies required entries, and uploads the `.nupkg` as a CI
artifact. Publishing to NuGet.org is intentionally not enabled yet.

See [packaging](deployment/README.md), the
[VL.Agent node reference](docs/reference/VL.Agent.md), and the
[graph transaction protocol](docs/graph-transaction-protocol.md).

## License

MIT.
