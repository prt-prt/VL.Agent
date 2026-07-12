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

`vl-mcp`, `vl-map`, and `vl-probe` are published as self-contained Windows x64
executables, so the alpha artifact does not require a separate .NET 10 runtime.
vvvv-loaded code targets `net8.0-windows7.0` and is built against the published
`VL.HDE` 2025.7.2 package.

The GitHub package workflow builds, unpacks, and executes the bundled tools from
the `.nupkg`, then uploads it together with `Install-VL.Agent.ps1` as the
`VL.Agent-alpha` artifact. It does not push to NuGet.org.
