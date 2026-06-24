# Graph Transaction Protocol

First implementation contract for high-throughput vvvv patch editing.

The goal is to let an agent propose a complete graph change as one structured
transaction instead of placing and connecting nodes one request at a time.

Schema: `schemas/graph-transaction.schema.json`

Examples: `examples/graph-transactions/`

## Principles

- One transaction should map to one user-visible operation and, where possible,
  one undo step.
- The HDE bridge should support `dryRun` before apply so the cockpit can preview
  operations.
- Every created element gets a transaction-local `alias`; the bridge returns the
  actual vvvv ids after apply.
- Validation is part of the transaction result, not a separate afterthought.
- The schema is backend-neutral. Codex, pi, Claude, or a custom runtime can all
  emit the same transaction shape.

## Minimal Apply Result

```json
{
  "ok": true,
  "partial": false,
  "transactionId": "20260620-graph-001",
  "label": "Create OSC controlled Skia visualization",
  "created": {
    "oscIn": "documentId elementId",
    "amplitude": "documentId elementId"
  },
  "connected": ["Id 4521->Id 4537"],
  "diagnostics": [],
  "durationMs": 42
}
```

## Node Discovery

Before creating unfamiliar nodes, call `vvvv_node_query` with node-browser search
text such as `Skia Line` or `LFO`. The live query returns exact `addNode` symbol
strings and input/output pin names/types for the active patch's current
compilation, avoiding guessed categories or endpoints.

## First Implementation Slice

Start with operations that are already close to proven:

1. `setPin`
2. `addNode`
3. `addPad`
4. `connect`
5. `setBounds`
6. `select`
7. `validate`

The first implementation accepts `setPin` targets in the form:

```text
<UniqueId>:<PinName>
```

Example:

```json
{
  "op": "setPin",
  "target": "D8uX... Vj3K...:Input",
  "value": 42,
  "type": "Int32"
}
```

Multiple `setPin` operations are accumulated and committed with a single
`Confirm(...)` call. `dryRun=true` validates targets and coercion without applying
the accumulated solution.

`addPad` is the first structural operation. It creates a typed IOBox on the active
editor canvas and returns the created pad's `UniqueId` under the operation's
transaction-local `alias`. The initial implementation is intentionally narrow:
primitive types only, active-canvas placement only, and no node-browser symbol
resolution. Mixed `addPad` + `setPin` transactions are rejected for now so the
bridge never applies value edits before a structural edit failure.

`connect` creates a dataflow link through `Patch.GetOrAddLink(...)` and commits
the returned link's updated `ContainingPatch`. The current implementation
supports transaction-created pad aliases, transaction-created node aliases
written as `<Alias>:<PinName>`, existing pad `UniqueId` targets, and existing
node pins written as:

```text
<NodeUniqueId>:<PinName>
```

For example, an agent can create an `LFO` process node and a `Math:+` operation
node, then connect `lfo:Phase` to `plus:Input` in one transaction.

For transaction-created node endpoints, dry-run validates pin direction and pin
type from the resolved node definitions. A scalar-to-vector mistake such as
`lfo:Phase -> rect:Position` fails before mutation with a type-mismatch
diagnostic; the agent should insert a suitable conversion/join node instead.

Successful apply only fills the `connected` result after `MakeCurrent(...)`
returns and the link is observable or the link collection is not exposed by the
public model surface. If a later structural step fails after earlier edits were
committed, the result reports `partial:true`, keeps `connected` honest, and
returns the created alias ids so a follow-up repair transaction can address them
directly by `UniqueId`.

`setBounds` repositions or resizes an existing node/pad by full `UniqueId`.
Targets are resolved from the current selection first, then from the live graph
model exposed by the current solution/active patch. Alias targets are not
supported yet; use `[x,y]` or `[x,y,width,height]`.

`select` updates the editor selection through `API.CurrentSelection`. The first
slice supports aliases created earlier in the same transaction and existing
node/pad targets by full `UniqueId`, including unselected elements resolvable
through the live graph model.

`addNode` creates operation and process nodes on the active editor canvas through
the public `ModelExtensions.AddNode(...)` path. The first implementation accepts
symbols in this shape:

```text
<operation|process>:<Category>:<Name>
```

The reference is resolved against the live compilation with vvvv's symbol
resolver, normalized with `NodeRefHelpers.NormalizeNodeReferenceOnCreation`, and
then inserted into the active patch. Live verification in vvvv gamma 7.2 placed
`operation:Math:+` and `process:Animation:LFO`; same-transaction `select` works
for created node aliases. Multi-node failure is not atomic yet: if a later
`addNode` fails, an earlier valid node may already have been committed.

Then add the remaining structural graph operations once the safe editor-command
boundary is confirmed:

1. `disconnect`
2. `annotate`

## Open Questions

- Can structural edits be grouped into one `Confirm(...)` call?
- Can `addNode`/`addPad`/`connect` be made fully atomic across the whole
  transaction, instead of reporting partial edits when a later commit fails?
- How should endpoint references resolve when aliases and existing ids collide?
- Should `symbol` reference node-browser labels, stable symbol IDs, or generated
  `NodeReference` snippets?
- Can `dryRun` use the same resolver as apply without mutating the graph?
