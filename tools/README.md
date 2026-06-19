# tools/

Standalone .NET 10 command-line tools that make up the read-only intelligence
layer of the vvvv-agent. All are **static / metadata-only** — they never execute
vvvv code, so they are safe to run against any project or install.

## `vl-probe` — API surface dumper

Reflects (metadata-only, via `MetadataLoadContext`) over the assemblies shipped
with a vvvv gamma install and dumps their public API. Used to validate which
editor/runtime/model APIs the agent can build on. Produced
`research/windows-api-validation-findings.md`.

```bash
cd vl-probe
dotnet run -c Release                                   # defaults to gamma 7.2
dotnet run -c Release -- --install "<install dir>" \
    --targets VL.Lang.dll,VL.HDE.dll,VL.Core.dll --out <dir>
```

Outputs `api-full.md` (every public type) and `api-bridge-relevant.md`
(keyword-filtered to Session/Node/Pin/Command/Channel/…).

## `vl-map` — project cartographer

Indexes a vvvv project: walks `.vl`/`.cs`/`.csproj`/`.sdsl`, parses every `.vl`
document, and emits a structured JSON index plus a human-readable summary.

```bash
cd vl-map
dotnet run -c Release -- --project "<project dir>" [--out vl-map.json] [--quiet]
```

Per document it extracts: id, language/format version, definitions (with kind),
nuget/document/platform dependencies, referenced libraries, and element counts
(nodes/pads/links/canvases/pins). Project-wide it builds the document-dependency
graph and flags issues:

- **package version drift** — a package pinned to multiple versions across documents,
- **missing document dependencies** — relative `DocumentDependency` paths that don't resolve,
- **duplicate element IDs** — violations of the `.vl` uniqueness rule,
- **parse failures**.

Verified against `testbed/dodecahedron-vl` (22 documents, 98 definitions, 1968
nodes): correctly surfaced 8 packages with version drift and 2 archived
documents with dangling `BunrakuFrame.vl` references.
