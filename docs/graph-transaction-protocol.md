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
  "transactionId": "20260620-graph-001",
  "label": "Create OSC controlled Skia visualization",
  "created": {
    "oscIn": "documentId elementId",
    "amplitude": "documentId elementId"
  },
  "diagnostics": [],
  "durationMs": 42
}
```

## First Implementation Slice

Start with operations that are already close to proven:

1. `setPin`
2. `validate`

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

Then add structural graph operations once the safe editor-command boundary is
confirmed:

1. `addPad`
2. `addNode`
3. `connect`
4. `disconnect`
5. `annotate`
6. `setBounds`
7. `select`

## Open Questions

- Which public HDE/session API safely creates nodes and links?
- Can structural edits be grouped into one `Confirm(...)` call?
- How should endpoint references resolve when aliases and existing ids collide?
- Should `symbol` reference node-browser labels, stable symbol IDs, or generated
  `NodeReference` snippets?
- Can `dryRun` use the same resolver as apply without mutating the graph?
