# Graph Transaction Examples

Copyable payloads for `vvvv_apply_graph_transaction` calls. Replace placeholder
`UniqueId` values with values from `vvvv_editor_state`. Targets use the form
`<UniqueId>:<PinName>`.

See `docs/graph-transaction-protocol.md` for the full operation contract.

```json
{
  "transaction": {
    "schemaVersion": 1,
    "label": "Dry run set pin",
    "dryRun": true,
    "ops": [
      { "op": "setPin", "target": "<UniqueId>:Input", "value": 42, "type": "Int32" }
    ]
  }
}
```

Structural examples: `dry-run-add-pad.json`, `dry-run-add-pad-connect.json`,
`dry-run-set-bounds.json`, `dry-run-add-pad-select.json`.
