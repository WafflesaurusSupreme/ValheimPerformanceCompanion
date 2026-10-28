[CmdletBinding()]
param([Parameter(Mandatory)][string]$GamePath, [Parameter(Mandatory)][string]$BepInExPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
$env:DOTNET_ROOT = Split-Path -LiteralPath $sdk
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$managed = Join-Path ([IO.Path]::GetFullPath($GamePath)) 'valheim_Data\Managed'
$runtime = Join-Path ([IO.Path]::GetFullPath($GamePath)) 'MonoBleedingEdge\EmbedRuntime'
if (-not (Test-Path -LiteralPath (Join-Path $runtime 'mono-2.0-bdwgc.dll'))) { throw 'The game Mono runtime is missing.' }
$output = Join-Path $PSScriptRoot ('artifacts\mono-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
[IO.Directory]::CreateDirectory($output) | Out-Null
Push-Location -LiteralPath $PSScriptRoot
try {
    & $sdk build tests\Companion.MonoHost -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Mono host build failed.' }
    & $sdk build tests\Companion.MonoTests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Mono test driver build failed.' }
    $references = @('mscorlib.dll','System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll') | ForEach-Object { '/r:' + (Join-Path $managed $_) }
    $version = (Get-Content -LiteralPath global.json -Raw | ConvertFrom-Json).sdk.version
    & $sdk (Join-Path $env:DOTNET_ROOT "sdk\$version\Roslyn\bincore\csc.dll") /nologo /target:exe /nostdlib ("/out:" + (Join-Path $output 'MonoPipeProbe.exe')) @references tests\MonoPipeProbe.cs src\Shared\WindowsPipe.cs
    if ($LASTEXITCODE -ne 0) { throw 'Mono probe compile failed.' }
    & $sdk run --project tests\Companion.MonoTests -c Release --no-build -- (Join-Path $PSScriptRoot 'tests\Companion.MonoHost\bin\Release\net10.0-windows\Companion.MonoHost.exe') $runtime $managed (Join-Path $output 'ready.txt') (Join-Path $output 'MonoPipeProbe.exe') 2>&1 | Tee-Object -FilePath (Join-Path $output 'verification.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Mono transport regression failed.' }
    & $sdk (Join-Path $env:DOTNET_ROOT "sdk\$version\Roslyn\bincore\csc.dll") /nologo /target:exe /nostdlib ("/out:" + (Join-Path $output 'RecorderContractsProbe.exe')) @references tests\RecorderContractsProbe.cs
    if ($LASTEXITCODE -ne 0) { throw 'Recorder contract probe compile failed.' }
    & (Join-Path $PSScriptRoot 'tests\Companion.MonoHost\bin\Release\net10.0-windows\Companion.MonoHost.exe') $runtime $managed (Join-Path $output 'RecorderContractsProbe.exe') (Join-Path $PSScriptRoot 'src\Companion.Recorder\bin\Release\netstandard2.1\ValheimPerformanceCompanion.Recorder.dll') ([IO.Path]::GetFullPath($BepInExPath)) 2>&1 | Tee-Object -FilePath (Join-Path $output 'contracts.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Actual recorder contracts failed on Valheim Mono.' }
    & $sdk (Join-Path $env:DOTNET_ROOT "sdk\$version\Roslyn\bincore\csc.dll") /nologo /target:exe /nostdlib ("/out:" + (Join-Path $output 'RecorderLateAttachProbe.exe')) @references tests\RecorderLateAttachProbe.cs
    if ($LASTEXITCODE -ne 0) { throw 'Recorder late-attachment probe compile failed.' }
    & (Join-Path $PSScriptRoot 'tests\Companion.MonoHost\bin\Release\net10.0-windows\Companion.MonoHost.exe') $runtime $managed (Join-Path $output 'RecorderLateAttachProbe.exe') (Join-Path $PSScriptRoot 'src\Companion.Recorder\bin\Release\netstandard2.1\ValheimPerformanceCompanion.Recorder.dll') ([IO.Path]::GetFullPath($BepInExPath)) 2>&1 | Tee-Object -FilePath (Join-Path $output 'late-attach.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Recorder late attachment failed on Valheim Mono.' }
    & $sdk build tests\Companion.ClockMono -c Release --nologo "-p:FrameworkPathOverride=$managed"
    if ($LASTEXITCODE -ne 0) { throw 'Native GC clock probe build failed.' }
    & (Join-Path $PSScriptRoot 'tests\Companion.MonoHost\bin\Release\net10.0-windows\Companion.MonoHost.exe') $runtime $managed (Join-Path $PSScriptRoot 'tests\Companion.ClockMono\bin\Release\net472\Companion.ClockMono.exe') (Join-Path $PSScriptRoot 'src\Companion.Recorder\bin\Release\netstandard2.1\ValheimPerformanceCompanion.Recorder.dll') ([IO.Path]::GetFullPath($BepInExPath)) (Join-Path $output 'clock-captures') 2>&1 | Tee-Object -FilePath (Join-Path $output 'clock.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Direct recorder GC clock regression failed on Valheim Mono.' }
}
finally { Pop-Location }
