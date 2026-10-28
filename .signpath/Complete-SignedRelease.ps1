#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$UnsignedDirectory,
    [Parameter(Mandatory)][string]$SignedDirectory,
    [Parameter(Mandatory)][string]$NativeGcProbePath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$signed = (Resolve-Path -LiteralPath $SignedDirectory).Path
$original = (Resolve-Path -LiteralPath $UnsignedDirectory).Path
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Signed release output must be new.' }
$firstParty = @('ValheimPerformanceCompanion.exe','ValheimPerformanceCompanionLauncher.exe','ValheimPerformanceCompanion.dll','Companion.Core.dll','Recorder/ValheimPerformanceCompanion.Recorder.dll')
$originalFiles = @(Get-ChildItem -LiteralPath $original -File -Recurse)
$signedFiles = @(Get-ChildItem -LiteralPath $signed -File -Recurse)
if ($originalFiles.Count -ne $signedFiles.Count) { throw 'Signing changed the release file count.' }
foreach ($file in $signedFiles) {
    for ($part = $file; $null -ne $part; $part = if ($part -is [IO.FileInfo]) { $part.Directory } else { $part.Parent }) {
        if ($part.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked signing output is not permitted.' }
    }
    $relative = [IO.Path]::GetRelativePath($signed, $file.FullName).Replace('\','/')
    $before = Join-Path $original $relative
    if (-not (Test-Path -LiteralPath $before -PathType Leaf)) { throw 'Signing returned an unexpected file.' }
    if ($relative -notin $firstParty -and (Get-FileHash -LiteralPath $before).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Signing changed a file outside the first-party signing scope: $relative" }
}
& (Join-Path $PSScriptRoot 'Assert-SignedFiles.ps1') -FilePath @($firstParty | ForEach-Object { Join-Path $signed $_ })
& (Join-Path $PSScriptRoot 'Assert-SignedFiles.ps1') -FilePath $NativeGcProbePath
# Read our verified recorder's embedded helper without invoking recorder code.
$recorder = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $signed 'Recorder/ValheimPerformanceCompanion.Recorder.dll')))
$resource = $recorder.GetManifestResourceStream('VpcGcProbe.dll')
if ($null -eq $resource) { throw 'The signed recorder has no embedded native helper.' }
$memory = [IO.MemoryStream]::new()
try { $resource.CopyTo($memory); $embeddedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($memory.ToArray())) }
finally { $resource.Dispose(); $memory.Dispose() }
if ($embeddedHash -ne (Get-FileHash -LiteralPath $NativeGcProbePath).Hash) { throw 'Recorder does not embed the exact signed native helper.' }
[IO.Directory]::CreateDirectory($destination) | Out-Null
$payload = Join-Path $destination 'ValheimPerformanceCompanion'
Copy-Item -LiteralPath $signed -Destination $payload -Recurse
[xml]$properties = Get-Content -LiteralPath (Join-Path $project 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
$archive = Join-Path $destination "ValheimPerformanceCompanion-$version-win-x64-signed.zip"
Compress-Archive -LiteralPath $payload -DestinationPath $archive
$checksum = (Get-FileHash -LiteralPath $archive).Hash
[IO.File]::WriteAllText((Join-Path $destination 'SHA256SUMS.txt'), "$checksum  $([IO.Path]::GetFileName($archive))`n", [Text.UTF8Encoding]::new($false))
Write-Output "Verified signed release: $archive"
