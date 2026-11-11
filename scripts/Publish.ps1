[CmdletBinding()]
param([switch]$IncludeRuntime, [switch]$IncludeModel, [string]$CacheHome, [switch]$SkipNativeBuild, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$destination = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $workspace 'artifacts/Stemora' }
Push-Location $workspace
try {
    New-Item -ItemType Directory -Force $destination | Out-Null
    if(-not $SkipNativeBuild) {
        & "$PSScriptRoot/Build-Native.ps1"
        if($LASTEXITCODE -ne 0) {throw 'Native infrastructure build failed.'}
    }
    $nativeDestination=[IO.Path]::GetFullPath((Join-Path $destination 'native'))
    if(-not $nativeDestination.StartsWith([IO.Path]::GetFullPath($destination)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'Invalid native publication path.'}
    if(Test-Path $nativeDestination) {Remove-Item -LiteralPath $nativeDestination -Recurse -Force}
    New-Item -ItemType Directory -Force $nativeDestination | Out-Null
    Get-ChildItem -LiteralPath "$workspace/artifacts/native" -File | Where-Object {
        $_.Extension -in @('.dll','.exe','.json','.txt','.manifest') -and $_.Name -notlike '*test*'
    } | Copy-Item -Destination "$destination/native" -Force
    dotnet publish src/Stemora.Cli -c Release -r win-x64 --self-contained true -o "$destination/cli"
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
    dotnet publish src/Stemora.App -c Release -p:Platform=x64 -r win-x64 --self-contained true -o $destination
    if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
    foreach ($resource in @('App.xbf', 'MainWindow.xbf')) {
        if (-not (Test-Path (Join-Path $destination $resource))) { throw "Published WinUI resource missing: $resource" }
    }
    New-Item -ItemType File -Force "$destination/portable.flag" | Out-Null
    Copy-Item -LiteralPath README.md,THIRD-PARTY-NOTICES.md -Destination $destination
    New-Item -ItemType Directory -Force "$destination/docs" | Out-Null
    Copy-Item -LiteralPath docs/UI-DESIGN.md -Destination "$destination/docs/UI-DESIGN.md"
    Copy-Item -LiteralPath docs/NATIVE-INFRA.md -Destination "$destination/docs/NATIVE-INFRA.md"
    Copy-Item -LiteralPath docs/RELEASING.md -Destination "$destination/docs/RELEASING.md"
    New-Item -ItemType Directory -Force "$destination/licenses" | Out-Null
    Copy-Item -Path 'licenses/*' -Destination "$destination/licenses" -Force
    $cli = Join-Path $destination 'cli/stemora.exe'
    if ($CacheHome) {
        if ($IncludeModel -and (Test-Path "$CacheHome/models")) {
            New-Item -ItemType Directory -Force "$destination/data" | Out-Null
            Copy-Item -LiteralPath "$CacheHome/models" -Destination "$destination/data" -Recurse -Force
        }
    }
    if ($IncludeRuntime) {
        & $cli runtime install --flavor cuda
        if ($LASTEXITCODE -ne 0) { throw 'Runtime installation failed.' }
    }
    if ($IncludeModel) {
        & $cli models download --id bs-roformer-sw-q8_0
        if ($LASTEXITCODE -ne 0) { throw 'Model download failed.' }
    }
    Write-Host "Ready: $destination/Stemora.App.exe"
    Write-Host "CLI:   $destination/cli/stemora.exe"
} finally { Pop-Location }
