<#
.SYNOPSIS
  Repeatable VL.Agent speed benchmark. Launches Codex headless against a scenario
  prompt, times the run, and captures the session transcript for later inspection.

.DESCRIPTION
  Each run:
    1. Snapshots the .agent/results loop (so we can count ops this run produced).
    2. Runs `codex exec <scenario prompt>` non-interactively, tee'd to a run log.
    3. Records wall-clock time and basic loop efficiency (ops, ok/err).
    4. Writes bench/runs/<timestamp>-<scenario>.{log,summary.json} and prints a summary.

  This is deliberately low-complexity: it measures *time* and gives you a pointer
  into the session history. Deeper analysis = open the .log / transcript by hand.

.PARAMETER Scenario
  Path to a scenario .md file (see bench/scenarios/). Defaults to skia-line
  (the smallest end-to-end scenario).

.PARAMETER Project
  vvvv project dir whose .agent/ loop is driven. Defaults to the repo root.

.PARAMETER CodexArgs
  Extra args passed verbatim to `codex exec` (e.g. '--model','o3'). The runner
  already passes --cd <Project> and a sandbox/approval flag; override as needed.

.PARAMETER ApprovalFlag
  Approval/sandbox flag for codex exec. Default
  '--dangerously-bypass-approvals-and-sandbox' so the agent can use vl-mcp tools
  without prompting. Set to '' to use your codex defaults.

.EXAMPLE
  pwsh bench/run-bench.ps1
  pwsh bench/run-bench.ps1 -Scenario bench/scenarios/skia-osc-datavis.md
  pwsh bench/run-bench.ps1 -CodexArgs '--model','gpt-5-codex'
#>
[CmdletBinding()]
param(
    [string]$Scenario = "$PSScriptRoot/scenarios/skia-line.md",
    [string]$Project = "",
    [string[]]$CodexArgs = @(),
    [string]$ApprovalFlag = '--dangerously-bypass-approvals-and-sandbox'
)

$ErrorActionPreference = 'Stop'

$Scenario = (Resolve-Path $Scenario).Path
if (-not $Project) { $Project = Split-Path -Parent $PSScriptRoot }
$Project  = (Resolve-Path $Project).Path
$runsDir  = Join-Path $PSScriptRoot 'runs'
New-Item -ItemType Directory -Force -Path $runsDir | Out-Null

if (-not (Get-Command codex -ErrorAction SilentlyContinue)) {
    throw "codex CLI not found on PATH. Install Codex or add it to PATH first."
}

$scenarioName = [IO.Path]::GetFileNameWithoutExtension($Scenario)
$stamp        = Get-Date -Format 'yyyyMMdd-HHmmss'
$runId        = "$stamp-$scenarioName"
$logPath      = Join-Path $runsDir "$runId.log"
$summaryPath  = Join-Path $runsDir "$runId.summary.json"

$resultsDir = Join-Path $Project '.agent/results'
function Get-ResultFiles { if (Test-Path $resultsDir) { Get-ChildItem $resultsDir -Filter *.json -File } else { @() } }

# --- prompt -----------------------------------------------------------------
$prompt = Get-Content -Raw -LiteralPath $Scenario

Write-Host "== VL.Agent bench: $scenarioName ==" -ForegroundColor Cyan
Write-Host "  project : $Project"
Write-Host "  scenario: $Scenario"
Write-Host "  log     : $logPath"
Write-Host ""

# --- snapshot the loop before ----------------------------------------------
$before    = Get-ResultFiles
$beforeSet = @{}; $before | ForEach-Object { $beforeSet[$_.Name] = $_.LastWriteTimeUtc }

# --- run codex exec, timed --------------------------------------------------
$codexArgsFull = @('exec', '--cd', $Project)
if ($ApprovalFlag) { $codexArgsFull += $ApprovalFlag }
$codexArgsFull += $CodexArgs

$sw = [System.Diagnostics.Stopwatch]::StartNew()
# Tee codex output to both console and the run log; stderr folded into the log.
$oldErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $prompt | & codex @codexArgsFull '-' 2>&1 | Tee-Object -FilePath $logPath
}
finally {
    $ErrorActionPreference = $oldErrorActionPreference
}
$exitCode = $LASTEXITCODE
$sw.Stop()
$elapsed = [math]::Round($sw.Elapsed.TotalSeconds, 1)

# --- snapshot the loop after & score ---------------------------------------
$after = Get-ResultFiles
$newOrChanged = $after | Where-Object {
    -not $beforeSet.ContainsKey($_.Name) -or $beforeSet[$_.Name] -ne $_.LastWriteTimeUtc
}

$okCount = 0; $errCount = 0
foreach ($f in $newOrChanged) {
    $txt = Get-Content -Raw -LiteralPath $f.FullName
    if ($txt -match '"ok"\s*:\s*true')  { $okCount++ }
    elseif ($txt -match '"ok"\s*:\s*false') { $errCount++ }
}
$opCount = $newOrChanged.Count

# Best-effort pointer into Codex session history.
$codexHome   = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME '.codex' }
$sessionsDir = Join-Path $codexHome 'sessions'
$latestSession = $null
if (Test-Path $sessionsDir) {
    $latestSession = Get-ChildItem $sessionsDir -Recurse -Filter *.jsonl -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
}

$summary = [ordered]@{
    runId          = $runId
    scenario       = $scenarioName
    project        = $Project
    elapsedSeconds = $elapsed
    codexExitCode  = $exitCode
    loopOps        = $opCount
    loopOk         = $okCount
    loopErr        = $errCount
    runLog         = $logPath
    codexSession   = if ($latestSession) { $latestSession.FullName } else { $null }
    timestampUtc   = (Get-Date).ToUniversalTime().ToString('o')
}
$summary | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 -LiteralPath $summaryPath

Write-Host ""
Write-Host "== result ==" -ForegroundColor Cyan
Write-Host ("  elapsed   : {0,8} s" -f $elapsed)
Write-Host ("  loop ops  : {0,8}  (ok={1} err={2})" -f $opCount, $okCount, $errCount)
Write-Host ("  exit code : {0,8}" -f $exitCode)
Write-Host "  run log   : $logPath"
if ($latestSession) { Write-Host "  session   : $($latestSession.FullName)" }
Write-Host "  summary   : $summaryPath"
