# Sprint 02: compiler verification after graph edits

## Goal

Add an opt-in, cross-frame check that distinguishes a successfully applied graph
transaction from a transaction that introduced new compiler errors.

## Contract

```json
{
  "verify": {
    "compiler": "noNewErrors",
    "settleFrames": 2,
    "timeoutFrames": 120
  }
}
```

The bridge captures the current compiler-error multiset immediately before apply.
It then waits for a compiler observable emission and a stable error snapshot.
Only errors added relative to the baseline fail verification.

The result always separates mutation from verification:

- `applied: true` means the graph edit remains in the document.
- `verification.status: passed` means no new stable compiler errors were found.
- `verification.status: failed` returns the newly introduced errors.
- `verification.status: timedOut` is inconclusive and never claims a compiler
  failure.

Verification does not roll back edits in this sprint.

## Safety boundaries

- Verified transactions are serialized so later mutations cannot contaminate the
  diagnostic window.
- The state machine advances from `CommandProcessor.Update()` once per vvvv frame.
- It never sleeps, blocks the editor thread, or recursively posts editor work.
- Transactions without `verify` retain the existing immediate behavior.
- Existing pre-apply `validate` behavior is unchanged.

## Acceptance criteria

1. A post-apply compiler emission is required before pass/fail.
2. Existing errors do not fail `noNewErrors`.
3. New error multiplicity is preserved.
4. Warnings and informational messages are ignored.
5. Failure and timeout return `ok: false` with `applied: true`.
6. A pending verified transaction prevents later mailbox mutations until it
   completes.
7. Pure tracker tests and the MCP smoke suite pass cross-platform.

## Windows/vvvv validation

The final runtime gate must confirm that successful vvvv compilation emits on
`API.LatestMessagesFromCompiler`. If it does not, the conservative implementation
will time out rather than falsely pass; the next step would be an explicit
compilation revision/completion source from VL.Lang.
