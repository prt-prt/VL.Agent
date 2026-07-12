[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string] $PackagePath,

    [string] $PackageRepository = (Join-Path $env:LOCALAPPDATA "vvvv\gamma\nugets"),

    [switch] $Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3

$package = (Resolve-Path $PackagePath).Path
if ([IO.Path]::GetExtension($package) -ne ".nupkg") {
    throw "PackagePath must point to a .nupkg file."
}

$destination = Join-Path $PackageRepository "VL.Agent"
if (Test-Path $destination) {
    if (-not $Force) {
        throw "VL.Agent already exists at '$destination'. Re-run with -Force to replace it."
    }
    Remove-Item $destination -Recurse -Force
}

New-Item -ItemType Directory -Force $PackageRepository | Out-Null
New-Item -ItemType Directory -Force $destination | Out-Null

# Expand-Archive only accepts .zip by extension, while a NuGet package is a ZIP
# with a different suffix. ZipFile works directly and is available in PowerShell 5.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($package, $destination)

$required = @(
    "VL.Agent.vl",
    "VL.Agent.HDE.vl",
    "lib\net8.0-windows7.0\VL.Agent.dll",
    "tools\win-x64\vl-mcp.exe",
    "tools\win-x64\vl-map.exe",
    "tools\win-x64\vl-probe.exe",
    "setup\Start-VL.Agent.ps1"
)

$missing = @($required | Where-Object { -not (Test-Path (Join-Path $destination $_) -PathType Leaf) })
if ($missing.Count -gt 0) {
    Remove-Item $destination -Recurse -Force
    throw "Package is incomplete. Missing: $($missing -join ', ')"
}

$mcpCommand = Join-Path $destination "tools\win-x64\vl-mcp.exe"
$mcpConfig = [ordered]@{
    mcpServers = [ordered]@{
        "vl-agent" = [ordered]@{
            command = $mcpCommand
            args = @()
        }
    }
}
$mcpConfigPath = Join-Path $destination "vl-agent.mcp.json"
$mcpConfig | ConvertTo-Json -Depth 5 | Set-Content $mcpConfigPath -Encoding UTF8

Write-Host "VL.Agent installed successfully."
Write-Host "Package:    $destination"
Write-Host "MCP config: $mcpConfigPath"
Write-Host ""
Write-Host "Start the HDE bridge with:"
Write-Host "  powershell -ExecutionPolicy Bypass -File `"$(Join-Path $destination 'setup\Start-VL.Agent.ps1')`""
