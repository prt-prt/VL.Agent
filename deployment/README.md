# Packaging

The package follows the official `vvvv/VL.NewLibrary.Template` layout. Release
builds produce the vvvv-loaded assembly under `lib/`; standalone executables are
staged under `artifacts/tools/<runtime-id>/` and packed as `tools/<runtime-id>/`.

## Local package build

On Windows:

```powershell
dotnet build src\VL.Agent\VL.Agent.csproj -c Release
bash scripts/publish-tools.sh
Invoke-WebRequest https://raw.githubusercontent.com/vvvv/PublicContent/master/nugeticon.png -OutFile deployment\nugeticon.png
nuget pack deployment\VL.Agent.nuspec -OutputDirectory artifacts\packages -NoDefaultExcludes
```

`vl-mcp`, `vl-map`, and `vl-probe` are currently framework-dependent .NET 10
executables. vvvv-loaded code targets `net8.0-windows7.0` and is built against
the published `VL.HDE` 2025.7.2 package.

The GitHub package workflow builds and verifies a `.nupkg` artifact. It does not
push to NuGet.org until a `NUGET_KEY` publishing step is deliberately enabled.
