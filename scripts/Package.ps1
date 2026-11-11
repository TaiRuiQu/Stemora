[CmdletBinding()]
param([string]$SourceDirectory, [string]$ArchivePath)
$ErrorActionPreference='Stop'
$workspace=Split-Path $PSScriptRoot -Parent
if(-not $SourceDirectory) {$SourceDirectory=Join-Path $workspace 'artifacts/Stemora'}
if(-not $ArchivePath) {$ArchivePath=Join-Path $workspace 'artifacts/Stemora-windows-x64.zip'}
$SourceDirectory=[IO.Path]::GetFullPath($SourceDirectory)
$ArchivePath=[IO.Path]::GetFullPath($ArchivePath)
if(-not (Test-Path "$SourceDirectory/Stemora.App.exe")) {throw 'Publish the app before packaging.'}
$stream=[IO.FileStream]::new($ArchivePath,[IO.FileMode]::Create,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach($file in [IO.Directory]::EnumerateFiles($SourceDirectory,'*',[IO.SearchOption]::AllDirectories)) {
        $name=[IO.Path]::GetRelativePath($SourceDirectory,$file).Replace('\','/')
        if($name -eq 'data/settings.json' -or $name.StartsWith('data/jobs/') -or $name.StartsWith('data/runtime/') -or $name.EndsWith('.lock') -or $name.EndsWith('.partial') -or $name.EndsWith('.pdb')) {continue}
        if($name -match '(?i)vulkan') {throw "Obsolete backend unexpectedly included: $name"}
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file,$name,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {$zip.Dispose();$stream.Dispose()}
$verification=[IO.Compression.ZipFile]::OpenRead($ArchivePath)
try {
    foreach($entry in @('Stemora.App.exe','cli/stemora.exe','App.xbf','MainWindow.xbf','native/stemora-infra.exe','native/stemora_infra.dll','portable.flag')) {
        if(-not $verification.GetEntry($entry)) {throw "Missing archive entry: $entry"}
    }
} finally {$verification.Dispose()}
Get-Item $ArchivePath | Select-Object FullName,Length,LastWriteTime
