# agentic-vl — guide for agents

This repo explores (and is incrementally building) a **vvvv-agent**: a text-first
assistant for understanding, debugging, and safely editing vvvv gamma projects.
You — the coding agent reading this — are effectively the front end of that system.
This file tells you what exists, what's verified, and how to use it.

## Ground truth first

- vvvv concepts: prefer the bundled **`vvvv-*` skills** (fundamentals, fileformat,
  custom-nodes, node-libraries, channels, editor-extensions, testing, …). They are
  accurate and specific to gamma.
- API facts: **`research/windows-api-validation-findings.md`** records the public
  API surface validated against the installed gamma 7.2 (not guesses). Read it
  before designing anything that touches vvvv internals.
- This is **Windows** with vvvv gamma 6.7/7.0/7.1/7.2 installed and .NET 10 SDK.
  vvvv 7.2 targets **net8.0**. Install root: `C:\Program Files\vvvv\vvvv_gamma_7.2-win-x64`.

## Tools (the read-only intelligence layer) — `tools/`

Both are static .NET 10 CLIs that run **no vvvv code**. See `tools/README.md`.

- **`vl-probe`** — metadata-only (`MetadataLoadContext`) dumper of a vvvv install's
  public API. Re-run it to answer "does API X exist / what's its shape" without
  launching vvvv. Output: `api-full.md`, `api-bridge-relevant.md`.
- **`vl-map`** — project cartographer. Indexes `.vl`/`.cs`/`.csproj`/`.sdsl`, parses
  every `.vl` (XML), emits `vl-map.json` + a summary: definitions, dependencies,
  element counts, document-dependency graph, and detection of package version
  drift / missing document deps / duplicate IDs.
  `dotnet run -c Release -- --project <dir>`

## Testbed — `testbed/dodecahedron-vl/`

A real vvvv project (22 `.vl`, `Spatial-AV.vl` entry point), cloned with its
`origin` remote **removed** and gitignored — safe to experiment against, can't be
pushed. Use it to validate any tool or analysis.

## What's confirmed about touching the live editor (don't reinvent)

- **Read editor/runtime state** via `VL.HDE.API` (channels/observables):
  `HoveredElement`, `CurrentSelection`/`Selection`, `LoadedDocuments`,
  `Messages`/`LatestMessagesFromAllRuntimes`/`LatestMessagesFromCompiler`.
- **Navigate / inject**: `VL.Lang.PublicAPI.IDevSession` — `ShowPatchOfNode`,
  `OpenDocument`, `Paste(modelSnippet, location)`.
- **Safe writes** (undo-integrated): `IDevSession.Current.CurrentSolution` →
  `SetPinValue(...)` / `ModifyPinGroup(...)` → `Confirm(kind)`.
- **Annotate patches**: `SessionNodes.AddMessage/AddPersistentMessage` on an element id.
- **Access root**: `VL.Core.AppHost.Current.Services` and `IDevSession.Current`.
  Inside vvvv these are also nodes, so a bridge can take them as **input pins**.

## Working principles for this project

1. **Observe before mutate.** Prefer read-only analysis; for edits prefer the
   `Confirm`-based solution API or `Paste` (both undo-safe) over hand-written `.vl` XML.
2. **Verify against the install / testbed**, not from memory. Use `vl-probe` to check
   an API actually exists in this gamma version.
3. **The `.vl` format is XML** but ID/uniqueness/reference rules matter — see the
   `vvvv-fileformat` skill before generating or editing it.
4. Keep tools static and side-effect-free unless explicitly building the runtime bridge.

## Conventions

- Tools target **net10.0** (they run on the dev machine). Anything loaded *into*
  vvvv targets **net8.0** to match the runtime.
- `testbed/`, `bin/`, `obj/` are gitignored. Commit source under `tools/`, docs under
  `research/`, and keep `SESSION_CONTEXT.md` current for cross-machine handoff.
