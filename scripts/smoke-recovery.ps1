# Test the *actual packaged recovery executable* and lease handshake without
# intentionally accessing or changing GPU settings. Safe in ordinary PowerShell.
[CmdletBinding()]
param([string]$AppDirectory = "artifacts/app")
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$dir = (Resolve-Path (Join-Path $root $AppDirectory)).Path
$guardExe = Join-Path $dir "AmdTune.Recovery.exe"
$bridgeExe = Join-Path $dir "AmdTune.AdlxBridge.exe"
foreach ($path in @($guardExe, $bridgeExe)) {
  if (!(Test-Path $path)) { throw "Required development bundle executable missing: $path" }
}
& $guardExe --self-check
if ($LASTEXITCODE -ne 0) { throw "Recovery runtime self-check failed: exit code $LASTEXITCODE" }

$diagnosticDir = Join-Path ([IO.Path]::GetTempPath()) ("amd-tune-recovery-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $diagnosticDir | Out-Null
$lease = Join-Path $diagnosticDir "diagnostic-lease.json"
$ready = "$lease.ready"
$done = "$lease.done"
$log = "$lease.recovery.log"
$child = $null
try {
  $payload = @{
    ParentProcessId = $PID
    BridgeExecutable = $bridgeExe
    Baseline = @{ VoltageOffsetMv = 0; MemoryClockMhz = 2518; MemoryTiming = 0 }
    StartedAt = [DateTimeOffset]::UtcNow.ToString("O")
    DiagnosticOnly = $true
  } | ConvertTo-Json -Depth 6
  [IO.File]::WriteAllText($lease, $payload, [Text.Encoding]::UTF8)

  # This is the same --lease entry point used by hardware experiments. The
  # DiagnosticOnly flag uses a deliberately nonfunctional, no-write IGpuDevice.
  $child = Start-Process -FilePath $guardExe -ArgumentList ('--lease "' + $lease + '"') -PassThru -WindowStyle Hidden
  $readyOk = $false
  for ($i = 0; $i -lt 60; $i++) {
    if (Test-Path $ready) { $readyOk = $true; break }
    if ($child.HasExited) { break }
    Start-Sleep -Milliseconds 100
    $child.Refresh()
  }
  if (!$readyOk) {
    $detail = if (Test-Path $log) { Get-Content $log -Raw } else { "No recovery log." }
    throw "Recovery guard did not acknowledge diagnostic lease. $detail"
  }
  [IO.File]::WriteAllText($done, "test-disarm", [Text.Encoding]::UTF8)
  $child.WaitForExit(6000) | Out-Null
  $child.Refresh()
  if (!$child.HasExited -or $child.ExitCode -ne 0) {
    $detail = if (Test-Path $log) { Get-Content $log -Raw } else { "No recovery log." }
    throw "Recovery guard did not disarm successfully. $detail"
  }
  $logText = Get-Content $log -Raw
  if (!$logText.Contains("DIAGNOSTIC ONLY") -or !$logText.Contains("Guard disarmed")) {
    throw "Recovery log missing evidence of diagnostic-only startup and successful disarm."
  }
  Write-Host "RECOVERY_HANDSHAKE_PASS: independent executable started, acknowledged, and disarmed; no GPU writes."
}
finally {
  if (Test-Path $lease) {
    # A late-starting watchdog still sees the disarm signal and cannot
    # write hardware because DiagnosticOnly was included in the lease.
    if (!(Test-Path $done)) { [IO.File]::WriteAllText($done, "test-cleanup", [Text.Encoding]::UTF8) }
  }
  if ($null -ne $child) {
    try {
      if (!$child.HasExited) {
        $child.WaitForExit(2500) | Out-Null
        if (!$child.HasExited) { Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue }
      }
    } finally { $child.Dispose() }
  }
  Remove-Item -LiteralPath $diagnosticDir -Recurse -Force -ErrorAction SilentlyContinue
}
