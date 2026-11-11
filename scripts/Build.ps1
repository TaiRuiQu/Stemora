[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$SkipTests, [switch]$SkipNativeBuild)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
Push-Location $workspace
try {
    if(-not $SkipNativeBuild) { & "$PSScriptRoot/Build-Native.ps1" -SkipTests:$SkipTests }
    dotnet build Stemora.sln -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        dotnet run --project tests/Stemora.Tests -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
    }
} finally { Pop-Location }
