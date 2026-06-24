param(
    [string]$AgentDir = ".agent",
    [string]$Op = "nodeQuery",
    [string]$Query = "LFO",
    [int]$Count = 10,
    [int]$TimeoutMs = 5000,
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"

function New-AgentPayload {
    param([string]$Op, [string]$Query)

    switch ($Op) {
        "nodeQuery" {
            return [ordered]@{
                op = "nodeQuery"
                query = $Query
                limit = 1
            }
        }
        default {
            throw "Unsupported benchmark op '$Op'. Start with nodeQuery because it is read-only but exercises the live bridge."
        }
    }
}

function Submit-AgentRequest {
    param(
        [string]$AgentDir,
        [string]$Op,
        [object]$Payload,
        [int]$TimeoutMs
    )

    $requestsDir = Join-Path $AgentDir "requests"
    $resultsDir = Join-Path $AgentDir "results"
    New-Item -ItemType Directory -Force -Path $requestsDir | Out-Null
    New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

    $requestId = [guid]::NewGuid().ToString("N")
    $createdAt = [DateTimeOffset]::UtcNow
    $envelope = [ordered]@{
        schemaVersion = 1
        requestId = $requestId
        traceId = $requestId
        op = $Op
        transport = "fileMailbox"
        createdAtUtc = $createdAt.ToString("O")
        deadlineMs = $TimeoutMs
        payload = $Payload
    }

    $requestPath = Join-Path $requestsDir "$requestId.json"
    $tmpPath = "$requestPath.tmp"
    $resultPath = Join-Path $resultsDir "$requestId.json"
    $json = $envelope | ConvertTo-Json -Depth 20 -Compress
    Set-Content -LiteralPath $tmpPath -Value $json -NoNewline -Encoding UTF8
    Move-Item -LiteralPath $tmpPath -Destination $requestPath -Force

    $started = [Diagnostics.Stopwatch]::StartNew()
    while ($started.ElapsedMilliseconds -lt $TimeoutMs) {
        if (Test-Path -LiteralPath $resultPath) {
            try {
                $text = Get-Content -Raw -LiteralPath $resultPath
                Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
                $result = $text | ConvertFrom-Json
                $trace = $result.trace
                return [pscustomobject]@{
                    ok = [bool]$result.ok
                    requestId = $requestId
                    elapsedMs = [int]$started.ElapsedMilliseconds
                    bridgeRoundTripMs = Get-TraceInt $trace "roundTripMs"
                    mailboxWaitMs = Get-TraceInt $trace "mailboxWaitMs"
                    processingMs = Get-TraceInt $trace "processingMs"
                    error = $result.error
                }
            }
            catch {
                Start-Sleep -Milliseconds 25
            }
        }
        Start-Sleep -Milliseconds 25
    }

    return [pscustomobject]@{
        ok = $false
        requestId = $requestId
        elapsedMs = [int]$started.ElapsedMilliseconds
        bridgeRoundTripMs = -1
        mailboxWaitMs = -1
        processingMs = -1
        error = "timeout"
    }
}

function Get-TraceInt {
    param([object]$Trace, [string]$Name)
    if ($null -eq $Trace) { return -1 }
    $prop = $Trace.PSObject.Properties[$Name]
    if ($null -eq $prop -or $null -eq $prop.Value) { return -1 }
    return [int]$prop.Value
}

if ($Count -lt 1) { throw "Count must be at least 1." }

$resolvedAgentDir = Resolve-Path -LiteralPath $AgentDir -ErrorAction SilentlyContinue
if ($resolvedAgentDir) {
    $AgentDir = $resolvedAgentDir.Path
}
else {
    $AgentDir = Join-Path (Get-Location) $AgentDir
}
$payload = New-AgentPayload -Op $Op -Query $Query
$results = New-Object System.Collections.Generic.List[object]
$startedAtUtc = [DateTime]::UtcNow

for ($i = 1; $i -le $Count; $i++) {
    $result = Submit-AgentRequest -AgentDir $AgentDir -Op $Op -Payload $payload -TimeoutMs $TimeoutMs
    $results.Add($result)
    Write-Host ("{0}/{1} ok={2} elapsed={3}ms bridge={4}ms wait={5}ms proc={6}ms" -f `
        $i, $Count, $result.ok, $result.elapsedMs, $result.bridgeRoundTripMs, $result.mailboxWaitMs, $result.processingMs)
}

$ok = @($results | Where-Object ok)
$failed = @($results | Where-Object { -not $_.ok })
$elapsedValues = @($ok | ForEach-Object { [int]$_.elapsedMs } | Sort-Object)
$bridgeValues = @($ok | Where-Object { $_.bridgeRoundTripMs -ge 0 } | ForEach-Object { [int]$_.bridgeRoundTripMs } | Sort-Object)
$waitValues = @($ok | Where-Object { $_.mailboxWaitMs -ge 0 } | ForEach-Object { [int]$_.mailboxWaitMs } | Sort-Object)
$processingValues = @($ok | Where-Object { $_.processingMs -ge 0 } | ForEach-Object { [int]$_.processingMs } | Sort-Object)

$metricSummary = {
    param($Values)
    $typed = @($Values | ForEach-Object { [int]$_ } | Sort-Object)
    if ($typed.Count -eq 0) {
        return ,@{ "p50" = $null; "p95" = $null }
    }
    if (@($typed | Where-Object { $_ -ne 0 }).Count -eq 0) {
        return ,@{ "p50" = 0; "p95" = 0 }
    }
    $p50Index = [int][Math]::Ceiling(0.5 * $typed.Count) - 1
    $p95Index = [int][Math]::Ceiling(0.95 * $typed.Count) - 1
    if ($p50Index -lt 0) { $p50Index = 0 }
    if ($p95Index -lt 0) { $p95Index = 0 }
    if ($p50Index -ge $typed.Count) { $p50Index = $typed.Count - 1 }
    if ($p95Index -ge $typed.Count) { $p95Index = $typed.Count - 1 }
    return ,@{
        "p50" = $typed[$p50Index]
        "p95" = $typed[$p95Index]
    }
}

$elapsedSummary = & $metricSummary $elapsedValues
$bridgeSummary = & $metricSummary $bridgeValues
$waitSummary = & $metricSummary $waitValues
$processingSummary = & $metricSummary $processingValues

$summary = New-Object psobject
$summary | Add-Member -NotePropertyName "agentDir" -NotePropertyValue $AgentDir
$summary | Add-Member -NotePropertyName "op" -NotePropertyValue $Op
$summary | Add-Member -NotePropertyName "query" -NotePropertyValue $Query
$summary | Add-Member -NotePropertyName "count" -NotePropertyValue $Count
$summary | Add-Member -NotePropertyName "ok" -NotePropertyValue $ok.Count
$summary | Add-Member -NotePropertyName "failed" -NotePropertyValue $failed.Count
$summary | Add-Member -NotePropertyName "startedAtUtc" -NotePropertyValue $startedAtUtc.ToString("o")
$summary | Add-Member -NotePropertyName "completedAtUtc" -NotePropertyValue ([DateTime]::UtcNow).ToString("o")
$summary | Add-Member -NotePropertyName "elapsedMs" -NotePropertyValue $elapsedSummary
$summary | Add-Member -NotePropertyName "bridgeRoundTripMs" -NotePropertyValue $bridgeSummary
$summary | Add-Member -NotePropertyName "mailboxWaitMs" -NotePropertyValue $waitSummary
$summary | Add-Member -NotePropertyName "processingMs" -NotePropertyValue $processingSummary
$sampleArray = [object[]]$results.ToArray()
$summary | Add-Member -NotePropertyName "samples" -NotePropertyValue $sampleArray

if (-not $OutputPath) {
    $runsDir = Join-Path $PSScriptRoot "runs"
    New-Item -ItemType Directory -Force -Path $runsDir | Out-Null
    $safeQuery = ($Query -replace '[^A-Za-z0-9_-]+', '-').Trim('-')
    if (-not $safeQuery) { $safeQuery = "query" }
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputPath = Join-Path $runsDir "$stamp-mailbox-$Op-$safeQuery.summary.json"
}

$summary | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 -LiteralPath $OutputPath
Write-Host "summary: $OutputPath"
$summary | ConvertTo-Json -Depth 8
