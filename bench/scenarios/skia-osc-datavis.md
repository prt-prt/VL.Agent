# Benchmark scenario: Skia + OSC data visualization

You are driving a running **vvvv gamma** patch through the `vvvv-agent` MCP tools
(`vvvv_editor_state`, `vvvv_context_query`, `vvvv_set_pin_value`,
`vvvv_apply_graph_transaction`). The patch renders a **Skia** data visualization
whose inputs are driven by **OSC** values surfaced as public channels / pads.

## Goal

Configure the visualization to a known test state and confirm the Skia output
reacts, **observe-before-mutate**, in as few efficient tool calls as possible.

## Task (do this, then stop)

1. Read context once to learn the current selection, documents, and any
   compiler/runtime messages.
2. Identify the pads/pins that stand in for the OSC-controlled visualization
   parameters: **value range max**, **bar/point count**, and a **color/hue**
   control (or the closest equivalents present).
3. Apply ONE `vvvv_apply_graph_transaction` (schemaVersion 1) that sets:
   - range max to `100`
   - count to `32`
   - hue to `0.6`
   Batch all sets into a single transaction (one undo step).
4. Re-read context once and confirm the values were accepted and that the Skia
   render path reports no new exceptions.
5. Report a 3-line summary: what you changed, whether it verified, and whether any
   expected OSC path / pad was **missing or stale** (a diagnosable gap).

## Success criteria (for the human / analyzer)

- [ ] Reached target values via a single batched transaction (`appliedOps >= 3`).
- [ ] Verified via a follow-up read, not assumed.
- [ ] Correctly flagged any missing/stale OSC pad instead of inventing an id.
- [ ] No redundant snapshot reads or per-pin one-at-a-time writes.

## Constraints

- Observe before mutate. Do not hand-edit `.vl` XML.
- Do not use `paste`. Do not attempt structural ops beyond `setPin` /
  `addPad` / `setBounds`.
- Skia output is judged visually by the human; your job is to make the inputs
  change correctly and report verifiability.
