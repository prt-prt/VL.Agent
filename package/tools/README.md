# Bundled Tools

Package builds place published tool binaries here.

Expected first runtime target:

```text
win-x64/
  vl-mcp.exe
  vl-map.exe
  vl-probe.exe
```

The tools are source-built from `tools/` during development. This package folder
is only for distribution artifacts.

Build the default `win-x64` layout from the repository root:

```shell
DOTNET=/Users/philipp/.dotnet/dotnet deployment/publish-tools.sh
```

Useful overrides:

```shell
RID=win-x64 CONFIGURATION=Release SELF_CONTAINED=false deployment/publish-tools.sh
```

Generated runtime folders such as `package/tools/win-x64/` are intentionally
ignored by git. They are local staging output for `deployment/VL.Agent.nuspec`.
