#requires -Version 7.0
Set-StrictMode -Version Latest

function Expand-VerifiedArchive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$|^[A-Fa-f0-9]{128}$')][string]$ExpectedHash
    )
    $algorithm = if ($ExpectedHash.Length -eq 128) { 'SHA512' } else { 'SHA256' }
    if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm $algorithm).Hash -ne $ExpectedHash) { throw 'Archive hash verification failed.' }
    $root = [IO.Path]::GetFullPath($Destination).TrimEnd('\', '/')
    if (Test-Path -LiteralPath $root) { throw 'Archive destination must not already exist.' }
    for ($part = [IO.DirectoryInfo]::new($root); $null -ne $part; $part = $part.Parent) {
        if ($part.Exists -and ($part.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Archive destination traverses a linked directory.' }
    }
    $zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
    try {
        if ($zip.Entries.Count -gt 40000) { throw 'Archive has too many entries.' }
        $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$total = 0
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ([IO.Path]::IsPathRooted($name) -or $name.Contains(':') -or $name -match '(^|/)\.\.(/|$)' -or $name.Contains([char]0)) { throw 'Unsafe archive entry path.' }
            foreach ($segment in $name.TrimEnd('/').Split('/')) {
                if (-not $segment -or $segment -eq '.' -or $segment -match '[<>:"|?*\x00-\x1f]' -or $segment.EndsWith('.') -or $segment.EndsWith(' ') -or $segment -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'Unsafe Windows archive name.' }
            }
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Archive links are not permitted.' }
            $target = [IO.Path]::GetFullPath((Join-Path $root $name))
            if (-not $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not $paths.Add($target)) { throw 'Escaping or duplicate archive entry.' }
            if (-not $name.EndsWith('/')) { $null = $files.Add($target) }
            $total += $entry.Length
            if ($entry.Length -gt 2GB -or $total -gt 5GB) { throw 'Archive exceeds extraction limits.' }
        }
        foreach ($target in $paths) {
            for ($parent = [IO.Path]::GetDirectoryName($target); $parent -ne $root; $parent = [IO.Path]::GetDirectoryName($parent)) {
                if ($files.Contains($parent)) { throw 'Archive file conflicts with a parent directory.' }
            }
        }
        [IO.Directory]::CreateDirectory($root) | Out-Null
        foreach ($entry in $zip.Entries) {
            $target = [IO.Path]::GetFullPath((Join-Path $root $entry.FullName.Replace('\', '/')))
            if ($entry.FullName.EndsWith('/') -or $entry.FullName.EndsWith('\')) { [IO.Directory]::CreateDirectory($target) | Out-Null; continue }
            [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
            $inputStream = $entry.Open()
            try {
                $outputStream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try {
                    $buffer = [byte[]]::new(65536)
                    [long]$written = 0
                    while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $written += $read
                        if ($written -gt $entry.Length) { throw 'Archive expanded beyond its declared size.' }
                        $outputStream.Write($buffer, 0, $read)
                    }
                } finally { $outputStream.Dispose() }
            } finally { $inputStream.Dispose() }
            if ((Get-Item -LiteralPath $target).Length -ne $entry.Length) { throw 'Extracted file length mismatch.' }
        }
    } finally { $zip.Dispose() }
}

function Save-VerifiedDownload {
    [CmdletBinding()]
    param([Parameter(Mandatory)][uri]$Uri, [Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$|^[A-Fa-f0-9]{128}$')][string]$ExpectedHash)
    if ($Uri.Scheme -ne 'https' -or $Uri.UserInfo) { throw 'A credential-free HTTPS download URL is required.' }
    if (-not (Test-Path -LiteralPath $Path)) {
        [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
        try { Invoke-WebRequest -Uri $Uri -OutFile $Path -TimeoutSec 300 -ErrorAction Stop }
        catch { throw 'Build download failed. Check the configured source; URL query parameters are intentionally omitted from diagnostics.' }
    }
    $algorithm = if ($ExpectedHash.Length -eq 128) { 'SHA512' } else { 'SHA256' }
    if ((Get-FileHash -LiteralPath $Path -Algorithm $algorithm).Hash -ne $ExpectedHash) { throw 'Downloaded file hash mismatch.' }
}
