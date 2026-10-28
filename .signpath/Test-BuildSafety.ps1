#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ArchiveTools.ps1')
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Test output must be new.' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$cases = @(
    @{ Names = @("directory Ω [x]'s/value.txt"); Valid = $true },
    @{ Names = @('../escape.txt'); Valid = $false },
    @{ Names = @('..\escape.txt'); Valid = $false },
    @{ Names = @('C:/escape.txt'); Valid = $false },
    @{ Names = @('/escape.txt'); Valid = $false },
    @{ Names = @('file:stream'); Valid = $false },
    @{ Names = @('A.txt','a.txt'); Valid = $false },
    @{ Names = @('trailing./file'); Valid = $false },
    @{ Names = @('NUL.txt'); Valid = $false },
    @{ Names = @('file','file/child.txt'); Valid = $false },
    @{ Names = @('directory/link'); Link = $true; Valid = $false }
)
$index = 0
foreach ($case in $cases) {
    $archivePath = Join-Path $root "case-$index.zip"
    $zip = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $case.Names) {
            $entry = $zip.CreateEntry($name)
            if ($case.ContainsKey('Link')) { $entry.ExternalAttributes = [int]0xA0000000 }
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('test payload') } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
    $destination = Join-Path $root "expanded-$index"
    $rejected = $false
    try { Expand-VerifiedArchive -ArchivePath $archivePath -Destination $destination -ExpectedHash (Get-FileHash -LiteralPath $archivePath).Hash } catch { $rejected = $true; if ($case.Valid) { throw } }
    if ($case.Valid -eq $rejected) { throw "Archive case $index did not enforce the expected boundary." }
    if (-not $case.Valid -and (Test-Path -LiteralPath $destination)) { throw 'An unsafe archive was partially extracted.' }
    $index++
}
$validArchive = Join-Path $root 'case-0.zip'
$badHashRejected = $false
try { Expand-VerifiedArchive -ArchivePath $validArchive -Destination (Join-Path $root 'bad-hash') -ExpectedHash ('0' * 64) } catch { $badHashRejected = $true }
if (-not $badHashRejected -or (Test-Path -LiteralPath (Join-Path $root 'bad-hash'))) { throw 'Archive hash mismatch was not rejected before extraction.' }
$unsignedRejected = $false
try { & (Join-Path $PSScriptRoot 'Assert-SignedFiles.ps1') -FilePath $validArchive } catch { $unsignedRejected = $true }
if (-not $unsignedRejected) { throw 'Unsigned input passed the signing guard.' }
Write-Output "Build safety: $($cases.Count) archive cases, wrong hash, and unsigned-file rejection passed."
