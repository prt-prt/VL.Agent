# Benchmark scenario: animated Skia line (smallest)

You are driving a running **vvvv gamma** patch through the `vvvv-agent` MCP tools
(`vvvv_editor_state`, `vvvv_context_query`, `vvvv_set_pin_value`,
`vvvv_node_query`, `vvvv_apply_graph_transaction`). This is the smallest
end-to-end benchmark.

## Goal

Produce a **Skia texture** that shows a **black vertical line** moving smoothly
**left-to-right across a white background**, looping. Observe before mutating; use
as few, as efficient tool calls as possible, but prefer a real attempt with
diagnostics over stopping early.

## Task (do this, then stop)

1. Read context once (`vvvv_context_query kind=summary`) to learn the current
   documents, selection, and any compiler/runtime messages.
2. Determine what already exists in the patch versus what must be created to reach
   the goal: a white background fill, a vertical line, a time source animating the
   line's horizontal position, and a Skia renderer/texture output.
3. Use `vvvv_node_query` for any unfamiliar node-browser symbols or pin names
   before placing nodes. If several plausible candidates exist, choose the most
   relevant one and continue.
4. Reach the goal using only the **supported experimental** write ops
   (`setPin`, `addNode`, `addPad`, `connect`, `setBounds`, `select`) in batched
   `vvvv_apply_graph_transaction` requests:
   - If the line + renderer already exist, animate the X position to sweep `0 -> 1`
     and confirm it loops.
   - If the graph does not exist, **attempt to build it**. Do not stop just
     because the exact recipe is unknown.
   - Use `dryRun=true` first when trying uncertain node symbols or pin names.
   - If dry run or apply fails, read the diagnostics, adjust the node symbols,
     pins, or graph shape, and retry.
   - Existing compiler warnings are not a reason to avoid writes. Use
     `validate=false` for structural plumbing if needed, then verify with a final
     read.
5. Re-read context once to confirm what you changed was accepted and introduced no
   new compiler/runtime exceptions.
6. Report a 3-line summary: what you changed, whether it verified, and the exact
   gap (if any) that blocked full completion.

## Attempt Budget

- Make at least one concrete graph transaction attempt unless the MCP bridge is
  unavailable.
- Use up to 10 `vvvv_node_query` calls to discover symbols and pins.
- Use up to 4 graph transaction attempts total, including dry runs.
- Prefer one larger transaction over many tiny ones, but retry with smaller
  transactions if diagnostics need isolation.
- Do not spend the run analyzing missing tooling. The benchmark owner will infer
  missing platform capability from your transcript and diagnostics.

## Success criteria (for the human / analyzer)

- [ ] One read -> node queries as needed -> concrete transaction attempt(s) ->
      one verify read -> stop.
- [ ] If fully buildable today: black line visibly sweeps across white, looping.
- [ ] If not buildable today: the attempted transaction(s), diagnostics, and
      remaining blocker are named clearly.
- [ ] No redundant full snapshot reads or per-pin one-at-a-time writes.

## Constraints

- Observe before mutate. Do not hand-edit `.vl` XML. Do not use `paste`.
- Only `setPin` / `addNode` / `addPad` / `connect` / `setBounds` / `select` are
  supported writes today.
- Current known limitation: if a graph requires connecting arbitrary unselected
  existing live nodes, report that exact gap. Connections between
  transaction-created nodes must use `alias:PinName`.
- Avoid fabricated `UniqueId`s, but do not avoid plausible node-browser symbols:
  resolve candidates with `vvvv_node_query`, then test with `dryRun`.
