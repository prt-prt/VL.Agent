# Graph Transaction Examples

Copy these payloads into `vvvv_apply_graph_transaction` calls.

Replace placeholder `UniqueId` values with values from `vvvv_editor_state`.

Target format for the first implementation:

```text
<UniqueId>:<PinName>
```

`addPad` creates a typed IOBox on the active editor canvas. The apply result
returns created aliases mapped to vvvv `UniqueId` values.

`connect` can link transaction-created pad aliases, transaction-created node
pins written as `<alias>:<PinName>`, existing pads, and existing node pins
written as `<NodeUniqueId>:<PinName>`. Dry-runs validate created-node pin
direction and type, so route incompatible types through an explicit conversion
or join node.

`setBounds` moves or resizes an existing node/pad by full `UniqueId`. The bridge
resolves current selection first, then the live graph model; alias targets are
not supported yet.

`select` can select aliases created earlier in the same transaction, or existing
node/pad targets by full `UniqueId` when they are resolvable in the live graph
model.

Example MCP tool arguments:

```json
{
  "transaction": {
    "schemaVersion": 1,
    "label": "Dry run set pin",
    "dryRun": true,
    "ops": [
      {
        "op": "setPin",
        "target": "<UniqueId>:Input",
        "value": 42,
        "type": "Int32"
      }
    ]
  }
}
```

For structural examples, see `dry-run-add-pad.json`,
`dry-run-add-pad-connect.json`, `dry-run-set-bounds.json`, and
`dry-run-add-pad-select.json`.
