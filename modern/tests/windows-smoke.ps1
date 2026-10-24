$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$package = Join-Path $root 'artifacts/client'
$env:RUNPAD_DATA = Join-Path $env:TEMP ('runpad-smoke-' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $env:TEMP ('runpad-install-' + [Guid]::NewGuid().ToString('N'))
New-Item $install -ItemType Directory -Force | Out-Null
Copy-Item "$package/*" $install -Recurse -Force
$cli = "$install/app/cli/Runpad.Cli.exe"
function Invoke-Runpad([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments) {
    $text = & $cli @Arguments
    if ($LASTEXITCODE) { throw "CLI failed: $Arguments`n$text" }
    return $text
}
try {
    Invoke-Runpad @('doctor','--offline') | Out-Host
    Invoke-Runpad @('config','set','classroom','CI') | Out-Null
    $agent = Start-Process "$install/app/agent/Runpad.Agent.exe" -PassThru
    $ready = $false
    for ($i=0; $i -lt 40; $i++) {
        try {
            $status = (& $cli status 2>$null | Out-String) | ConvertFrom-Json
            if ($status.modules.Count -ge 3) { $ready = $true; break }
        } catch {}
        Start-Sleep -Milliseconds 500
    }
    if (!$ready) { throw 'Agent or builtin modules did not become ready.' }
    if (@($status.modules | Where-Object state -eq 'running').Count -ne 3) { throw ($status | ConvertTo-Json -Depth 20) }
    Invoke-Runpad @('module','disable','wallpaper') | Out-Null
    $disabled = (Invoke-Runpad @('status') | Out-String | ConvertFrom-Json).modules | Where-Object id -eq 'wallpaper'
    if ($disabled.state -ne 'disabled') { throw 'Module did not stop.' }
    Invoke-Runpad @('module','enable','wallpaper') | Out-Null
    $enabled = (Invoke-Runpad @('status') | Out-String | ConvertFrom-Json).modules | Where-Object id -eq 'wallpaper'
    if ($enabled.state -ne 'running') { throw 'Module did not restart.' }
    dotnet publish "$root/examples/hello/HelloModule.csproj" -c Release -o "$env:TEMP/runpad-hello-ci"
    if ($LASTEXITCODE) { throw 'Example publish failed.' }
    Copy-Item "$root/examples/hello/module.json" "$env:TEMP/runpad-hello-ci/"
    Invoke-Runpad @('module','install',"$env:TEMP/runpad-hello-ci") | Out-Host
    $hello = Invoke-Runpad @('module','invoke','hello','snapshot') | Out-String | ConvertFrom-Json
    if ($hello.message -notmatch 'Модуль работает') { throw 'Custom module did not execute.' }
    Invoke-Runpad @('agent','stop') | Out-Null
    $agent.WaitForExit(20000) | Out-Null
    if (!$agent.HasExited) { throw 'Agent did not exit.' }
    # Full update transaction and rollback with a complete locally verified package.
    $candidate = Join-Path $install ('.next-' + [Guid]::NewGuid().ToString('N'))
    New-Item $candidate -ItemType Directory | Out-Null
    Copy-Item "$package/*" $candidate -Recurse -Force
    $updater = Join-Path $env:TEMP ('runpad-updater-ci-' + [Guid]::NewGuid().ToString('N'))
    Copy-Item "$install/app/updater" $updater -Recurse -Force
    & "$updater/Runpad.Updater.exe" --root $install --candidate $candidate
    if ($LASTEXITCODE) { throw 'Update failed.' }
    if (!(Get-Content "$install/update-result.json" -Raw | ConvertFrom-Json).ok) { throw 'Update journal is not successful.' }
    if (!(Test-Path "$install/previous-app")) { throw 'Rollback image missing.' }
    & "$updater/Runpad.Updater.exe" --root $install --rollback
    if ($LASTEXITCODE) { throw 'Rollback failed.' }
    Invoke-Runpad @('doctor','--offline') | Out-Host
    Write-Host 'PASS: packaged CLI, agent, process modules, external module, enable/disable, updater and rollback.'
} finally {
    & $cli agent stop 2>$null | Out-Null
    Get-Process -Name 'Runpad.Agent','Runpad.ModuleHost' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$install/*" } | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item Env:RUNPAD_DATA -ErrorAction SilentlyContinue
}
