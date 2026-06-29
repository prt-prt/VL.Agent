# VL.Agent

Agentic development prototype for
[vvvv gamma](https://vvvv.org/): live HDE/editor context, static project indexing,
MCP tools, and narrow undo-integrated graph transactions.


## Getting started

- **In vvvv:** install the package (clone repo for now, Nuget Distribution is still WIP) and reference `VL.Agent.HDE.vl` to run the bridge
- **MCP server:** build and connect `vl-mcp`. See `src/VL.Agent.Mcp/README.md`.

## Repository layout

- `VL.Agent.vl` — package-facing root document.
- `VL.Agent.HDE.vl`.
- `src/VL.Agent/` — vvvv-facing `net8.0-windows7.0` node library.
- `src/VL.Agent.Mcp/` — `vl-mcp` stdio MCP server.
- `src/VL.Agent.Map/` — `vl-map` static VL/C#/SDSL indexer.
- `src/VL.Agent.Probe/` — `vl-probe` metadata-only vvvv API dumper.
- `src/VL.Agent.Viewer/` — dependency-free static graph viewer.
- `schemas/` and `examples/` — graph-transaction contract and payloads.
- `help/` — vvvv Help Browser content.
- `deployment/` and `scripts/` — NuGet definition and release tooling.
- `tests/` — cross-platform tool smoke tests; Windows runtime checks belong here.

## License

MIT.
