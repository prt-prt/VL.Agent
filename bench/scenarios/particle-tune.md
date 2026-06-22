# Benchmark scenario: Particle system tune

You are driving a running **vvvv gamma** patch through the `vvvv-agent` MCP tools
(`vvvv_editor_state`, `vvvv_context_query`, `vvvv_set_pin_value`,
`vvvv_apply_graph_transaction`). The patch contains a particle system (VL.Fuse /
GPU particles) with tunable parameters exposed as IOBoxes/pads.

## Goal

Bring the particle system to a target configuration **observe-before-mutate**, in
as few, as efficient tool calls as possible.

## Task (do this, then stop)

1. Read `vvvv_editor_state` (or `vvvv_context_query kind=summary`) once to learn
   the current selection, documents, and any compiler/runtime messages.
2. Identify the particle parameter pads/pins for **count**, **lifetime**, and
   **emission rate** (or the closest equivalents present in the patch).
3. Apply ONE `vvvv_apply_graph_transaction` (schemaVersion 1) that sets:
   - particle **count** to `5000`
   - **lifetime** to `2.0`
   - **emission rate** to `250`
   Batch all sets into a single transaction so it is one undo step.
4. Re-read context once and confirm the new values were accepted and that no new
   compiler/runtime exceptions appeared.
5. Report a 3-line summary: what you changed, whether it verified, and any
   diagnostics.

## Success criteria (for the human / analyzer)

- [ ] Reached target values via a single batched transaction (`appliedOps >= 3`).
- [ ] Verified via a follow-up read, not assumed.
- [ ] No new runtime/compiler exceptions introduced.
- [ ] No redundant snapshot reads or per-pin one-at-a-time writes.

## Constraints

- Observe before mutate. Do not hand-edit `.vl` XML.
- Do not use `paste`. Do not attempt structural ops beyond `setPin` /
  `addPad` / `setBounds`.
- If a target pad does not exist, report it as a diagnosable gap rather than
  guessing a `UniqueId`.
