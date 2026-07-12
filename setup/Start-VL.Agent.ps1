[CmdletBinding()]
param(
    [string] $VvvvPath,
    [switch] $Preview,
    [switch] $PassThru
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3
$packageRoot = Split-Path $PSScriptRoot -Parent
$repository = Split-Path $packageRoot -Parent
$extension = Join-Path $packageRoot "VL.Agent.HDE.vl"

if (-not (Test-Path $extension -PathType Leaf)) {
    throw "VL.Agent.HDE.vl was not found at '$extension'."
}

if ([string]::IsNullOrWhiteSpace($VvvvPath)) {
    $root = Join-Path $env:ProgramFiles "vvvv"
    if (-not (Test-Path $root -PathType Container)) {
        throw "No vvvv installation found under '$root'. Pass -VvvvPath explicitly."
    }

    $pattern = if ($Preview) { "vvvv_gamma-preview*-win-x64" } else { "vvvv_gamma_[0-9]*-win-x64" }
    $installation = Get-ChildItem $root -Directory -Filter $pattern |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if ($null -eq $installation) {
        throw "No matching vvvv gamma installation found under '$root'. Pass -VvvvPath explicitly."
    }
    $VvvvPath = Join-Path $installation.FullName "vvvv.exe"
}

$VvvvPath = (Resolve-Path $VvvvPath).Path
$arguments = @(
    "--package-repositories", "`"$repository`"",
    "--editable-packages", "`"VL.Agent*`"",
    "-o", "`"$extension`""
)

Write-Host "Starting $VvvvPath"
Write-Host "VL.Agent package repository: $repository"
$process = Start-Process -FilePath $VvvvPath -ArgumentList $arguments -PassThru
if ($PassThru) { $process }
