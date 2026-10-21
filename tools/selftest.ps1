<#
Starts the game with the in-game self-test and waits for its result (docs/TESTING.md). Needs the dev plugin
(tools\dev-deploy.ps1 -Setup). Exit code 0 when no test failed.

  tools\selftest.ps1 -GameDir "F:\...\Hardspace Shipbreaker"                      # all tests, waits for a loaded shift
  tools\selftest.ps1 -GameDir "..." -Tests xr,stereo,options -NoWait              # only these, right in the main menu
  tools\selftest.ps1 -GameDir "..." -XrRuntimeJson "C:\...\meta_openxr_simulator.json"  # without a headset

Works with Windows PowerShell 5.1 and PowerShell 7.
#>
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    # Comma-separated test names or prefixes (e.g. "aim" or "xr.start,options"); all tests if empty.
    [string]$Tests = "",
    # Run right away instead of waiting for gameplay; tests that need gameplay are skipped.
    [switch]$NoWait,
    # Leave the game running afterwards.
    [switch]$KeepRunning,
    # OpenXR runtime for this run only (sets XR_RUNTIME_JSON), e.g. an XR simulator.
    [string]$XrRuntimeJson = "",
    [int]$TimeoutMinutes = 15
)
$ErrorActionPreference = "Stop"
$exe = Join-Path $GameDir "Shipbreaker.exe"
$results = Join-Path $GameDir "BepInEx\selftest\results.txt"
if (-not (Test-Path $exe)) { throw "$exe not found" }
if (-not (Test-Path (Join-Path $GameDir "BepInEx\plugins\ShipbreakerVR.Dev\ShipbreakerVR.Dev.dll"))) {
    throw "Dev plugin missing; run tools\dev-deploy.ps1 -GameDir `"$GameDir`" -Setup first"
}

$arguments = @(if ($Tests) { "--vr-selftest=$Tests" } else { "--vr-selftest" })
if (-not $KeepRunning) { $arguments += "--vr-selftest-quit" }
if ($NoWait) { $arguments += "--vr-selftest-nowait" }
if ($XrRuntimeJson) { $env:XR_RUNTIME_JSON = (Resolve-Path $XrRuntimeJson).Path }

if (Test-Path $results) { Remove-Item -Force $results }
$started = Get-Date
Write-Host "Starting $exe $arguments"
if (-not $NoWait) { Write-Host "Load a shift in the game; the tests start once gameplay is running." }
Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $GameDir | Out-Null

# Waits for the result file rather than the process: if Steam restarts the game, the started process ends early.
$deadline = $started.AddMinutes($TimeoutMinutes)
$shown = 0
$done = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    if (-not (Test-Path $results)) { continue }
    $lines = @(Get-Content $results)
    for (; $shown -lt $lines.Count; $shown++) {
        $line = $lines[$shown]
        $color = switch -Wildcard ($line) { "PASS*" { "Green" } "FAIL*" { "Red" } "SKIP*" { "DarkYellow" } default { "Gray" } }
        Write-Host $line -ForegroundColor $color
    }
    if ($lines | Where-Object { $_ -like "DONE*" }) { $done = $true; break }
}

if (-not $done) {
    Write-Host "No result after $TimeoutMinutes minutes; see BepInEx\LogOutput.log" -ForegroundColor Red
    exit 2
}
$failed = @(Get-Content $results | Where-Object { $_ -like "FAIL*" }).Count
exit $(if ($failed -gt 0) { 1 } else { 0 })
