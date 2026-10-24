$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$installer = Get-ChildItem "$root/artifacts/RunpadPro-Setup-*-win-x64.exe" | Select-Object -First 1
$destination = Join-Path $env:ProgramFiles 'RunpadPro-CI'
$installLog = Join-Path $env:TEMP 'runpad-installer-ci.log'
$process = Start-Process $installer.FullName -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/DIR=`"$destination`"",'/TASKS=',"/LOG=`"$installLog`"") -PassThru -Wait
if ($process.ExitCode -ne 0) { throw 'Installer failed.' }
$cli = "$destination/app/cli/Runpad.Cli.exe"
if (!(Test-Path $cli)) { Get-Content $installLog -Tail 60 | Out-Host; throw 'Installed CLI missing.' }
$env:RUNPAD_DATA = Join-Path $env:TEMP ('runpad-service-test-' + [Guid]::NewGuid().ToString('N'))
try {
    & $cli service install
    if ($LASTEXITCODE) { throw 'Service installation failed.' }
    $ready = $false
    for ($i=0; $i -lt 10; $i++) {
        $status = & $cli service status 2>$null | Out-String
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (!$ready) { throw 'Service pipe did not become ready.' }
    if (($status | ConvertFrom-Json).service -ne 'RunpadPro') { throw 'Unexpected service status.' }
    Write-Host 'PASS: silent installer, Windows service startup and administrator pipe authentication.'
} finally {
    & $cli service uninstall | Out-Host
    $uninstall = Start-Process "$destination/unins000.exe" -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw 'Uninstaller failed.' }
    Remove-Item Env:RUNPAD_DATA -ErrorAction SilentlyContinue
}
$global:LASTEXITCODE = 0
