[CmdletBinding()]
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
Push-Location (Join-Path $PSScriptRoot "..")
try {
  dotnet publish src/AmdTune.Web/AmdTune.Web.csproj -c $Configuration -r win-x64 --self-contained false -o artifacts/app
  if ($LASTEXITCODE -ne 0) { throw "Web build failed." }
  dotnet publish src/AmdTune.Recovery/AmdTune.Recovery.csproj -c $Configuration -r win-x64 --self-contained false -o artifacts/recovery
  if ($LASTEXITCODE -ne 0) { throw "Recovery build failed." }
  Copy-Item artifacts/recovery/AmdTune.Recovery.exe artifacts/app/ -Force
  if (Test-Path artifacts/native/AmdTune.AdlxBridge.exe) {
    Copy-Item artifacts/native/AmdTune.AdlxBridge.exe artifacts/app/ -Force
  } else {
    Write-Warning "Native ADLX bridge not built. Simulation mode works; hardware mode fails closed."
  }
  Write-Host "Simulation: artifacts/app/AmdTune.Web.exe"
  Write-Host "Hardware discovery: artifacts/app/AmdTune.Web.exe --hardware"
  Write-Host "Physical writes (only after hardware checklist): artifacts/app/AmdTune.Web.exe --hardware --enable-hardware-writes"
} finally { Pop-Location }
