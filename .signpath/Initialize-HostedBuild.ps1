#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkDirectory,
    [string]$PlainInputArchive,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$PlainInputHash
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ArchiveTools.ps1')
if (-not $PlainInputArchive) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'build-inputs.json') -PathType Leaf)) { throw 'Commit the encrypted build inputs and .signpath/build-inputs.json before running the hosted build. See GITHUB_BUILD.md.' }
    if (-not $env:VPC_BUILD_INPUTS_PASSPHRASE) { throw 'Configure the repository Actions secret VPC_BUILD_INPUTS_PASSPHRASE before running the hosted build.' }
}
$project = Split-Path -Parent $PSScriptRoot
$work = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'WorkDirectory must be new so a partial setup cannot be mistaken for success.' }
[IO.Directory]::CreateDirectory($work) | Out-Null
$pins = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tool-sources.json') -Raw | ConvertFrom-Json
$tools = Join-Path $project '.tools'
[IO.Directory]::CreateDirectory($tools) | Out-Null
$sdk = Join-Path $tools 'dotnet'
$zig = Join-Path $tools 'zig-0.16.0'
foreach ($tool in @(
    @{ Url = $pins.DotNet.url; Hash = $pins.DotNet.hash; Archive = 'dotnet-sdk.zip'; Destination = $sdk },
    @{ Url = $pins.Zig.tarball; Hash = $pins.Zig.shasum; Archive = 'zig-0.16.0.zip'; Destination = $zig }
)) {
    $archive = Join-Path $tools $tool.Archive
    Save-VerifiedDownload -Uri $tool.Url -Path $archive -ExpectedHash $tool.Hash
    if (-not (Test-Path -LiteralPath $tool.Destination)) { Expand-VerifiedArchive -ArchivePath $archive -Destination $tool.Destination -ExpectedHash $tool.Hash }
    elseif ($env:GITHUB_ACTIONS -eq 'true') { throw 'Hosted tool destination already exists; refusing an unverified tool cache.' }
}
Save-VerifiedDownload -Uri $pins.PerfView.url -Path (Join-Path $tools 'PerfView.exe') -ExpectedHash $pins.PerfView.sha256
$env:DOTNET_ROOT = $sdk
$env:PATH = $sdk + [IO.Path]::PathSeparator + $env:PATH
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$actualSdk = & (Join-Path $sdk 'dotnet.exe') --version
if ($LASTEXITCODE -ne 0 -or $actualSdk.Trim() -ne (Get-Content -LiteralPath (Join-Path $project 'global.json') -Raw | ConvertFrom-Json).sdk.version) { throw 'The pinned .NET SDK did not start with the required version.' }
if ($PlainInputArchive) {
    if (-not $PlainInputHash) { throw 'PlainInputHash is required for a local plaintext archive.' }
    if ($env:GITHUB_ACTIONS -eq 'true') { throw 'Hosted builds require the committed encrypted input and pinned manifest.' }
    $inputArchive = (Resolve-Path -LiteralPath $PlainInputArchive).Path
    $inputHash = $PlainInputHash
} else {
    $inputPin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build-inputs.json') -Raw | ConvertFrom-Json
    $encrypted = Join-Path $PSScriptRoot $inputPin.File
    if ((Get-FileHash -LiteralPath $encrypted -Algorithm SHA256).Hash -ne $inputPin.EncryptedSHA256) { throw 'Encrypted build inputs do not match the committed hash.' }
    $inputArchive = Join-Path $work 'build-inputs.zip'
    & (Join-Path $PSScriptRoot 'Invoke-ArchiveEncryption.ps1') -Mode Decrypt -InputFile $encrypted -OutputFile $inputArchive
    $inputHash = $inputPin.PlainSHA256
}
$inputsRoot = Join-Path $work 'inputs'
Expand-VerifiedArchive -ArchivePath $inputArchive -Destination $inputsRoot -ExpectedHash $inputHash
$manifest = @(Get-Content -LiteralPath (Join-Path $inputsRoot 'inputs-manifest.json') -Raw | ConvertFrom-Json)
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest) {
    if ($entry.Path -notmatch '^(Game/valheim_Data/Managed/.+\.dll|Game/MonoBleedingEdge/EmbedRuntime/.+\.dll|Game/MonoBleedingEdge/etc/.+|BepInEx/core/.+\.dll)$' -or $entry.SHA256 -notmatch '^[A-Fa-f0-9]{64}$' -or -not $expected.Add($entry.Path)) { throw 'Invalid build-input manifest entry.' }
    $file = Join-Path $inputsRoot $entry.Path
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne $entry.Bytes -or (Get-FileHash -LiteralPath $file).Hash -ne $entry.SHA256) { throw 'Build-input manifest verification failed.' }
}
$actual = @(Get-ChildItem -LiteralPath $inputsRoot -File -Recurse | Where-Object Name -ne 'inputs-manifest.json')
if ($actual.Count -ne $expected.Count) { throw 'Build inputs contain unexpected files.' }
foreach ($required in @('Game/valheim_Data/Managed/assembly_valheim.dll','Game/valheim_Data/Managed/assembly_utils.dll','Game/MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll','BepInEx/core/BepInEx.dll','BepInEx/core/0Harmony.dll')) {
    if (-not $expected.Contains($required)) { throw "Required build input missing: $required" }
}
$game = Join-Path $inputsRoot 'Game'
# Discovery needs an installation marker. This empty file is never executed;
# tests use the real, hash-verified Mono runtime and managed assemblies above.
[IO.File]::WriteAllBytes((Join-Path $game 'valheim.exe'), [byte[]]::new(0))
$env:VPC_TEST_GAME_ROOT = $game
$bep = Join-Path $inputsRoot 'BepInEx/core'
if ($env:GITHUB_ENV) {
    foreach ($pair in @("DOTNET_ROOT=$sdk",'DOTNET_CLI_TELEMETRY_OPTOUT=1','DOTNET_GENERATE_ASPNET_CERTIFICATE=false',"VPC_TEST_GAME_ROOT=$game","VPC_GAME_PATH=$game","VPC_BEPINEX_PATH=$bep")) {
        if ($pair -match '[\r\n]') { throw 'Invalid environment output.' }
        Add-Content -LiteralPath $env:GITHUB_ENV -Value $pair -Encoding utf8
    }
    Add-Content -LiteralPath $env:GITHUB_PATH -Value $sdk -Encoding utf8
}
[pscustomobject]@{ GamePath = $game; BepInExPath = $bep; DotNetRoot = $sdk; VerifiedInputFiles = $expected.Count }
