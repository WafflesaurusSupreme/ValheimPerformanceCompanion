#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$FilePath,
    [string]$ExpectedPublisher = 'SignPath Foundation'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
foreach ($path in $FilePath) {
    $file = Get-Item -LiteralPath $path -ErrorAction Stop
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Signature verification requires a regular file.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $null -eq $signature.TimeStamperCertificate) {
        throw "A valid timestamped public signature is required: $($file.Name)"
    }
    if ($signature.SignerCertificate.Subject -eq $signature.SignerCertificate.Issuer -or
        $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne $ExpectedPublisher) {
        throw "Unexpected signing publisher: $($file.Name)"
    }
    Write-Output "Verified signed file: $($file.Name)"
}
