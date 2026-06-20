# Release Checklist

Public-release checklist for the future `VL.Agent` NuGet package.

## Before First Public Alpha

- [ ] Decide final package ID, license, icon, author metadata, and repository URL.
- [ ] Decide whether bundled `win-x64` tools are framework-dependent or
      self-contained.
- [ ] Decide whether `VL.Agent` ships as source project files, compiled
      `lib/net8.0-windows` binaries, or both.
- [ ] Validate `VL.Agent/VL.Agent.csproj` against vvvv gamma 7.2 on Windows.
- [ ] Run `deployment/publish-tools.sh` and inspect `package/tools/win-x64/`.
- [ ] Run the `package_nuget_artifact` GitHub workflow and inspect the uploaded
      `.nupkg`.
- [ ] Install the package into a clean vvvv environment.
- [ ] Confirm `VL.Agent.HDE.vl` opens as the default HDE/editor host.
- [ ] Confirm bundled `vl-mcp.exe` can be used as the MCP command.
- [ ] Confirm Help Browser metadata and first help patches are discoverable.

## Manual Publish Gate

Do not push to nuget.org until the package artifact has been tested on a clean
Windows machine with vvvv gamma installed.

Once publishing is enabled, prefer a tag-driven workflow:

1. Update `deployment/VL.Agent.nuspec` version.
2. Run local smoke and package checks.
3. Create a `vX.Y.Z-alpha.N` tag.
4. Let GitHub Actions build the package artifact.
5. Push the verified `.nupkg` to nuget.org with `NUGET_KEY`.

## Known Alpha Warnings

`nuget pack` currently emits NU5100 warnings for CLI payload DLLs under
`package/tools/win-x64/`. This is expected while the tools ship as package assets
instead of reference assemblies.
