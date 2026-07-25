# Sprint 01: context-aware node catalog cache

## Goal

Reduce repeated live node-resolver work while establishing the cache boundary for
a future semantic node catalog.

The existing response cache only helps when the complete query and limit match.
New compound queries repeat resolver searches even when their terms were queried
moments earlier. Cold resolver work has measured in seconds, while exact cached
queries are already fast.

## Scope

- Keep one cache owned by each `CommandProcessor` instance.
- Scope it to the active patch and live compilation object.
- Cache normalized resolver terms as well as exact serialized responses.
- Reset the complete cache when the active patch or compilation changes.
- Return compact cache telemetry with each successful node query.
- Keep all resolver access on vvvv's editor/update thread.

## Acceptance criteria

1. Repeating an identical query returns a response-cache hit.
2. Overlapping queries reuse previously resolved terms.
3. Changing patch or compilation clears both term and response entries.
4. Candidate ordering, symbols, and pin descriptions remain compatible.
5. Cache storage is bounded and does not retain old compilations.
6. Existing MCP smoke tests still pass.

## Explicitly out of scope

- Background access to the vvvv resolver.
- Eager enumeration of the complete node browser.
- Sharing cached symbols with graph mutation code.
- Semantic embeddings, help-patch recipes, or persistent catalogs.
- Transport changes or graph mutation changes.

## Review and validation

The cross-platform smoke suite verifies the MCP surface. Runtime cache behavior
must also be checked on Windows with vvvv gamma by querying an exact phrase,
then an overlapping phrase, then switching patches or recompiling. Telemetry
should show response reuse, term reuse, and context resets respectively.

## Follow-up decision

Use live measurements from this slice to decide whether the next catalog step
should copy immutable symbol DTOs for background ranking, or remain incrementally
populated on the editor thread. Do not move resolver objects to a worker until
their thread-safety is demonstrated.
