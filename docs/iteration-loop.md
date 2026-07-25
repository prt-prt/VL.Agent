# Iteration loop

The strategic direction for agent throughput: make the patch/observe/adjust loop
cheap enough that an agent can find a working patch by iterating, the way a human
patcher does. Optimize the loop, not the agent's prior knowledge.

vvvv's core value is that patches run while they are edited. An agent that cannot
observe a running patch is not using vvvv; it is emitting graph operations and
hoping. Every unit of work that goes into making application and verification
cheaper and more legible is a unit the agent no longer spends reasoning about the
bridge instead of the program.

## Target loop

The loop to unlock, in the order a human performs it:

1. Decide what to add.
2. Find the node.
3. Place and connect it.
4. Observe the result — output, values, framerate.
5. Adjust, or discard and try again.

Steps 1–3 work today. Step 4 is missing except for compiler diagnostics. Step 5
is expensive because a discarded attempt leaves debris.

## Rejected alternative: authored recipe library

A curated library of higher-level patch recipes was considered and rejected.

- It does not scale against the extended ecosystem (`VL.Skia`, `VL.Stride`,
  `VL.OpenCV`, the `VL.IO.*` family, `VL.Audio`, community packages), all
  versioning independently.
- Each entry is a maintenance liability and a future source of deprecated
  patterns.
- It is a workaround for a blind agent: it encodes static knowledge to
  compensate for missing perception. Fixing perception removes the need.

The scalable substitute is to **index the help patches the ecosystem already
ships**. They are authored by library maintainers, they are plain `.vl` XML that
`vl-map` can already parse, and they stay current because they ship with the
package. This seeds the loop with a plausible first guess. It is subordinate to
the loop, never a substitute for it.

## Requirements

### Observation

Frames alone are not enough. Three channels matter:

- **Values.** The equivalent of print debugging. `EditorBridge` already
  serializes pin values for the current selection; generalize that to "probe
  these pins" with values available on demand or streamed. Often more diagnostic
  than the render output and far cheaper.
- **Frames.** Low-rate thumbnails, frame hash, `changedPixels`, `isBlank`. The
  contract is already specified under *Visual Verification Contract* in
  `agent-bridge-optimization-roadmap.md`. Prefer vvvv-native render target or
  texture capture over screen automation.
- **Framerate.** In vvvv a patch that runs too slowly is incorrect, not merely
  slow. Treat cost as an observable.

### Cheap revert

Most trials are wrong and get discarded, so discarding must be cheap and clean.

- Batch structural ops (`addNode`, `addPad`, `connect`) onto one solution before
  a single `MakeCurrent`, so one transaction is one undo step. This currently
  holds for node-pin `setPin` only, and the per-element `MakeCurrent` loops make
  an N-node transaction N+ undo steps. This is an enabler, not polish.
- `partial: true` reports damage without compensating for it. Verify-plus-
  rollback is the missing half of `sprint-02-compiler-verification.md`.
- **Scratch document.** The larger move: give the agent its own document to
  experiment in. Most of the safety ceremony — dry run, pre-flight validation,
  signature verification — is expensive precisely because it guards live user
  work. Inside a sandbox that ceremony can be dropped, and only the verified
  result is promoted through the careful path. This splits one loop that must be
  both fast and safe into two loops that are each only one of those.

### Incremental session

The transaction model — plan, validate, apply, verify, serialized — is built for
applying a known-good plan safely. Iteration wants the opposite: one small
change, observe, change again, with observation standing rather than requested.

Keep graph transactions for promotion out of the sandbox. Add a persistent
session with cheap incremental ops and a standing observation feed. This is the
concrete reason the transport work matters: the loop needs push, not a 100 ms
poll. See `agent-bridge-optimization-roadmap.md` and `agent-client-protocol.md`.

### Error quality

In a search loop, failure quality decides whether the agent converges or
thrashes. Diagnostics are the gradient.

- A `connect` type mismatch should report expected versus found and name the
  conversion node. The bridge can ask the resolver; the agent cannot.
- Never report a failure as a success. `HasModelLink` returning `true` on
  exception points the search the wrong way.
- Never report an unperformed check as passed. `validate` accepting `links` and
  `selection` as silent no-ops teaches the agent false things.
- Inconclusive must stay distinguishable from passed and failed, as
  `verification.status: timedOut` already does.

## Relation to sprint 02

`sprint-02-compiler-verification.md` is the first rung of this loop, and it
separates `applied` from `verification.status` correctly: whether the edit landed
and whether it is good are different questions.

Two consequences to track:

- **Verification latency is the new loop budget.** The mailbox went from p50
  `roundTripMs` 1898 to 48. Sprint 02 reintroduces a multi-frame gate — two
  settle frames minimum, up to 120 frames before timeout. Measure that at p50
  now; the old mailbox numbers are no longer the interesting ones.
- **Serialized verified transactions cap the loop at one experiment at a time.**
  Correct for contamination safety, and the constraint a scratch document would
  later relax.

## Sequencing

1. Observation: pin-value probes first (cheapest, most diagnostic), then frame
   probe, then framerate.
2. Cheap revert: batch structural ops into one undo step; then verify-plus-
   rollback; then the scratch document.
3. Persistent incremental session with a standing observation feed, over a
   streaming transport.
4. Help-patch indexing as loop seeding, whenever convenient.

Error quality is threaded through all four rather than scheduled as cleanup.
