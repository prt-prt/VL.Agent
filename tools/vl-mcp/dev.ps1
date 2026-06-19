param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $Watch
)

$ErrorActionPreference = "Stop"

$Project = Join-Path $PSScriptRoot "vl-mcp.csproj"
$Framework = "net8.0"
$Exe = Join-Path $PSScriptRoot "bin\$Configuration\$Framework\vl-mcp.exe"

function Invoke-DevBuild {
    [Console]::Error.WriteLine("[vl-mcp-dev] building $Configuration/$Framework")
    $log = New-TemporaryFile
    try {
        & dotnet build $Project -c $Configuration -p:AgenticVlDev=true *> $log
        $exitCode = $LASTEXITCODE
        Get-Content $log | ForEach-Object { [Console]::Error.WriteLine($_) }
        if ($exitCode -ne 0) {
            throw "dotnet build failed with exit code $exitCode"
        }
    }
    finally {
        Remove-Item $log -ErrorAction SilentlyContinue
    }
}

function Wait-ForSourceChange {
    [Console]::Error.WriteLine("[vl-mcp-dev] waiting for source changes")
    $script:VlMcpDevChanged = $false
    $watchers = @()
    foreach ($dir in @($PSScriptRoot, (Join-Path $PSScriptRoot "..\vl-map"))) {
        $watcher = [System.IO.FileSystemWatcher]::new((Resolve-Path $dir), "*.*")
        $watcher.IncludeSubdirectories = $true
        $watcher.EnableRaisingEvents = $true
        $action = { $script:VlMcpDevChanged = $true }
        Register-ObjectEvent $watcher Changed -Action $action | Out-Null
        Register-ObjectEvent $watcher Created -Action $action | Out-Null
        Register-ObjectEvent $watcher Deleted -Action $action | Out-Null
        Register-ObjectEvent $watcher Renamed -Action $action | Out-Null
        $watchers += $watcher
    }

    try {
        while (-not $script:VlMcpDevChanged) {
            Start-Sleep -Milliseconds 250
        }
        Start-Sleep -Milliseconds 300
    }
    finally {
        Get-EventSubscriber | Where-Object { $_.SourceObject -in $watchers } | Unregister-Event
        foreach ($watcher in $watchers) { $watcher.Dispose() }
    }
}

if ($Watch) {
    while ($true) {
        Invoke-DevBuild
        Wait-ForSourceChange
    }
}

Invoke-DevBuild
[Console]::Error.WriteLine("[vl-mcp-dev] starting $Exe")
& $Exe
exit $LASTEXITCODE
