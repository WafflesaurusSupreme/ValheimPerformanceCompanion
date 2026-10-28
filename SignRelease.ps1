#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$CertificateThumbprint,
    [Parameter(Mandatory)][string]$SignToolPath,
    [Parameter(Mandatory)][uri]$TimestampServer
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$publish = (Resolve-Path -LiteralPath $PublishDirectory -ErrorAction Stop).Path
$signTool = (Resolve-Path -LiteralPath $SignToolPath -ErrorAction Stop).Path
if (-not $TimestampServer.IsAbsoluteUri -or $TimestampServer.Scheme -notin @('http', 'https') -or $TimestampServer.UserInfo) {
    throw 'TimestampServer must be an absolute HTTP(S) URL without credentials.'
}
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
if (-not $certificate.HasPrivateKey -or $certificate.Subject -eq $certificate.Issuer) {
    throw 'An existing publicly trusted code-signing certificate with a private key is required. Self-signed test certificates are not a public-distribution fix.'
}
$usages = @($certificate.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } | ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
if ('1.3.6.1.5.5.7.3.3' -notin $usages) {
    throw 'The selected certificate is not a code-signing certificate.'
}
$chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
try {
    $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(10)
    if (-not $chain.Build($certificate)) { throw 'The signing certificate is expired, revoked, or does not chain to a trusted root.' }
} finally { $chain.Dispose() }
$relativeTargets = @('ValheimPerformanceCompanion.exe', 'ValheimPerformanceCompanionLauncher.exe',
    'ValheimPerformanceCompanion.dll', 'Companion.Core.dll', 'Recorder\ValheimPerformanceCompanion.Recorder.dll')
$targets = foreach ($relative in $relativeTargets) {
    $path = Join-Path $publish $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Release component is missing: $relative" }
    for ($part = [IO.DirectoryInfo]::new((Split-Path -Parent $path)); $null -ne $part; $part = $part.Parent) {
        if ($part.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Signing cannot traverse a linked directory: $relative" }
    }
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Signing cannot target a linked file: $relative" }
    $path
}
function Invoke-SignTool {
    param([string[]]$ToolArguments)
    $start = [Diagnostics.ProcessStartInfo]::new($signTool)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in $ToolArguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(120000)) { $process.Kill($true); $process.WaitForExit(); throw 'SignTool exceeded its two-minute deadline.' }
        if ($process.ExitCode -ne 0) { throw "SignTool failed or returned a warning (exit $($process.ExitCode)). Release must not be published as signed." }
    } finally { $process.Dispose() }
}
foreach ($path in $targets) {
    if ($PSCmdlet.ShouldProcess($path, 'Sign with the explicitly selected certificate and timestamp server')) {
        Invoke-SignTool -ToolArguments @('sign', '/sha1', $CertificateThumbprint, '/s', 'My', '/fd', 'SHA256', '/tr', $TimestampServer.AbsoluteUri, '/td', 'SHA256', $path)
        Invoke-SignTool -ToolArguments @('verify', '/pa', '/all', $path)
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) {
            throw "Signature, timestamp, or publisher verification failed: $path"
        }
    }
}
