# CI-compatible local HTTP smoke test. Always starts with simulation; never accesses a GPU.
[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
Push-Location (Join-Path $PSScriptRoot "..")
$web = $null
try {
  $assembly = (Resolve-Path "src/AmdTune.Web/bin/Release/net10.0/AmdTune.Web.dll").Path
  $web = Start-Process dotnet -ArgumentList ('"' + $assembly + '"') -PassThru
  $base = "http://127.0.0.1:5783"
  $info = $null
  for ($i = 0; $i -lt 40; $i++) {
    try { $info = Invoke-RestMethod "$base/api/info"; break }
    catch { Start-Sleep -Milliseconds 300 }
  }
  if ($null -eq $info) { throw "Web did not start on localhost." }
  if ($info.mode -ne "simulation") { throw "Unsafe mode: simulation required." }
  $gpu = Invoke-RestMethod "$base/api/discover"
  $original = Invoke-RestMethod "$base/api/settings"
  if (!$gpu.isSimulation) { throw "Simulation reported a physical GPU." }
  $request = @{
    candidates = @(@{
      voltageOffsetMv = ($original.voltageOffsetMv - 15)
      memoryClockMhz = $original.memoryClockMhz
      memoryTiming = $original.memoryTiming
    })
    repetitions = 3
  } | ConvertTo-Json -Depth 8
  $headers = @{ "Origin" = $base; "X-AmdTune-CSRF" = $info.csrfToken }
  $null = Invoke-RestMethod -Method Post -Uri "$base/api/experiment" -Headers $headers -ContentType "application/json" -Body $request
  $status = $null
  for ($i = 0; $i -lt 100; $i++) {
    $status = Invoke-RestMethod "$base/api/experiment"
    if (!$status.running) { break }
    Start-Sleep -Milliseconds 200
  }
  if ($null -eq $status.report -or $status.report.phase -ne 7) { throw "Expected completed experiment (RunPhase.Completed=7)." }
  if (!$status.report.rollbackVerified -or !$status.report.simulated) { throw "Result missing simulation/rollback verification." }
  $after = Invoke-RestMethod "$base/api/settings"
  if ($after.voltageOffsetMv -ne $original.voltageOffsetMv -or $after.memoryClockMhz -ne $original.memoryClockMhz) {
    throw "Simulation settings were not rolled back."
  }
  Write-Host "PASS: local API, discovery, CSRF, A/B/A experiment and verified simulation rollback"
} finally {
  if ($null -ne $web) { Stop-Process -Id $web.Id -Force -ErrorAction SilentlyContinue }
  Pop-Location
}
