[CmdletBinding()]
param([switch]$Fix)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
Push-Location $workspace
try {
    $formatArguments = @('format', 'whitespace', 'Stemora.sln', '--verbosity', 'minimal')
    if (-not $Fix) { $formatArguments += '--verify-no-changes' }
    & dotnet @formatArguments
    if ($LASTEXITCODE -ne 0) { throw 'C# formatting check failed. Run scripts/Lint.ps1 -Fix.' }

    $nativeFiles = Get-ChildItem native/include,native/src,native/tests -Recurse -File |
        Where-Object Extension -in '.h','.hpp','.cpp' | Select-Object -ExpandProperty FullName
    $nativeFiles += Join-Path $workspace 'src/Stemora.Launcher/main.cpp'
    [string[]]$clangArguments = if ($Fix) { @('-i') } else { @('--dry-run', '--Werror') }
    & clang-format @clangArguments @nativeFiles
    if ($LASTEXITCODE -ne 0) { throw 'C++ formatting check failed. Run scripts/Lint.ps1 -Fix.' }

    foreach ($script in Get-ChildItem scripts -Filter '*.ps1') {
        $parseTokens = $null
        $parseErrors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$parseTokens, [ref]$parseErrors)
        if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
    }
    Write-Host 'C#, C++ and PowerShell checks passed.'
} finally { Pop-Location }
