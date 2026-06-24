param(
    [string]$AgentDir = ".agent",
    [string]$Op = "nodeQuery",
    [string]$Query = "LFO",
    [int]$Count = 10,
    [int]$TimeoutMs = 5000
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

for ($i = 1; $i -le $Count; $i++) {
    $result = Submit-AgentRequest -AgentDir $AgentDir -Op $Op -Payload $payload -TimeoutMs $TimeoutMs
    $results.Add($result)
    Write-Host ("{0}/{1} ok={2} elapsed={3}ms bridge={4}ms wait={5}ms proc={6}ms" -f `
        $i, $Count, $result.ok, $result.elapsedMs, $result.bridgeRoundTripMs, $result.mailboxWaitMs, $result.processingMs)
}

$ok = @($results | Where-Object ok)
$failed = @($results | Where-Object { -not $_.ok })
$elapsed = @($ok | ForEach-Object elapsedMs | Sort-Object)
$bridge = @($ok | Where-Object { $_.bridgeRoundTripMs -ge 0 } | ForEach-Object bridgeRoundTripMs | Sort-Object)
$wait = @($ok | Where-Object { $_.mailboxWaitMs -ge 0 } | ForEach-Object mailboxWaitMs | Sort-Object)
$proc = @($ok | Where-Object { $_.processingMs -ge 0 } | ForEach-Object processingMs | Sort-Object)

function Percentile {
    param([int[]]$Values, [double]$P)
    if ($Values.Count -eq 0) { return $null }
    $index = [Math]::Min($Values.Count - 1, [Math]::Max(0, [int][Math]::Ceiling(($P / 100.0) * $Values.Count) - 1))
    return $Values[$index]
}

$summary = [ordered]@{
    agentDir = $AgentDir
    op = $Op
    count = $Count
    ok = $ok.Count
    failed = $failed.Count
    elapsedMs = [ordered]@{
        p50 = Percentile $elapsed 50
        p95 = Percentile $elapsed 95
    }
    bridgeRoundTripMs = [ordered]@{
        p50 = Percentile $bridge 50
        p95 = Percentile $bridge 95
    }
    mailboxWaitMs = [ordered]@{
        p50 = Percentile $wait 50
        p95 = Percentile $wait 95
    }
    processingMs = [ordered]@{
        p50 = Percentile $proc 50
        p95 = Percentile $proc 95
    }
}

$summary | ConvertTo-Json -Depth 8
