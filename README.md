# VL.Agent

Agentic development prototype for
[vvvv gamma](https://vvvv.org/): live HDE/editor context, static project indexing,
MCP tools, and narrow undo-integrated graph transactions.


## Getting started

- **CI alpha:** download the `VL.Agent-alpha` artifact from the package workflow and follow `setup/README.md`.
- **In vvvv:** install the package and open `VL.Agent.HDE.vl` to run the bridge.
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
- `setup/` — clean-install bootstrap and vvvv launcher for CI alpha artifacts.
- `tests/` — cross-platform tool smoke tests; Windows runtime checks belong here.

## License

MIT.
