# Run from a Visual Studio Developer PowerShell (x64) with MSVC tools installed.
# AMD ADLX SDK is acquired separately under its own license. No SDK redistribution.
[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$AdlxSdkDirectory,
  [string]$OutputDirectory = "artifacts/native"
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$sdk = (Resolve-Path $AdlxSdkDirectory).Path
$expectedSdkRevision = "32b5a740d42295c5dfe9026b9f52683da0f3af91"
$actualSdkRevision = (& git -C $sdk rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or $actualSdkRevision.Trim() -ne $expectedSdkRevision) {
  throw "AMD ADLX SDK revision mismatch. Run: git -C $sdk checkout --detach $expectedSdkRevision"
}
$helper = Join-Path $sdk "SDK/ADLXHelper/Windows/Cpp/ADLXHelper.cpp"
$platform = Join-Path $sdk "SDK/Platform/Windows/WinAPIs.cpp"
$header = Join-Path $sdk "SDK/Include/IGPUTuning.h"
foreach ($file in @($helper, $platform, $header)) {
  if (!(Test-Path $file)) { throw "AMD SDK file missing: $file" }
}
if (!(Get-Command cl.exe -ErrorAction SilentlyContinue)) {
  throw "cl.exe not found. Start Visual Studio Developer PowerShell (x64)."
}
Push-Location $root
try {
  New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
  $output = (Resolve-Path $OutputDirectory).Path
  & cl.exe /nologo /std:c++17 /EHsc /O2 /W4 /I "$sdk" "native/AmdTune.AdlxBridge/main.cpp" "$helper" "$platform" "/Fe:$output/AmdTune.AdlxBridge.exe"
  if ($LASTEXITCODE -ne 0) { throw "Native compilation failed: $LASTEXITCODE" }
  Write-Host "Native ADLX bridge built at $output/AmdTune.AdlxBridge.exe"
} finally { Pop-Location }
