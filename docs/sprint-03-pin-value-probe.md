# Sprint 03: live pin value probe

## Goal

Give the agent the equivalent of print debugging: read live pin values from the
running patch across several frames, so it can tell a working edit from a
compiling-but-wrong one.

This is the first observation channel from `iteration-loop.md`. Compiler
verification (sprint 02) answers "did this break the build". A value probe
answers "is anything actually flowing, and is it changing".

## Why sampling, not a single read

One instant cannot distinguish a live oscillator from a stuck value. The probe
samples across frames and reports the shape of what it saw — first, last,
distinct count, changed, and numeric min/max — so questions like "is this
oscillating", "is this stuck at zero", and "is this NaN" are answerable from one
request.

## Contract

Read-only op, no solution mutation:

```json
{
  "op": "probePins",
  "frames": 8,
  "pins": ["Output"],
  "targets": ["Id abc123/Id def456"],
  "maxValueLength": 200,
  "maxSamples": 16
}
```

- `frames` — how many vvvv frames to sample. Default 8, valid 1–120.
- `pins` — optional pin-name filter, case-insensitive. Empty means all pins.
- `targets` — optional element filter, matching `UniqueId` exactly or element
  name case-insensitively. Empty means every probeable selected element.
- `maxValueLength` — per-sample string cap. Default 200, valid 16–4096.
- `maxSamples` — retained raw samples per pin. Default 16, valid 1–120.

Out-of-range and wrong-typed values are **rejected** with an error naming the
field and its range, not silently clamped — a window the caller did not ask for
is a false signal in a search loop. A blank filter entry is rejected too, since
treating it as "no filter" would silently widen the probe. The option object
clamps as well, so a future caller that bypasses the reader still cannot produce
an unbounded window.

Result:

```json
{
  "ok": true,
  "status": "completed",
  "source": "selection",
  "frames": 8,
  "elementCount": 1,
  "pinCount": 3,
  "elements": [
    {
      "uniqueId": "Id abc123/Id def456",
      "name": "LFO",
      "kind": "node",
      "pins": [
        {
          "name": "Output",
          "type": "Single",
          "isInput": false,
          "isConnected": true,
          "first": "0.12",
          "last": "0.87",
          "sampleCount": 8,
          "distinctCount": 8,
          "changed": true,
          "numeric": true,
          "min": 0.12,
          "max": 0.98,
          "samples": ["0.12", "0.35", "0.61", "0.87"],
          "samplesTruncated": false
        }
      ]
    }
  ]
}
```

`status` is `completed` or `noPins`. `noPins` returns `ok: false` with an
actionable error and is reported on the first frame rather than after the full
sampling window, so a bad request fails fast.

## Source of pins: current selection

Live values are reachable through `ILiveDataHub.Info.Value`, and the only live
element lookup this codebase has is selection-based
(`TryGetSelectedLiveElement`, `TryGetSelectedDataHubEndpoint`). So this slice
probes the current editor selection.

That is a real limit, and it is also how a human inspects a patch — click the
node, look at the values. Because `select` is already a supported graph-transaction
op, an agent can select then probe without touching document state.

A pad contributes a single pin named `Value`, matching the convention already
used by the MCP-side static pin validation.

**Open question for Windows validation:** whether `API.ActiveLiveCanvasStream`
exposes live elements for a whole-canvas walk. If it does, a `source: "canvas"`
tier can be added behind the same pure pipeline without changing the contract.
Do not add speculative reflection for this before the API is confirmed with
`vl-probe`.

## Scope

- One pure sampler owned by each `CommandProcessor` instance, advanced once per
  `Update()` frame, mirroring `CompilerVerificationTracker`.
- A pending probe defers its result the same way a verified transaction does.
- While a probe is pending, later mailbox requests wait, so the sampled patch
  cannot be mutated mid-window.
- Never sleeps, blocks the editor thread, or posts recursive editor work.
- All sampling, filtering, aggregation, formatting, and result shaping is pure
  and lives in `PinProbe.cs` with no vvvv dependency.

## Explicitly out of scope

- Frame or texture capture (the visual channel is a later sprint).
- Framerate and cost reporting.
- Whole-canvas or by-UniqueId lookup outside the selection.
- Streaming or pushed values; this is one bounded request/response.
- Any mutation, including auto-selecting the requested targets.

## Acceptance criteria

1. A completed probe reports per-pin first, last, sample count, distinct count,
   and `changed`.
2. A pin whose values all parse as numbers reports `numeric: true` with min and
   max; a pin with any non-numeric sample reports `numeric: false` and no
   min/max.
3. Retained samples are bounded by `maxSamples` and flag truncation, while
   `sampleCount` and `distinctCount` stay accurate for the full window.
4. Per-sample values are truncated to `maxValueLength` with a visible marker.
5. Pin and target filters are honored, targets matching by `UniqueId` or name.
6. Pins appearing or disappearing mid-window are retained with an honest
   `sampleCount` rather than dropped or crashing.
7. An empty first frame returns `noPins` with `ok: false` immediately.
8. The probe never reports `ok: true` for a window in which it collected nothing.
9. Pure sampler tests and the MCP smoke suite pass cross-platform.

## Windows/vvvv validation

The runtime gate must confirm on vvvv gamma that selecting a node with a moving
output (an `LFO`) and probing it returns `changed: true` with distinct samples,
that selecting a static IOBox returns `changed: false`, and that an empty
selection returns `noPins`. Also confirm the mailbox deadline scales with
`frames` so a long window does not read as a timeout.
