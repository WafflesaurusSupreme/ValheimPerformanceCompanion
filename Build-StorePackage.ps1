#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$IdentityName,
    [string]$Publisher,
    [string]$PublisherDisplayName = 'WafflesaurusSupreme',
    [string]$CodeSigningPublisher,
    [string]$MakeAppxPath,
    [switch]$ValidationOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($ValidationOnly) {
    if ($IdentityName -or $Publisher) { throw 'ValidationOnly uses a deliberately non-publishable identity; omit publisher inputs.' }
    $IdentityName = 'WafflesaurusSupreme.Vpc.Validation'
    $Publisher = 'CN=PackagingValidationOnly'
} elseif (-not $IdentityName -or -not $Publisher) {
    throw 'Supply the exact Identity Name and Publisher from Partner Center, or use ValidationOnly for local packaging checks.'
}
if ($IdentityName -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{2,49}$' -or $Publisher -notmatch '^CN=\S') { throw 'Invalid package identity.' }
$source = (Resolve-Path -LiteralPath $PublishDirectory -ErrorAction Stop).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Store packaging output must be a new directory.' }
if ($output.StartsWith($source.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Store packaging output cannot be inside its input.' }
foreach ($name in @('ValheimPerformanceCompanion.exe','ValheimPerformanceCompanionLauncher.exe','VpcCollectorService.exe','VpcCollectorService.dll','Recorder\ValheimPerformanceCompanion.Recorder.dll','Tools\PerfView.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $name) -PathType Leaf)) { throw "Store package input is missing $name" }
}
for ($part = [IO.DirectoryInfo]::new($source); $null -ne $part; $part = $part.Parent) {
    if ($part.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Store package input cannot traverse a linked directory.' }
}
$pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
$pending.Push([IO.DirectoryInfo]::new($source))
while ($pending.Count -gt 0) {
    foreach ($item in $pending.Pop().EnumerateFileSystemInfos()) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Store package input contains a linked entry.' }
        if ($item -is [IO.DirectoryInfo]) { $pending.Push($item) }
    }
}
if (-not $MakeAppxPath) {
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $versions = @(Get-ChildItem -LiteralPath $sdkRoot -Directory | Where-Object Name -Match '^10\.0\.\d+\.\d+$' | Sort-Object { [version]$_.Name } -Descending)
    foreach ($sdkVersion in $versions) {
        $candidate = Join-Path $sdkVersion.FullName 'x64\makeappx.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $MakeAppxPath = $candidate; break }
    }
}
if (-not $MakeAppxPath) { throw 'The Windows SDK MakeAppx tool is required; no tool is installed automatically.' }
$makeAppx = (Resolve-Path -LiteralPath $MakeAppxPath -ErrorAction Stop).Path
if ((Get-AuthenticodeSignature -LiteralPath $makeAppx).Status -ne 'Valid') { throw 'MakeAppx does not have a valid trusted signature.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
if (-not $ValidationOnly) {
    if (-not $CodeSigningPublisher) { throw 'A production submission requires the expected Authenticode publisher for code copied into Valheim.' }
    $firstParty = @('ValheimPerformanceCompanion.exe','ValheimPerformanceCompanionLauncher.exe','ValheimPerformanceCompanion.dll','Companion.Core.dll','VpcCollectorService.exe','VpcCollectorService.dll','Recorder\ValheimPerformanceCompanion.Recorder.dll')
    & (Join-Path $PSScriptRoot '.signpath\Assert-SignedFiles.ps1') -FilePath @($firstParty | ForEach-Object { Join-Path $source $_ }) -ExpectedPublisher $CodeSigningPublisher
    $recorder = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $source 'Recorder\ValheimPerformanceCompanion.Recorder.dll')))
    $resource = $recorder.GetManifestResourceStream('VpcGcProbe.dll')
    if ($null -eq $resource) { throw 'The recorder has no native GC helper resource.' }
    $probe = Join-Path $output 'verified-native-gc.dll'
    $probeStream = [IO.File]::Open($probe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $resource.CopyTo($probeStream) } finally { $probeStream.Dispose(); $resource.Dispose() }
    & (Join-Path $PSScriptRoot '.signpath\Assert-SignedFiles.ps1') -FilePath $probe -ExpectedPublisher $CodeSigningPublisher
}
$stage = Join-Path $output 'payload'
Copy-Item -LiteralPath $source -Destination $stage -Recurse
[xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'packaging\Store.AppxManifest.xml') -Raw
[xml]$properties = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Directory.Build.props') -Raw
$releaseVersion = [version][string]$properties.Project.PropertyGroup.Version
$version = '{0}.{1}.{2}.0' -f $releaseVersion.Major, $releaseVersion.Minor, $releaseVersion.Build
$manifest.Package.Identity.SetAttribute('Name', $IdentityName)
$manifest.Package.Identity.SetAttribute('Publisher', $Publisher)
$manifest.Package.Identity.SetAttribute('Version', $version)
$manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
$settings = [Xml.XmlWriterSettings]::new()
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$settings.Indent = $true
$writer = [Xml.XmlWriter]::Create((Join-Path $stage 'AppxManifest.xml'), $settings)
try { $manifest.Save($writer) } finally { $writer.Dispose() }
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $stage 'StoreAssets'
[IO.Directory]::CreateDirectory($assets) | Out-Null
$icon = [Drawing.Image]::FromFile((Join-Path $PSScriptRoot 'Assets\companion-icon.png'))
try {
    foreach ($asset in @(@('StoreLogo.png',50),@('Square44x44Logo.png',44),@('Square150x150Logo.png',150))) {
        $size = [int]$asset[1]
        $bitmap = [Drawing.Bitmap]::new($size, $size)
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([Drawing.Color]::Transparent)
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.DrawImage($icon, 0, 0, $size, $size)
            } finally { $graphics.Dispose() }
            $bitmap.Save((Join-Path $assets ([string]$asset[0])), [Drawing.Imaging.ImageFormat]::Png)
        } finally { $bitmap.Dispose() }
    }
} finally { $icon.Dispose() }
$label = if ($ValidationOnly) { 'validation-unsigned' } else { 'store-submission-unsigned' }
$archive = Join-Path $output "ValheimPerformanceCompanion-$version-$label.msix"
$start = [Diagnostics.ProcessStartInfo]::new($makeAppx)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
foreach ($argument in @('pack','/d',$stage,'/p',$archive,'/h','SHA256')) { $start.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($start)
try {
    if (-not $process.WaitForExit(180000)) { $process.Kill($true); $process.WaitForExit(); throw 'MakeAppx exceeded its three-minute deadline.' }
    if ($process.ExitCode -ne 0) { throw "MakeAppx package validation failed: $($process.ExitCode)" }
} finally { $process.Dispose() }
# Check that packaging retained every published byte, including the exact recorder and native resource.
$package = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $entries = @{}
    foreach ($entry in $package.Entries) {
        $relative = $entry.FullName.Replace('\','/')
        if ($relative.StartsWith('/') -or $relative.Split('/') -contains '..' -or $entries.ContainsKey($relative)) { throw 'Invalid or duplicate MSIX entry.' }
        $entries[$relative] = $entry
    }
    foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\','/')
        if (-not $entries.ContainsKey($relative)) { throw "Package omitted $relative" }
        $stream = $entries[$relative].Open()
        try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        if ($actual -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { throw "Package changed $relative" }
    }
} finally { $package.Dispose() }
Write-Output ([pscustomobject]@{ Package = $archive; SHA256 = (Get-FileHash -LiteralPath $archive).Hash; ValidationOnly = [bool]$ValidationOnly; Signed = $false; PackagingCostUSD = 0 })
