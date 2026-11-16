[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)]
  [long]$RunId
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (!(Get-Command gh -ErrorAction SilentlyContinue)) {
  throw 'GitHub CLI (gh) is required for this helper. You can also download the native artifact manually.'
}
# Always download to a unique empty directory: gh run download intentionally
# refuses to extract an artifact over an existing filename.
$temp = Join-Path ([IO.Path]::GetTempPath()) ("amd-tune-bridge-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $temp | Out-Null
try {
  & gh run download $RunId -R CodyMathis123/amd-tune -n amd-adlx-bridge-unsigned-dev -D $temp
  if ($LASTEXITCODE -ne 0) { throw "Could not download artifact from GitHub Actions run $RunId." }
  $source = Join-Path $temp 'AmdTune.AdlxBridge.exe'
  if (!(Test-Path $source)) { throw "Expected native bridge missing from downloaded artifact." }
  $raw = & $source telemetry-stream-capabilities
  if ($LASTEXITCODE -ne 0) {
    throw 'Downloaded native bridge does not support the new telemetry-stream command.'
  }
  $caps = $raw | ConvertFrom-Json
  if ($caps.supported -ne $true -or $caps.minIntervalMs -ne 100 -or $caps.maxIntervalMs -ne 1000) {
    throw 'Downloaded native bridge has an unexpected telemetry stream protocol.'
  }
  $native = Join-Path $root 'artifacts/native'
  New-Item -ItemType Directory -Force $native | Out-Null
  $target = Join-Path $native 'AmdTune.AdlxBridge.exe'
  Copy-Item -LiteralPath $source -Destination $target -Force
  Write-Host "Updated native bridge from verified run $RunId"
  Write-Host "Native SHA256: $((Get-FileHash $target -Algorithm SHA256).Hash)"
  Write-Host 'Next: .\scripts\publish-windows.ps1 then .\scripts\hardware-doctor.ps1'
}
finally {
  Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
