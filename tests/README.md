# Tests

`smoke-tools.sh` builds the standalone MCP/indexing projects and exercises the
stdio MCP surface, bundled resources, graph-transaction schema, and a synthetic
editor snapshot.

Run it from any working directory:

```shell
DOTNET=dotnet tests/smoke-tools.sh
```

The vvvv-loaded `VL.Agent` project is compiled on Windows in the package workflow.
Runtime/HDE behavior still requires vvvv gamma 7.2; those checks should be added
here with `VL.TestFramework` as stable test patches become available.
