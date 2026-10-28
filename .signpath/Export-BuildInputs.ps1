#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GamePath,
    [Parameter(Mandatory)][string]$BepInExPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath $GamePath).Path
$bep = (Resolve-Path -LiteralPath $BepInExPath).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory must be new; existing files will not be replaced.' }
$roots = @(
    @{ Source = Join-Path $game 'valheim_Data\Managed'; Target = 'Game/valheim_Data/Managed'; Filter = '*.dll' },
    @{ Source = Join-Path $game 'MonoBleedingEdge\EmbedRuntime'; Target = 'Game/MonoBleedingEdge/EmbedRuntime'; Filter = '*.dll' },
    @{ Source = Join-Path $game 'MonoBleedingEdge\etc'; Target = 'Game/MonoBleedingEdge/etc'; Filter = '*' },
    @{ Source = $bep; Target = 'BepInEx/core'; Filter = '*.dll' }
)
$files = foreach ($root in $roots) {
    foreach ($file in Get-ChildItem -LiteralPath $root.Source -Filter $root.Filter -File -Recurse -ErrorAction Stop) {
        for ($part = $file; $null -ne $part; $part = if ($part -is [IO.FileInfo]) { $part.Directory } else { $part.Parent }) {
            if ($part.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked build inputs are not permitted.' }
        }
        [pscustomobject]@{ Source = $file.FullName; Path = $root.Target + '/' + [IO.Path]::GetRelativePath($root.Source, $file.FullName).Replace('\', '/'); SHA256 = (Get-FileHash -LiteralPath $file.FullName).Hash; Bytes = $file.Length }
    }
}
foreach ($required in @('Game/valheim_Data/Managed/assembly_valheim.dll','Game/MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll','BepInEx/core/BepInEx.dll','BepInEx/core/0Harmony.dll')) {
    if ($required -notin $files.Path) { throw "Required build input missing: $required" }
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$archivePath = Join-Path $output 'valheim-build-inputs.zip'
$archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.Source, $file.Path, [IO.Compression.CompressionLevel]::Optimal) | Out-Null }
    $manifest = $archive.CreateEntry('inputs-manifest.json')
    $writer = [IO.StreamWriter]::new($manifest.Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write((@($files | Select-Object Path,SHA256,Bytes) | ConvertTo-Json -Depth 4)) } finally { $writer.Dispose() }
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $archivePath).Hash
[IO.File]::WriteAllText((Join-Path $output 'SHA256.txt'), "$hash  valheim-build-inputs.zip`n", [Text.UTF8Encoding]::new($false))
Write-Output ([pscustomobject]@{ ArchivePath = $archivePath; SHA256 = $hash; Files = $files.Count; Bytes = (Get-Item -LiteralPath $archivePath).Length })
