# Compile official AMD ADLX from a pinned SDK; no device writes.
# This works from ordinary PowerShell when the MSVC C++ workload is installed.
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
  throw "AMD ADLX SDK revision mismatch. Checkout pinned revision $expectedSdkRevision."
}
$helper = Join-Path $sdk "SDK/ADLXHelper/Windows/Cpp/ADLXHelper.cpp"
$platform = Join-Path $sdk "SDK/Platform/Windows/WinAPIs.cpp"
$header = Join-Path $sdk "SDK/Include/IGPUTuning.h"
foreach ($file in @($helper, $platform, $header)) {
  if (!(Test-Path $file)) { throw "AMD SDK file missing: $file" }
}

# Merely locating cl.exe on PATH is insufficient; MSVC also requires INCLUDE and LIB.
$compiler = Get-Command cl.exe -ErrorAction SilentlyContinue
if (!$compiler -or !$env:INCLUDE -or !$env:LIB) {
  $x86ProgramFiles = [Environment]::GetFolderPath("ProgramFilesX86")
  $vswhere = Join-Path $x86ProgramFiles "Microsoft Visual Studio\Installer\vswhere.exe"
  if (!(Test-Path $vswhere)) {
    throw "MSVC not initialized and vswhere.exe is missing. Install Visual Studio Build Tools with Desktop development with C++ (MSVC x64 and Windows SDK)."
  }
  $installPath = @(& $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath) |
    Where-Object { $_ -and $_.Trim() } |
    Select-Object -First 1
  if (!$installPath) {
    throw "MSVC C++ toolset not found. Open Visual Studio Installer > Modify > Desktop development with C++ (MSVC x64 and Windows SDK)."
  }
  $devShell = Join-Path $installPath "Common7\Tools\Launch-VsDevShell.ps1"
  if (!(Test-Path $devShell)) {
    throw "MSVC found, but Developer PowerShell launcher is missing: $devShell"
  }
  Write-Host "Initializing x64 MSVC toolchain: $installPath"
  & $devShell -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
}
$compiler = Get-Command cl.exe -ErrorAction SilentlyContinue
$linker = Get-Command link.exe -ErrorAction SilentlyContinue
if (!$compiler -or !$linker -or !$env:INCLUDE -or !$env:LIB) {
  throw "MSVC environment is incomplete; cl.exe, link.exe, INCLUDE, and LIB are required. Check Desktop development with C++ in Visual Studio Installer."
}
Push-Location $root
try {
  New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
  $output = (Resolve-Path $OutputDirectory).Path
  & cl.exe /nologo /std:c++17 /EHsc /O2 /W4 /I "$sdk" "native/AmdTune.AdlxBridge/main.cpp" "$helper" "$platform" "/Fe:$output/AmdTune.AdlxBridge.exe"
  if ($LASTEXITCODE -ne 0) { throw "Native compilation failed, exit $LASTEXITCODE" }
  $binary = Join-Path $output "AmdTune.AdlxBridge.exe"
  if (!(Test-Path $binary)) { throw "Compiler exited successfully but output is missing: $binary" }
  Write-Host "Native ADLX bridge built at $binary"
} finally { Pop-Location }
