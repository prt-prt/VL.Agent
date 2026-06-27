# VL.Agent agent guide

This repository is the publishable vvvv library and tool distribution for the
VL.Agent system. Keep product code, package assets, and implementation tests here;
framework-neutral agent benchmarks live in their own repository.

## Ground truth

- Use the local `vvvv-*` skills for vvvv concepts and file-format details.
- vvvv-loaded code targets `net8.0-windows7.0`; standalone tools target `net10.0`.
- `VL.Agent.vl`, `VL.Agent.HDE.vl`, and `deployment/VL.Agent.nuspec` are package
  entry points and must stay aligned.
- Release builds of `src/VL.Agent/VL.Agent.csproj` write to
  `lib/net8.0-windows7.0/`.

## Components

- `src/VL.Agent` — HDE/editor bridge nodes.
- `src/VL.Agent.Mcp` — MCP stdio server.
- `src/VL.Agent.Map` — static project indexer.
- `src/VL.Agent.Probe` — metadata-only public API dumper.
- `src/VL.Agent.Viewer` — static project-graph viewer.

Point MCP clients at the built `vl-mcp.exe`, not `dotnet run`.

## Runtime convention

```text
<project>/.agent/editor-state.json
<project>/.agent/requests/
<project>/.agent/results/
```

`EditorWatcher` writes live editor state. `CommandProcessor` supports narrow pin
updates, graph transactions, and an explicitly experimental paste request.

## Safety

Do not casually expand or re-enable direct paste. Mutating the graph from
`CommandProcessor.Update()` has raced the Skia editor render loop and caused
`InvalidOperationException: Collection was modified`.

1. Observe before mutating.
2. Prefer static indexes and editor snapshots for analysis.
3. Use `CurrentSolution.*.Confirm(...)` for narrow undo-integrated edits.
4. Do not hand-edit `.vl` XML unless the change is conservative and backed by
   file-format knowledge.
5. Update package paths, CI, and docs together when the repository shape changes.
