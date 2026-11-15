[CmdletBinding()]
param([string]$AppDirectory = "artifacts/app")
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$dir = Join-Path $root $AppDirectory
$bridge = Join-Path $dir "AmdTune.AdlxBridge.exe"
$guard = Join-Path $dir "AmdTune.Recovery.exe"
Write-Host "AMD Tune hardware doctor. READ-ONLY, no tuning changes."
Write-Host "OS: $([System.Environment]::OSVersion.VersionString)"
Write-Host "App: $dir"
Write-Host "Native ADLX bridge: $(Test-Path $bridge)"
Write-Host "Separate recovery executable: $(Test-Path $guard)"
if (!(Test-Path $bridge)) { throw "Build native bridge first; see README.md." }
Write-Host "ADLX capability discovery:"
& $bridge discover
if ($LASTEXITCODE -ne 0) { throw "Discovery failed."; }
Write-Host "Read current driver settings:"
& $bridge read
if ($LASTEXITCODE -ne 0) { throw "Settings read failed."; }
Write-Host "Read telemetry:"
& $bridge telemetry
if ($LASTEXITCODE -ne 0) { throw "Telemetry read failed."; }
Write-Host "Do not enable writes until driver ranges, GPU identity and guard are verified."
