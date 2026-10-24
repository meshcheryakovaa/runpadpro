param([string]$Destination = "$env:LOCALAPPDATA/Programs/RunpadPro-Dev")
$ErrorActionPreference = 'Stop'
$stage = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/client'
if (!(Test-Path "$stage/app/cli/Runpad.Cli.exe")) { throw 'Run scripts/package.ps1 -SkipInstaller first.' }
if (Test-Path "$Destination/app") { throw 'Destination exists. Close the development processes and choose a new destination or use runpad update.' }
New-Item $Destination -ItemType Directory -Force | Out-Null
Copy-Item "$stage/*" $Destination -Recurse -Force
Write-Host "Installed: $Destination"
Write-Host "Start: & '$Destination/runpad.cmd' shell"
