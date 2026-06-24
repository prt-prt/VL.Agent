# VL.Agent speed benchmark

A low-complexity, repeatable benchmark for how fast and how efficiently the agent
drives a live vvvv gamma patch through the `vvvv-agent` MCP tools.

It measures the one thing that matters first — **wall-clock time** — and leaves a
**pointer into the session history** so you can inspect *what* the agent did and
*what could be improved* by hand. It is intentionally not a full analysis system.

## What it measures

| Signal | Source |
| --- | --- |
| Wall-clock time | runner stopwatch around `codex exec` |
| Loop ops this run | new/changed files in `<project>/.agent/results/` |
| ok vs err ops | `"ok":true` / `"ok":false` in each result |
| Codex exit code | `codex exec` |
| Full agent transcript | the run log + the Codex session `.jsonl` |

Efficiency and "what could be improved" are read off the transcript: count tool
calls, look for redundant `vvvv_editor_state` reads, per-pin writes instead of one
batched transaction, retries, and unverified claims. The scenario files list the
efficiency criteria a good run should hit.

## Prerequisites

- vvvv gamma running with `VL.Agent.HDE.vl` installed and `AgentHost` resolving
  your test project (see `docs/WINDOWS_TESTING.md`).
- A built `vl-mcp.exe` wired to your agent. Codex is already configured in
  `.codex/config.toml`.
- `codex` on PATH.

## Run

```powershell
# default: smallest scenario (animated Skia line) against the repo root .agent loop
pwsh bench/run-bench.ps1

# Windows PowerShell fallback
powershell -ExecutionPolicy Bypass -File bench\run-bench.ps1 -Scenario bench\scenarios\skia-line.md -Project C:\path\to\project

# larger scenarios
pwsh bench/run-bench.ps1 -Scenario bench/scenarios/particle-tune.md
pwsh bench/run-bench.ps1 -Scenario bench/scenarios/skia-osc-datavis.md

# drive a different vvvv project, pick a model
pwsh bench/run-bench.ps1 -Project C:\path\to\my-project -CodexArgs '--model','gpt-5-codex'
```

## Mailbox Latency Probe

To isolate bridge/mailbox latency from model and MCP overhead, run the direct
request/result probe while vvvv is running with `AgentHost`:

```powershell
powershell -ExecutionPolicy Bypass -File bench\run-mailbox-latency.ps1 -AgentDir .agent -Count 20
```

The first probe op is `nodeQuery`, which is read-only but still exercises the
live bridge, active canvas, and live compilation resolver. The result summary
reports elapsed time plus trace fields injected by `CommandProcessor`:
`mailboxWaitMs`, `processingMs`, and bridge `roundTripMs`.

Each agent scenario run writes to `bench/runs/` (git-ignored):

- `<timestamp>-<scenario>.log` — full Codex output (the readable transcript)
- `<timestamp>-<scenario>.summary.json` — time + loop counts + session pointer

Each mailbox latency probe also writes:

- `<timestamp>-mailbox-<op>-<query>.summary.json` — p50/p95 timings plus per-request samples

The printed summary ends with the path to the Codex session `.jsonl` (under
`$CODEX_HOME/sessions/`, default `~/.codex/sessions/`) for turn-by-turn history.

## Scenarios

- `scenarios/skia-line.md` — **smallest, default.** Animate a black line sweeping
  across a white Skia texture. Buildable end-to-end only once `addNode`/`connect`
  land; today it also benchmarks how cleanly the agent diagnoses that gap.
- `scenarios/particle-tune.md` — tune a Fuse/GPU particle system to target params
  in one batched transaction, verified.
- `scenarios/skia-osc-datavis.md` — configure an OSC-driven Skia data viz to a
  known test state and confirm reactivity / diagnose missing OSC paths.

Both descend from the "Scenario Benchmarks" stubs in `docs/WINDOWS_TESTING.md` and
are written as concrete, bounded, repeatable agent tasks. To add a scenario, drop
another `.md` in `scenarios/` with the same shape (Goal / Task / Success criteria /
Constraints) and pass it via `-Scenario`.

## Reading a run

A good run on these scenarios looks like:

- one read → one batched transaction (`appliedOps >= 3`) → one verify read → stop
- low elapsed time, `loopErr = 0`, exit code 0

Watch for these inefficiencies in the transcript:

- repeated full `vvvv_editor_state` reads where `vvvv_context_query` would do
- per-pin `vvvv_set_pin_value` calls instead of one `vvvv_apply_graph_transaction`
- claiming success without a follow-up read
- inventing a `UniqueId` instead of reporting a missing pad
