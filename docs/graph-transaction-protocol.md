# Graph Transaction Protocol

Implementation contract for high-throughput vvvv patch editing: an agent proposes
a complete graph change as one structured transaction instead of placing and
connecting nodes one request at a time.

- Schema: `schemas/graph-transaction.schema.json`
- Examples: `examples/graph-transactions/`

## Principles

- One transaction maps to one user-visible operation and, where possible, one
  undo step.
- The HDE bridge supports `dryRun` before apply so the cockpit can preview.
- Every created element gets a transaction-local `alias`; the bridge returns the
  real vvvv ids after apply.
- Validation is part of the transaction result.
- The schema is backend-neutral — any runtime can emit the same shape.

## Apply result

```json
{
  "ok": true,
  "partial": false,
  "transactionId": "20260620-graph-001",
  "label": "Create OSC controlled Skia visualization",
  "created": { "oscIn": "documentId elementId" },
  "connected": ["Id 4521->Id 4537"],
  "diagnostics": [],
  "durationMs": 42
}
```

If a structural step fails after earlier edits committed, the result reports
`partial:true`, keeps `connected` honest, and returns the created alias ids so a
follow-up repair transaction can address them by `UniqueId`.

## Node discovery

Before creating unfamiliar nodes, call `vvvv_node_query` with node-browser search
text (e.g. `Skia Line`, `LFO`). It returns exact `addNode` symbol strings plus
pin names/types for the active patch's current compilation, avoiding guessed
categories.

## Supported operations

Current slice: `setPin`, `addNode`, `addPad`, `connect`, `setBounds`, `select`,
`validate`. Use `dryRun=true` to validate without applying.

- **`setPin`** — target form `<UniqueId>:<PinName>`. Multiple `setPin` ops are
  accumulated and committed with one `Confirm(...)`.
- **`addNode`** — symbol form `<operation|process>:<Category>:<Name>`, resolved
  against the live compilation and inserted into the active patch. Multi-node
  failure is not atomic yet.
- **`addPad`** — creates a typed IOBox on the active canvas (primitive types,
  active-canvas placement only). Mixed `addPad` + `setPin` transactions are
  rejected so value edits never apply before a structural failure.
- **`connect`** — links transaction-created pad aliases, created node pins
  (`<alias>:<PinName>`), existing pads, and existing node pins
  (`<NodeUniqueId>:<PinName>`). Dry-run validates pin direction and type; route
  incompatible types through an explicit conversion/join node.
- **`setBounds`** — repositions/resizes an existing node/pad by full `UniqueId`,
  resolved from selection first, then the live graph model. `[x,y]` or
  `[x,y,width,height]`. No alias targets yet.
- **`select`** — updates the editor selection via `API.CurrentSelection` for
  same-transaction aliases or existing `UniqueId` targets.

Planned next: `disconnect`, `annotate`.
