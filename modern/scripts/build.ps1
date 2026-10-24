param([string]$Configuration = 'Release', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet restore RunpadPro.slnx
    if ($LASTEXITCODE) { throw 'Restore failed' }
    dotnet build RunpadPro.slnx -c $Configuration --no-restore
    if ($LASTEXITCODE) { throw 'Build failed' }
    if (!$SkipTests) {
        dotnet test tests/Runpad.Tests/Runpad.Tests.csproj -c $Configuration --no-build --logger 'trx;LogFileName=tests.trx'
        if ($LASTEXITCODE) { throw 'Tests failed' }
    }
} finally { Pop-Location }
