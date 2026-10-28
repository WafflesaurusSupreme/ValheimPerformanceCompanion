[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$publish = [IO.Path]::GetFullPath($PublishDirectory)
$compiler = Join-Path $PSScriptRoot '.tools\zig-0.16.0\zig-x86_64-windows-0.16.0\zig.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The pinned project-local Zig 0.16.0 compiler is missing. See README build instructions.' }
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $PSScriptRoot '.tools\zig-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $PSScriptRoot '.tools\zig-local-cache'
$desktop = Join-Path $publish 'ValheimPerformanceCompanion.exe'
if (-not (Test-Path -LiteralPath $desktop)) { throw 'Published desktop application is missing.' }
$staging = Join-Path $PSScriptRoot ('artifacts\native-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
New-Item -ItemType Directory -Path $staging | Out-Null
$required = Get-ChildItem -LiteralPath $publish -File -Recurse | Where-Object { $_.Extension -in @('.dll','.exe','.json') }
$lines = foreach ($file in $required) {
    $relative = [IO.Path]::GetRelativePath($publish, $file.FullName).Replace('\','\\').Replace('"','\"')
    'L"' + $relative + '",'
}
[IO.File]::WriteAllLines((Join-Path $staging 'required-files.inc'), $lines, [Text.UTF8Encoding]::new($false))
$launcher = Join-Path $staging 'ValheimPerformanceCompanionLauncher.exe'
$resource = Join-Path $staging 'version.res'
Push-Location -LiteralPath $PSScriptRoot
try {
    & $compiler rc /fo $resource (Join-Path $PSScriptRoot 'src\Companion.Launcher\version.rc')
    if ($LASTEXITCODE -ne 0) { throw 'Native launcher resource build failed.' }
    & $compiler c++ (Join-Path $PSScriptRoot 'src\Companion.Launcher\main.cpp') $resource '-std=c++17' '-O2' '-static' '-municode' '-Wl,--subsystem,windows' '-Wall' '-Wextra' '-Werror' '-target' 'x86_64-windows-gnu' '-I' $staging '-luser32' '-lshell32' '-o' $launcher
    if ($LASTEXITCODE -ne 0) { throw 'Native startup launcher build failed.' }
}
finally { Pop-Location }
Copy-Item -LiteralPath $launcher -Destination (Join-Path $publish 'ValheimPerformanceCompanionLauncher.exe') -Force
Write-Output "Native launcher: $launcher"
