[CmdletBinding()]
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
Push-Location (Join-Path $PSScriptRoot "..")
try {
  dotnet publish src/AmdTune.Web/AmdTune.Web.csproj -c $Configuration -r win-x64 --self-contained false -o artifacts/app
  if ($LASTEXITCODE -ne 0) { throw "Web build failed." }
  dotnet publish src/AmdTune.Recovery/AmdTune.Recovery.csproj -c $Configuration -r win-x64 --self-contained false -p:PublishSingleFile=true -o artifacts/recovery
  if ($LASTEXITCODE -ne 0) { throw "Recovery build failed." }
  $recoveryExe = (Resolve-Path "artifacts/recovery/AmdTune.Recovery.exe").Path
  & $recoveryExe --self-check
  if ($LASTEXITCODE -ne 0) { throw "Independent recovery executable failed no-write runtime self-check." }
  Copy-Item $recoveryExe artifacts/app/ -Force
  $appRecoveryExe = (Resolve-Path "artifacts/app/AmdTune.Recovery.exe").Path
  & $appRecoveryExe --self-check
  if ($LASTEXITCODE -ne 0) { throw "Independent recovery executable failed after copying into the app directory." }
  $sourceBridge = "artifacts/native/AmdTune.AdlxBridge.exe"
  $appBridge = "artifacts/app/AmdTune.AdlxBridge.exe"
  if (Test-Path $sourceBridge) {
    Copy-Item $sourceBridge $appBridge -Force
    Write-Host "Native ADLX bridge copied from local build."
  } elseif (Test-Path $appBridge) {
    Write-Host "Using existing native ADLX bridge from application directory (no local C++ build required)."
  } else {
    Write-Warning "Native ADLX bridge missing. Simulation mode works; hardware mode fails closed."
  }
  foreach ($asset in @("wwwroot/index.html", "wwwroot/app.js", "wwwroot/styles.css")) {
    if (!(Test-Path (Join-Path "artifacts/app" $asset))) {
      throw "Published UI asset missing: $asset. Refusing to present an incomplete app as ready."
    }
  }
  Write-Host "Published web assets verified (index.html, app.js, styles.css)."
  Write-Host "Simulation: artifacts/app/AmdTune.Web.exe"
  Write-Host "Hardware discovery: artifacts/app/AmdTune.Web.exe --hardware"
  Write-Host "Physical writes (only after hardware checklist): artifacts/app/AmdTune.Web.exe --hardware --enable-hardware-writes"
} finally { Pop-Location }
