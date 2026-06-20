# Deployment

Release-facing files for the future `VL.Agent` NuGet package.

This repo follows the shape of the vvvv library template in
`.template/VL.NewLibrary.Template`, but keeps publishing conservative while the
package is still an alpha prototype.

## Local Package Build

Stage the Windows tool bundle:

```shell
DOTNET=/Users/philipp/.dotnet/dotnet deployment/publish-tools.sh
```

Pack the draft NuGet package:

```shell
nuget pack deployment/VL.Agent.nuspec -OutputDirectory /tmp/agentic-vl-pack-check -NoDefaultExcludes
```

Expected warning:

```text
NU5100: The assembly 'package/tools/win-x64/*.dll' is not inside the 'lib' folder
```

Those DLLs are command-line app payloads for `vl-mcp`, `vl-map`, and `vl-probe`,
not assemblies intended to be referenced by a consuming vvvv project.

## GitHub Automation

- `.github/workflows/ci.yml` runs the macOS/Linux-safe tool smoke checks on pull
  requests and pushes to `main`.
- `.github/workflows/package.yml` stages `win-x64` tool binaries on Windows,
  packs the NuGet, verifies required package entries, and uploads the `.nupkg` as
  a workflow artifact.
- `.github/dependabot.yml` keeps GitHub Actions and NuGet package references
  visible for updates.

The workflows intentionally do not push to nuget.org yet. Enable publishing only
after the package ID, license, icon, versioning policy, bridge build mode, and
framework-dependent-vs-self-contained tool decision are settled.
