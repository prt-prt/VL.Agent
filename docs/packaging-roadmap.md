# Packaging status

`VL.Agent` is structured as a single vvvv-friendly NuGet containing:

- `VL.Agent.vl` and the `VL.Agent.HDE.vl` extension;
- the compiled vvvv bridge under `lib/net8.0-windows7.0/`;
- `vl-mcp`, `vl-map`, and `vl-probe` under `tools/win-x64/`;
- graph transaction schemas and examples;
- Help Browser metadata and user-facing documentation.

## Implemented

- The repository follows the official VL library template shape.
- The bridge builds reproducibly from NuGet-hosted `VL.HDE` dependencies instead
  of a machine-specific vvvv installation path.
- Source `.vl` documents reference the packaged `VL.Agent.dll`; they do not
  reference a `.csproj`.
- The Windows package workflow builds, packs, inspects, and uploads a `.nupkg`.
- Generated binaries are isolated under ignored `lib/` and `artifacts/` paths.

## Remaining release gates

1. Run the package workflow and inspect its first artifact.
2. Install that artifact into a clean vvvv gamma 7.2 package repository.
3. Verify `VL.Agent.HDE.vl`, `AgentHost`, editor snapshots, and one dry-run graph
   transaction on Windows.
4. Add real help patches for installation and MCP setup.
5. Decide whether the .NET 10 tools should remain framework-dependent.
6. Add a deliberate NuGet.org publishing step and secret after the package ID and
   first alpha version are confirmed.
