[CmdletBinding()]
param([switch]$IncludeRuntime, [switch]$IncludeModel, [string]$CacheHome, [switch]$SkipNativeBuild, [string]$OutputDirectory, [string]$NativeDirectory)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$destination = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $workspace 'artifacts/Stemora' }
$nativeSource = if ($NativeDirectory) { [IO.Path]::GetFullPath($NativeDirectory) } else { Join-Path $workspace 'artifacts/native' }
Push-Location $workspace
try {
    New-Item -ItemType Directory -Force $destination | Out-Null
    $appDestination = Join-Path $destination 'app'
    $launcherBuild = Join-Path $workspace '.local/launcher-build'
    cmake -S src/Stemora.Launcher -B $launcherBuild -A x64
    if ($LASTEXITCODE -ne 0) { throw 'Launcher configuration failed.' }
    cmake --build $launcherBuild --config Release
    if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }
    if(-not $SkipNativeBuild) {
        & "$PSScriptRoot/Build-Native.ps1"
        if($LASTEXITCODE -ne 0) {throw 'Native infrastructure build failed.'}
    }
    $nativeDestination=[IO.Path]::GetFullPath((Join-Path $destination 'native'))
    if(-not $nativeDestination.StartsWith([IO.Path]::GetFullPath($destination)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'Invalid native publication path.'}
    if(Test-Path $nativeDestination) {Remove-Item -LiteralPath $nativeDestination -Recurse -Force}
    New-Item -ItemType Directory -Force $nativeDestination | Out-Null
    foreach ($required in @('stemora-infra.exe', 'stemora_infra.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $nativeSource $required))) { throw "Missing native runtime: $required" }
    }
    Get-ChildItem -LiteralPath $nativeSource -File | Where-Object {
        $_.Extension -in @('.dll','.exe','.json','.txt','.manifest') -and $_.Name -notlike '*test*'
    } | Copy-Item -Destination "$destination/native" -Force
    dotnet publish src/Stemora.Cli -c Release -r win-x64 --self-contained true -o "$destination/cli"
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
    dotnet publish src/Stemora.App -c Release -p:Platform=x64 -r win-x64 --self-contained true -o $appDestination
    if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
    Copy-Item -LiteralPath (Join-Path $launcherBuild 'Release/Stemora.App.exe') -Destination (Join-Path $destination 'Stemora.App.exe') -Force
    # Windows App SDK also ships native .mui resources, outside SatelliteResourceLanguages.
    # Only remove recognized culture folders, leaving non-language runtime assets intact.
    $cultures = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [Globalization.CultureInfo]::GetCultures([Globalization.CultureTypes]::AllCultures) | ForEach-Object { [void]$cultures.Add($_.Name) }
    $keepLanguages = @('en','en-US','zh','zh-CN','zh-Hans')
    foreach ($publishRoot in @($appDestination, (Join-Path $destination 'cli'))) {
        $resolvedPublishRoot = [IO.Path]::GetFullPath($publishRoot) + [IO.Path]::DirectorySeparatorChar
        foreach ($directory in Get-ChildItem -LiteralPath $publishRoot -Directory -Recurse) {
            # Some WinUI locale names are absent from .NET's installed-culture list.
            $hasLocalizedResources = $directory.Name -match '^[a-z]{2,3}(-[a-z0-9]{2,8})*$' -and
                @(Get-ChildItem -LiteralPath $directory.FullName -File | Where-Object { $_.Name -match '\.mui$|\.resources\.dll$' }).Count -gt 0
            if (($cultures.Contains($directory.Name) -or $hasLocalizedResources) -and $directory.Name -notin $keepLanguages) {
                $resolvedDirectory = [IO.Path]::GetFullPath($directory.FullName)
                if (-not $resolvedDirectory.StartsWith($resolvedPublishRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid resource cleanup path.' }
                Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
            }
        }
        Get-ChildItem -LiteralPath $publishRoot -Filter '*.pdb' -Recurse -File | Remove-Item -Force
    }
    foreach ($resource in @('App.xbf', 'MainWindow.xbf')) {
        if (-not (Test-Path (Join-Path $appDestination $resource))) { throw "Published WinUI resource missing: $resource" }
    }
    New-Item -ItemType File -Force "$destination/portable.flag" | Out-Null
    Copy-Item -LiteralPath README.md,README.en.md,LICENSE,THIRD-PARTY-NOTICES.md -Destination $destination
    New-Item -ItemType Directory -Force "$destination/docs/images" | Out-Null
    Copy-Item -LiteralPath docs/UI-DESIGN.md,docs/NATIVE-INFRA.md,docs/RELEASING.md,docs/CLI.md,docs/CLI.en.md,docs/DEVELOPMENT.md,docs/DEVELOPMENT.en.md,docs/MODEL-CATALOG.md,docs/MODEL-MEMORY.md,docs/MELODY-TRANSCRIPTION.md,docs/MELODY-TRANSCRIPTION-UX.md,docs/MUSIC-ANALYSIS.md -Destination "$destination/docs"
    New-Item -ItemType Directory -Force "$destination/docs/benchmarks" | Out-Null
    Copy-Item -Path 'docs/benchmarks/*.json' -Destination "$destination/docs/benchmarks" -Force
    Copy-Item -Path 'docs/images/*' -Destination "$destination/docs/images" -Force
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
