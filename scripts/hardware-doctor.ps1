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
$telemetryRaw = & $bridge telemetry
if ($LASTEXITCODE -ne 0) { throw "Telemetry read failed."; }
$telemetryRaw | ForEach-Object { Write-Host $_ }
$telemetry = $telemetryRaw | ConvertFrom-Json
$usageSchema = $telemetry.PSObject.Properties.Name -contains 'utilizationPercent'
Write-Host "Native bridge GPU utilization telemetry schema: $usageSchema"
if (-not $usageSchema) {
  Write-Warning "The native ADLX bridge is older than the current source (missing utilizationPercent). Local dotnet publish preserves the existing C++ bridge; it does not rebuild it. Download the native bridge from a successful GitHub Actions run for the current commit, extract the complete development bundle or copy the bridge into artifacts/native, then republish. DO NOT download unknown third-party binaries."
} elseif ($null -eq $telemetry.utilizationPercent) {
  Write-Warning "GPU utilization metric is unavailable from ADLX; active-load power/efficiency must remain unverified."
}
# A telemetry JSON schema is not evidence the native binary supports the new
# persistent-stream command. Detect this capability independently.
$streamCapabilities = & $bridge telemetry-stream-capabilities 2>$null
if ($LASTEXITCODE -eq 0) {
  Write-Host "Native read-only ADLX telemetry streaming: $streamCapabilities"
} else {
  Write-Warning "This native ADLX bridge does not support telemetry-stream; fetch the new amd-adlx-bridge-unsigned-dev artifact from the latest successful CI run. Do not run the sustained verification with an old bridge."
}
Write-Host "Read-only checks do not establish factory-default tuning. Compare these current driver settings with your intended baseline before any performance experiment."
Write-Host "Do not enable writes until driver ranges, GPU identity and guard are verified."
