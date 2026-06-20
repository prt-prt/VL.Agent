# Packaging Roadmap

`agentic-vl` should become distributable as a single vvvv-friendly NuGet package
that contains the full workflow, not just a node library.

## Target

The package should include:

- HDE/editor extension entry point (`VL.Agent.HDE.vl`).
- vvvv-loaded bridge (`VL.Agent`).
- MCP server (`vl-mcp`).
- Standalone CLIs (`vl-map`, `vl-probe`).
- Graph transaction schemas and examples.
- Help Browser metadata and help patches.
- User-facing setup docs.

NuGet is the distribution container. The HDE extension should become the primary
user entry point for discovering paths, checking tool availability, and guiding
MCP client setup.

## Current Package Skeleton

- `deployment/VL.Agent.nuspec` defines the first draft package contents.
- `deployment/publish-tools.sh` stages published CLI/MCP binaries under
  `package/tools/<runtime-id>/`.
- `deployment/README.md` documents the local package build and GitHub automation.
- `help/help.xml` reserves the standard vvvv help-pack location.
- `package/tools/` reserves a place for published CLI and MCP binaries.
- `.github/workflows/package.yml` builds and uploads draft `.nupkg` artifacts
  without publishing them to nuget.org.

The skeleton is not publish-ready yet.

Validated on macOS on 2026-06-20:

```shell
nuget pack deployment/VL.Agent.nuspec -OutputDirectory /tmp/agentic-vl-pack-check -NoDefaultExcludes
```

The resulting draft package includes the HDE `.vl`, `VL.Agent` source project,
help metadata, package placeholders, graph transaction schemas/examples, and
selected user-facing docs.

Validated on macOS on 2026-06-21:

```shell
DOTNET=/Users/philipp/.dotnet/dotnet deployment/publish-tools.sh
```

The script publishes `vl-mcp`, `vl-map`, and `vl-probe` for `win-x64` into
`package/tools/win-x64/`, matching the package skeleton's expected tool layout.

Packing after staging succeeds, with expected NU5100 warnings for tool payload
DLLs under `package/tools/win-x64/`. Those DLLs are command-line app payloads, not
reference assemblies that should be imported from `lib/`.

## Open Decisions

- Whether `VL.Agent` should ship as compiled `lib/net8.0` binaries, source
  project files, or both during the early community phase.
- Whether tool binaries should be framework-dependent or self-contained.
- Which runtime identifiers to ship first. `win-x64` is the likely baseline
  because vvvv gamma and HDE extension use are Windows-centered.
- How much MCP client configuration the HDE extension can automate.
- Which research docs should stay dev-only instead of shipping in the package.
- Final package ID, license, icon, repository URL, and author metadata.
- Whether GitHub Actions should publish to nuget.org on release tags, or whether
  the first alpha packages should be pushed manually after Windows validation.

## Near-Term Steps

1. Add a package-facing `VL.Agent.vl` if the HDE extension should not be the root
   package document.
2. Add real help patches for install, editor-state inspection, set-pin edits,
   and MCP setup.
3. Decide whether the staged `win-x64` tool bundle should stay
   framework-dependent or become self-contained for first community testing.
4. Decide the bridge build mode and update `VL.Agent.csproj` or packaging files
   accordingly.
5. Validate a local `nuget pack deployment/VL.Agent.nuspec` run on Windows.
6. Add a Windows CI path once the local package layout is proven.
