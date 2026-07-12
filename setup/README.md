# Install the CI alpha

The package workflow uploads two files in the `VL.Agent-alpha` artifact:

- `VL.Agent.<version>.nupkg`
- `Install-VL.Agent.ps1`

On a Windows machine with vvvv gamma 7.2 installed, put both files in one
directory and run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
$package = Get-ChildItem .\VL.Agent.*.nupkg | Select-Object -First 1
.\Install-VL.Agent.ps1 -PackagePath $package.FullName
```

This installs the package to `%LOCALAPPDATA%\vvvv\gamma\nugets\VL.Agent`, checks
that the bridge and tools are present, and writes `vl-agent.mcp.json` containing
the absolute path to the bundled MCP server. The command printed at the end
starts vvvv with the package repository enabled and opens `VL.Agent.HDE.vl`.

The bundled `vl-mcp`, `vl-map`, and `vl-probe` executables are self-contained;
installing a separate .NET runtime is not required.

Use `-PackageRepository <path>` to install elsewhere. Reinstall an existing copy
with `-Force`. Pass `-VvvvPath <path-to-vvvv.exe>` to `Start-VL.Agent.ps1` when
vvvv is installed outside `C:\Program Files\vvvv`.
