[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GamePath,
    [Parameter(Mandatory)][string]$BepInExPath,
    [string]$LegacyCapture,
    [switch]$FrameworkDependent,
    [switch]$SkipGuiTests,
    [ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$SigningCertificateThumbprint,
    [string]$SignToolPath,
    [uri]$TimestampServer,
    [string]$PrebuiltGcProbePath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($FrameworkDependent) { throw 'Framework-dependent packaging is unsupported. The portable native launcher requires the bundled runtime; omit -FrameworkDependent.' }
if ($SigningCertificateThumbprint -and (-not $SignToolPath -or -not $TimestampServer)) { throw 'Signing requires explicit SignToolPath and TimestampServer.' }
if (-not $SigningCertificateThumbprint -and ($SignToolPath -or $TimestampServer)) { throw 'Signing options require an explicit SigningCertificateThumbprint.' }
if ($PrebuiltGcProbePath) {
    $PrebuiltGcProbePath = (Resolve-Path -LiteralPath $PrebuiltGcProbePath -ErrorAction Stop).Path
    & (Join-Path $PSScriptRoot '.signpath\Assert-SignedFiles.ps1') -FilePath $PrebuiltGcProbePath
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$projectRoot = $PSScriptRoot
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\packages'
$sdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
if (-not (Test-Path -LiteralPath (Join-Path $GamePath 'valheim.exe'))) { throw 'GamePath must contain valheim.exe.' }
if (-not (Test-Path -LiteralPath (Join-Path $BepInExPath 'BepInEx.dll'))) { throw 'BepInExPath must be the existing BepInEx core directory.' }
$artifacts = Join-Path $projectRoot ('artifacts\build-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
New-Item -ItemType Directory -Path $artifacts | Out-Null
Push-Location -LiteralPath $projectRoot
try {
    $recorderArguments = @('build','src\Companion.Recorder\Companion.Recorder.csproj','-c','Release','--nologo',"-p:GamePath=$GamePath","-p:BepInExPath=$BepInExPath")
    if ($PrebuiltGcProbePath) { $recorderArguments += "-p:PrebuiltGcProbePath=$PrebuiltGcProbePath" }
    & $sdk @recorderArguments
    if ($LASTEXITCODE -ne 0) { throw 'Recorder build failed.' }
    & $sdk build 'src\Companion.App\Companion.App.csproj' -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    $testArguments = @('run','--project','tests\Companion.Tests\Companion.Tests.csproj','-c','Release','--',(Join-Path $artifacts 'tests'))
    if ($LegacyCapture) { $testArguments += $LegacyCapture }
    & $sdk @testArguments
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
    & $sdk run --project 'tests\Companion.ReviewProbes\Companion.ReviewProbes.csproj' -c Release -- (Join-Path $artifacts 'recovery-tests')
    if ($LASTEXITCODE -ne 0) { throw 'Capture recovery regressions failed.' }
    if (-not $SkipGuiTests) {
    & $sdk run --project 'tests\Companion.AppTests\Companion.AppTests.csproj' -c Release -- (Join-Path $artifacts 'wpf-tests')
    if ($LASTEXITCODE -ne 0) { throw 'WPF automatic setup/timer checks failed.' }
    & $sdk run --project 'tests\Companion.AppTests\Companion.AppTests.csproj' -c Release -- (Join-Path $artifacts 'close-tests') --close-only
    if ($LASTEXITCODE -ne 0) { throw 'WPF close regression failed.' }
    & $sdk run --project 'tests\Companion.AppTests\Companion.AppTests.csproj' -c Release -- (Join-Path $artifacts 'history-tests') --history
    if ($LASTEXITCODE -ne 0) { throw 'Capture history regression failed.' }
    & $sdk run --project 'tests\Companion.AppTests\Companion.AppTests.csproj' -c Release -- (Join-Path $artifacts 'retry-tests') --retry
    if ($LASTEXITCODE -ne 0) { throw 'WPF slow-start retry regression failed.' }
    & $sdk run --project 'tests\Companion.AppTests\Companion.AppTests.csproj' -c Release -- (Join-Path $artifacts 'late-attach-tests') --late-attach
    if ($LASTEXITCODE -ne 0) { throw 'WPF already-running game attachment regression failed.' }
    & $sdk run --project 'tests\Companion.AppTests\Companion.AppTests.csproj' -c Release -- (Join-Path $artifacts 'disconnect-tests') --disconnect
    if ($LASTEXITCODE -ne 0) { throw 'WPF recorder reconnection regression failed.' }
    } else { Write-Output 'GUI launch tests skipped as requested; no desktop app was started.' }
    & (Join-Path $projectRoot 'TestMono.ps1') -GamePath $GamePath -BepInExPath $BepInExPath
    $publish = Join-Path $artifacts 'ValheimPerformanceCompanion'
    & $sdk publish 'src\Companion.App\Companion.App.csproj' -c Release -r win-x64 --self-contained true -o $publish --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    foreach ($name in @('README.md','THIRD-PARTY-NOTICES.md','PerfView-LICENSE.txt','VERIFICATION.md','ADVERSARIAL-REVIEW-0.1.6.md')) {
        if (Test-Path -LiteralPath (Join-Path $projectRoot $name)) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $publish }
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Licenses') -Destination $publish -Recurse
    & (Join-Path $projectRoot 'BuildNative.ps1') -PublishDirectory $publish
    if ($SigningCertificateThumbprint) {
        & (Join-Path $projectRoot 'SignRelease.ps1') -PublishDirectory $publish -CertificateThumbprint $SigningCertificateThumbprint -SignToolPath $SignToolPath -TimestampServer $TimestampServer
    } else { Write-Output 'Release is unsigned. No signing service, payment, or trust-store change was requested.' }
    [xml]$buildProperties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
    $releaseVersion = [string]$buildProperties.Project.PropertyGroup.Version
    $archive = Join-Path $artifacts ("ValheimPerformanceCompanion-$releaseVersion-win-x64.zip")
    Compress-Archive -LiteralPath $publish -DestinationPath $archive
    Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
    if ($env:GITHUB_OUTPUT) {
        foreach ($line in @("publish-directory=$publish", "release-archive=$archive")) {
            if ($line -match '[\r\n]') { throw 'Invalid build output path.' }
            Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value $line -Encoding utf8
        }
    }
    Write-Output "Release: $archive"
}
finally { Pop-Location }
