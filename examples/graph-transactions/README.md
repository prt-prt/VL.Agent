# Graph Transaction Examples

Copy these payloads into `vvvv_apply_graph_transaction` calls.

Replace placeholder `UniqueId` values with values from `vvvv_editor_state`.

Target format for the first implementation:

```text
<UniqueId>:<PinName>
```

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
