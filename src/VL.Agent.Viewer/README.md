# vl-view

A self-contained, dependency-free force-directed viewer for the VL.Agent static
index. One HTML file, no build step, no CDN — opens in any browser and works
offline (and, later, inside an HDE WebView2).

## Why it exists

Borrowed from the "code-as-a-queryable-graph + visual graph UI" idea in
`codebase-memory-mcp`, but built around vvvv's own structure. `vl-map` already
extracts the graph; this renders it so a human (or an agent's human) can see
document dependencies, package fan-out, and version drift at a glance — on macOS,
in CI, or in PR review, **without a vvvv runtime**.

## Design: one renderer, two feeds

The renderer consumes a **normalized graph** `{ nodes, edges, meta }`. Source
formats are converted by adapters, so the same UI can serve multiple feeds:

- `fromVlMap(index)` — a `vl-map` ProjectIndex JSON (static, cross-platform). **Done.**
- `fromEditorSnapshot(snap)` — a live `EditorWatcher` snapshot (HDE feed). **Future.**

This keeps the visualization in *one* place: ship it standalone now, and host the
same file inside the HDE cockpit later, feeding it live snapshots instead of a
static index. The native vvvv editor already draws patches; vl-view's value is the
*cross-document* view and overlays the editor can't show (dependency graph,
version drift, blast-radius — eventually).

### Normalized graph contract

```jsonc
{
  "nodes": [{ "id": "doc:Foo.vl", "label": "Foo", "kind": "doc|pkg|lib",
              "size": 12, "meta": { /* free-form, shown in the detail pane */ } }],
  "edges": [{ "from": "doc:Foo.vl", "to": "pkg:VL.CoreLib",
              "kind": "uses|refs|depends", "resolved": true }],
  "meta":  { "root": "...", "files": {...}, "warnings": [...] }
}
```

The viewer auto-detects input: a raw `vl-map` index (`GeneratedBy`/`Documents`) is
run through `fromVlMap`; an already-normalized `{nodes,edges}` is rendered as-is.

### Two views over one index

The **view switcher** (top of the side panel) toggles between:

- **Project** — documents, packages, referenced libs and their dependencies
  (`fromVlMap`). Version drift shows as a red ring + dashed edge.
- **Patch · `<doc>`** — the node/pad/link **dataflow graph inside one `.vl`**
  (`fromVlMapPatch`). Nodes (purple, sized by pin count) and IOBox pads (orange)
  are vertices; links are resolved from pin/pad endpoints to the owning vertex
  when that ownership is unambiguous, so you see real `A → B` dataflow. Click a
  node for its category, source library, in/out pin counts, and full pin list.

This is powered by `vl-map`'s per-document `Graph` (`PatchNode` / `PatchPad` /
`PatchLink`), where each link keeps raw endpoint ids and, where possible,
pre-resolved owning-vertex ids.

## Use

```sh
# 1. produce an index (from the repo root)
dotnet src/VL.Agent.Map/bin/Release/net10.0/vl-map.dll --project . --out vl-map.json

# 2a. open index.html and drop vl-map.json onto the canvas, or
# 2b. serve the folder so it auto-loads sample-vl-map.json:
python3 -m http.server --directory src/VL.Agent.Viewer 8731   # http://localhost:8731/
```

`sample-vl-map.json` is a checked-in snapshot of this repo's own index so the
viewer shows something immediately.

## Interaction

- drag nodes · drag canvas to pan · scroll to zoom
- click a node → neighbor highlight + detail pane (defs, stats, degree, warnings)
- legend checkboxes toggle Documents / Packages / Referenced libs
- version-drift packages get a red ring; unresolved/conflicting edges render dashed red

## Status

Prototype, but now with the real graph. `vl-map` extracts a vertex-level
dataflow graph per document (`VlDocumentInfo.Graph`) and the viewer renders it.

Known rough edges / next steps:

- **Layout** is a plain force sim — fine to ~200 nodes, sluggish beyond. Big
  patches (the schreiraum corpus has 200+ node patches) want a quadtree/Barnes-Hut
  step or a layered layout that respects pin in/out direction.
- **Patch nesting is flattened** — all canvases in a document collapse into one
  graph. `PatchNode` could carry its owning patch id to allow grouping / drill-in.
- **`fromEditorSnapshot`** adapter (live HDE feed) is still to be written.
- Edges don't yet encode the specific pin endpoints visually (only vertex→vertex).
