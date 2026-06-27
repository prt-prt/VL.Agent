# VL.Agent

Agentic development infrastructure for
[vvvv gamma](https://vvvv.org/): live HDE/editor context, static project indexing,
MCP tools, and narrow undo-integrated graph transactions.

The read-only analysis layer is the stable baseline. Live graph mutation remains
experimental and is intentionally routed through the HDE runtime.

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
