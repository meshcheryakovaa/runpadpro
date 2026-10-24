param([string]$Version = '0.1.0', [switch]$SkipInstaller)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts'
$stage = Join-Path $output 'client'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item "$stage/app" -ItemType Directory -Force | Out-Null
function Publish([string]$project, [string]$destination, [bool]$selfContained = $true) {
    dotnet publish $project -c Release -r win-x64 --self-contained $selfContained.ToString().ToLowerInvariant() --disable-build-servers -m:1 -p:Version=$Version -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $destination
    if ($LASTEXITCODE) { throw "Publish failed: $project" }
}
Push-Location $root
try {
    foreach ($name in @('Cli','Agent','Shell','Service','ModuleHost','Updater','Server')) {
        Publish "src/Runpad.$name/Runpad.$name.csproj" "$stage/app/$($name.ToLowerInvariant())"
    }
    foreach ($module in @(@{project='Monitoring';id='monitoring'}, @{project='Wallpaper';id='wallpaper'}, @{project='YandexDisk';id='yandex-disk'})) {
        $project = "modules/Runpad.Modules.$($module.project)"
        $destination = "$stage/app/modules/$($module.id)"
        Publish "$project/Runpad.Modules.$($module.project).csproj" $destination $false
        Copy-Item "$project/module.json" "$destination/module.json"
        $manifest = Get-Content "$destination/module.json" -Raw | ConvertFrom-Json
        $manifest.version = $Version
        $manifest | ConvertTo-Json -Depth 10 | Set-Content "$destination/module.json" -Encoding utf8NoBOM
        if (Test-Path "$project/wallpaper.html") { Copy-Item "$project/wallpaper.html" $destination }
        'ready' | Set-Content "$destination/module.ready" -Encoding ascii
        Compress-Archive -Path "$destination/*" -DestinationPath "$output/RunpadPro-Module-$($module.id).zip" -Force
    }
    Copy-Item "$root/packaging/runpad.cmd", "$root/packaging/RunpadPro.vbs" $stage
    New-Item "$stage/app/tools" -ItemType Directory -Force | Out-Null
    Copy-Item "$root/scripts/Set-RunpadShell.ps1" "$stage/app/tools/"
    Copy-Item "$root/README.md" "$stage/README.md"
    @{ product='runpadpro-client'; version=$Version } | ConvertTo-Json | Set-Content "$stage/app/version.json" -Encoding utf8NoBOM
    $files = [ordered]@{}
    Get-ChildItem $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\','/')
        $files[$relative] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    }
    @{ product='runpadpro-client'; version=$Version; files=$files } | ConvertTo-Json -Depth 10 | Set-Content "$stage/package.json" -Encoding utf8NoBOM
    Compress-Archive -Path "$stage/*" -DestinationPath "$output/RunpadPro-Client-win-x64.zip" -Force
    Compress-Archive -Path "$stage/app/server/*" -DestinationPath "$output/RunpadPro-Server-win-x64.zip" -Force
    if (!$SkipInstaller) {
        $iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if (!$iscc) {
            $path = "${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe"
            if (!(Test-Path $path)) { throw 'Install Inno Setup 6 or pass -SkipInstaller.' }
            $compiler = $path
        } else { $compiler = $iscc.Source }
        & $compiler "/DAppVersion=$Version" "/DSourceDir=$stage" "/DOutputDir=$output" "$root/packaging/client.iss"
        if ($LASTEXITCODE) { throw 'Installer build failed' }
    }
    Get-ChildItem $output -File | Where-Object { $_.Extension -in '.zip','.exe' } | ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)" | Set-Content "$($_.FullName).sha256" -Encoding ascii
    }
} finally { Pop-Location }
