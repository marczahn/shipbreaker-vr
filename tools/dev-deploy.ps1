<#
Builds ShipbreakerVR into BepInEx\dev, where the dev plugin reloads it while the game runs (docs/TESTING.md).

  tools\dev-deploy.ps1 -GameDir "F:\...\Hardspace Shipbreaker" -Setup    # once: install the dev plugin, park the regular DLL
  tools\dev-deploy.ps1 -GameDir "F:\...\Hardspace Shipbreaker"           # after each change
  tools\dev-deploy.ps1 -GameDir "F:\...\Hardspace Shipbreaker" -Restore  # back to the regular install

Works with Windows PowerShell 5.1 and PowerShell 7.
#>
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [switch]$Setup,
    [switch]$Restore
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$plugins = Join-Path $GameDir "BepInEx\plugins"
$installed = Join-Path $plugins "ShipbreakerVR\ShipbreakerVR.dll"
$parked = "$installed.dev-parked"
$devPlugin = Join-Path $plugins "ShipbreakerVR.Dev"
$devFolder = Join-Path $GameDir "BepInEx\dev"

function Build($project, $out) {
    dotnet build (Join-Path $root $project) -c Debug -nologo -v q "-p:GameDir=$GameDir" -o $out
    if ($LASTEXITCODE -ne 0) { throw "Build of $project failed" }
}

if ($Restore) {
    if (Test-Path $parked) { Move-Item -Force $parked $installed }
    if (Test-Path $devPlugin) { Remove-Item -Recurse -Force $devPlugin }
    if (Test-Path $devFolder) { Remove-Item -Recurse -Force $devFolder }
    Write-Host "Regular install restored (rebuild and copy ShipbreakerVR.dll if you want your latest changes in it)."
    return
}

if ($Setup) {
    Build "src\ShipbreakerVR.Dev" $devPlugin
    # The chainloader only loads *.dll; a plugin both in plugins and in dev would be patched twice.
    if (Test-Path $installed) {
        Move-Item -Force $installed $parked
        Write-Host "Parked $installed"
    }
}

if (Test-Path $installed) { throw "$installed is still installed; run once with -Setup" }
Build "src\ShipbreakerVR" $devFolder
Write-Host "Built into $devFolder; a running game reloads it within a second or two (or press F6)."
