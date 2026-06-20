# Package Assets

This folder is the staging area for assets that should ship inside the future
`VL.Agent` NuGet package but are not direct vvvv documents.

Current intent:

- `tools/` - published CLI and MCP binaries grouped by runtime identifier.
- `schemas/` - copied JSON schemas if the package needs a self-contained copy.
- `examples/` - copied workflow examples if they should live next to tools.

The source of truth for schemas and examples remains the repository root until a
packaging script copies them here.
