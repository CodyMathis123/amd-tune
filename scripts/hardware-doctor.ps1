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
$uiAssets = @("wwwroot/index.html", "wwwroot/app.js", "wwwroot/styles.css")
$missingAssets = @($uiAssets | Where-Object { !(Test-Path (Join-Path $dir $_)) })
Write-Host "Published UI assets complete: $($missingAssets.Count -eq 0)"
if ($missingAssets.Count -gt 0) {
  Write-Warning ("Web dashboard may return HTTP 404. Missing: " + ($missingAssets -join ", "))
}
if (!(Test-Path $bridge)) { throw "Native bridge missing. Run build-native.ps1 -AdlxSdkDirectory ./third_party/ADLX, then publish-windows.ps1, then retry doctor." }
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
