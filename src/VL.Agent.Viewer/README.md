# vl-view

A self-contained, dependency-free force-directed viewer for the `VL.Agent` static
index. One HTML file, no build step, no CDN — opens in any browser and works
offline. It renders the graph that `vl-map` extracts: document dependencies,
package fan-out, and version drift across documents.

It offers two views over one index:

- **Project** — documents, packages, and referenced libs with their dependencies.
  Version drift shows as a red ring + dashed edge.
- **Patch · `<doc>`** — the node/pad/link dataflow graph inside one `.vl` document.

## Use

```sh
# 1. produce an index (from the repo root)
dotnet src/VL.Agent.Map/bin/Release/net10.0/vl-map.dll --project . --out vl-map.json

# 2a. open index.html and drop vl-map.json onto the canvas, or
# 2b. serve the folder so it auto-loads sample-vl-map.json:
python3 -m http.server --directory src/VL.Agent.Viewer 8731   # http://localhost:8731/
```

`sample-vl-map.json` is a checked-in snapshot of this repo's own index, so the
viewer shows something immediately.

## Interaction

- drag nodes · drag canvas to pan · scroll to zoom
- click a node → neighbor highlight + detail pane
- legend checkboxes toggle Documents / Packages / Referenced libs
