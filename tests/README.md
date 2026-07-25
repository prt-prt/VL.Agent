# Tests

`smoke-tools.sh` builds the standalone MCP/indexing projects and exercises the
stdio MCP surface, bundled resources, graph-transaction schema, and a synthetic
editor snapshot. It also runs the dependency-free unit suites for the vvvv-side
pure logic: the compiler verification tracker
(`VL.Agent.Verification.Tests`) and the pin probe sampler
(`VL.Agent.PinProbe.Tests`).

Those projects source-link individual files out of `src/VL.Agent` and stub the
narrow vvvv API surface they touch, so vvvv-side logic can be tested on any
platform without a vvvv installation. Keep new bridge logic in files that can be
linked this way — a file with no vvvv dependency at all, like `PinProbe.cs`,
needs no stub.

Run it from any working directory:

```shell
DOTNET=dotnet tests/smoke-tools.sh
```

The vvvv-loaded `VL.Agent` project is compiled on Windows in the package workflow.
Runtime/HDE behavior still requires vvvv gamma 7.2; those checks should be added
here with `VL.TestFramework` as stable test patches become available.
