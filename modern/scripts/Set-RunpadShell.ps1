param([Parameter(Mandatory=$true)][string]$User, [switch]$Disable)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (!([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run PowerShell as administrator.' }
$sid = ([Security.Principal.NTAccount]$User).Translate([Security.Principal.SecurityIdentifier]).Value
if ($sid -eq $identity.User.Value) { throw 'Specify a separate standard student account; do not replace the current administrator shell.' }
$feature = Get-WindowsOptionalFeature -Online -FeatureName Client-EmbeddedShellLauncher
if ($feature.State -ne 'Enabled') {
    if ($Disable) { return }
    Enable-WindowsOptionalFeature -Online -FeatureName Client-EmbeddedShellLauncher -All -NoRestart | Out-Null
    throw 'Shell Launcher enabled. Restart Windows, then run this command again. Requires Enterprise/Education/IoT Enterprise.'
}
$ns = 'root/standardcimv2/embedded'
$result = if ($Disable) {
    Invoke-CimMethod -Namespace $ns -ClassName WESL_UserSetting -MethodName RemoveCustomShell -Arguments @{Sid=$sid}
} else {
    $shell = Join-Path (Split-Path $PSScriptRoot -Parent) 'shell/Runpad.Shell.exe'
    if (!(Test-Path $shell)) { throw 'Run this script from the installed app/tools directory.' }
    $backup = Join-Path $env:ProgramData 'RunpadPro/shell-backup.json'
    if (!(Test-Path $backup)) {
        New-Item (Split-Path $backup -Parent) -ItemType Directory -Force | Out-Null
        Get-CimInstance -Namespace $ns -ClassName WESL_UserSetting | ConvertTo-Json -Depth 8 | Set-Content $backup
    }
    $default = Invoke-CimMethod -Namespace $ns -ClassName WESL_UserSetting -MethodName SetDefaultShell -Arguments @{Shell='explorer.exe';DefaultAction=[uint32]0}
    if ($default.ReturnValue -ne 0) { throw "SetDefaultShell failed: $($default.ReturnValue)" }
    $set = Invoke-CimMethod -Namespace $ns -ClassName WESL_UserSetting -MethodName SetCustomShell -Arguments @{Sid=$sid;Shell="`"$shell`"";DefaultAction=[uint32]0}
    if ($set.ReturnValue -ne 0) { throw "SetCustomShell failed: $($set.ReturnValue)" }
    Invoke-CimMethod -Namespace $ns -ClassName WESL_UserSetting -MethodName SetEnabled -Arguments @{Enabled=$true}
}
if ($result.ReturnValue -ne 0) { throw "Shell Launcher failed: $($result.ReturnValue)" }
Write-Host 'Saved. The change takes effect at the next sign-in of the specified user. Explorer remains the default for other accounts.'
