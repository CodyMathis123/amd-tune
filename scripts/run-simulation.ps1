[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
Push-Location (Join-Path $PSScriptRoot "..")
try {
  dotnet run --project src/AmdTune.Web/AmdTune.Web.csproj
  if ($LASTEXITCODE -ne 0) { throw "Simulation exited with $LASTEXITCODE" }
} finally { Pop-Location }
