# tools

Standalone .NET 10 command-line tools for inspecting vvvv gamma projects and
exposing that information to MCP clients.

The tools are static/metadata-only unless explicitly noted. They do not execute a
vvvv project.

## `vl-probe`

Metadata-only API surface dumper for a vvvv gamma installation. It uses
`MetadataLoadContext` to inspect assemblies and was used to produce
`research/windows-api-validation-findings.md`.

```powershell
cd tools\vl-probe
dotnet run -c Release
dotnet run -c Release -- --install "C:\Program Files\vvvv\vvvv_gamma_7.2-win-x64" --targets VL.Lang.dll,VL.HDE.dll,VL.Core.dll --out output
```

Outputs:

- `api-full.md`
- `api-bridge-relevant.md`

## `vl-map`

Static project cartographer for vvvv projects. It walks `.vl`, `.cs`, `.csproj`,
and `.sdsl` files, parses `.vl` XML, and emits a structured index plus a summary.

```powershell
cd tools\vl-map
dotnet run -c Release -- --project "C:\path\to\project" --out vl-map.json
```

The index includes:

- document IDs, language versions, and format versions
- definitions and dependencies
- node/pad/link/canvas/pin counts
- document dependency graph
- package version drift
- missing document dependencies
- duplicate element IDs
- parse failures

The indexing logic is available as `VlMap.Indexer.Build`.

## `vl-mcp`

MCP stdio server for agent clients.

Tools exposed:

- `vvvv_index_project` - static project index via `vl-map`
- `vvvv_editor_state` - live editor snapshot written by `EditorWatcher`
- `vvvv_set_pin_value` - narrow undo-integrated pin edit through `CommandProcessor`

Build and point your MCP client at the executable:

```powershell
dotnet build tools\vl-mcp\vl-mcp.csproj -c Release
```

```text
tools\vl-mcp\bin\Release\net10.0\vl-mcp.exe
```

Do not use `dotnet run` as an MCP command, because build output on stdout corrupts
the JSON-RPC stream.

Node insertion/paste is not exposed. The current paste experiment can mutate the
editor graph while the patch editor is rendering and destabilize the editor view.
