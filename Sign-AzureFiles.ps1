#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string[]]$FilePath,
    [Parameter(Mandatory)][string]$SignToolPath,
    [Parameter(Mandatory)][string]$DlibPath,
    [Parameter(Mandatory)][string]$MetadataPath,
    [Parameter(Mandatory)][string]$ExpectedPublisher
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$signTool = (Resolve-Path -LiteralPath $SignToolPath -ErrorAction Stop).Path
$dlib = (Resolve-Path -LiteralPath $DlibPath -ErrorAction Stop).Path
$metadataFile = (Resolve-Path -LiteralPath $MetadataPath -ErrorAction Stop).Path
foreach ($tool in @($signTool,$dlib)) {
    $signature = Get-AuthenticodeSignature -LiteralPath $tool
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName,$false) -ne 'Microsoft Corporation') {
        throw 'Signing requires explicitly provided Microsoft-signed Artifact Signing client tools.'
    }
}
if ((Get-Item -LiteralPath $metadataFile).Length -gt 16384) { throw 'Signing metadata exceeds its size limit.' }
$metadata = Get-Content -LiteralPath $metadataFile -Raw | ConvertFrom-Json -AsHashtable
foreach ($required in @('Endpoint','CodeSigningAccountName','CertificateProfileName')) {
    if (-not $metadata.ContainsKey($required) -or $metadata[$required] -isnot [string]) { throw "Signing metadata requires $required" }
}
$endpoint = [uri]$metadata.Endpoint
if (-not $endpoint.IsAbsoluteUri -or $endpoint.Scheme -ne 'https' -or $endpoint.Host -notmatch '^[a-z0-9-]+\.codesigning\.azure\.net$' -or
    $endpoint.UserInfo -or $endpoint.Query -or $endpoint.Fragment -or $endpoint.AbsolutePath -ne '/' -or $endpoint.Port -ne 443) {
    throw 'The signing endpoint must be an official Azure Artifact Signing HTTPS regional endpoint.'
}
foreach ($key in @('CodeSigningAccountName','CertificateProfileName')) {
    if ($metadata[$key] -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{1,62}$') { throw "Invalid $key" }
}
foreach ($key in $metadata.Keys) {
    if ($key -notin @('Endpoint','CodeSigningAccountName','CertificateProfileName','CorrelationId','ExcludeCredentials')) { throw "Unexpected signing metadata field: $key" }
}
if ($metadata.ContainsKey('CorrelationId') -and -not [guid]::TryParse([string]$metadata.CorrelationId,[ref]([guid]::Empty))) { throw 'Invalid signing correlation ID.' }
if ($metadata.ContainsKey('ExcludeCredentials')) {
    $allowed = @('EnvironmentCredential','WorkloadIdentityCredential','ManagedIdentityCredential','SharedTokenCacheCredential','VisualStudioCredential','VisualStudioCodeCredential','AzureCliCredential','AzurePowerShellCredential','AzureDeveloperCliCredential','InteractiveBrowserCredential')
    foreach ($credential in $metadata.ExcludeCredentials) { if ($credential -notin $allowed) { throw 'Unknown credential exclusion in signing metadata.' } }
}
$files = foreach ($item in $FilePath) {
    $file = Get-Item -LiteralPath $item -ErrorAction Stop
    if ($file.PSIsContainer -or $file.Extension -notin @('.dll','.exe')) { throw 'Only explicitly selected Windows binaries can be signed.' }
    for ($part = $file; $null -ne $part; $part = if ($part -is [IO.FileInfo]) { $part.Directory } else { $part.Parent }) {
        if ($part.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Signing cannot traverse linked paths.' }
    }
    $file.FullName
}
foreach ($file in $files) {
    if ($PSCmdlet.ShouldProcess($file, 'Sign and timestamp using the existing Azure Artifact Signing account')) {
        $start = [Diagnostics.ProcessStartInfo]::new($signTool)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        foreach ($argument in @('sign','/fd','SHA256','/tr','http://timestamp.acs.microsoft.com','/td','SHA256','/dlib',$dlib,'/dmdf',$metadataFile,$file)) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        try {
            if (-not $process.WaitForExit(180000)) { $process.Kill($true); $process.WaitForExit(); throw 'Artifact signing exceeded its three-minute deadline.' }
            if ($process.ExitCode -ne 0) { throw "Artifact signing failed or returned a warning: $($process.ExitCode)" }
        } finally { $process.Dispose() }
        & (Join-Path $PSScriptRoot '.signpath\Assert-SignedFiles.ps1') -FilePath $file -ExpectedPublisher $ExpectedPublisher
    }
}
# This script neither provisions billing resources nor changes subscription renewal.
# A caller must finish within the approved single Basic billing period and unenroll separately.
