[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $PSScriptRoot '.tools\zig-0.16.0\zig-x86_64-windows-0.16.0\zig.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Pinned Zig compiler is missing.' }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($destination) | Out-Null
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $PSScriptRoot '.tools\zig-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $PSScriptRoot '.tools\zig-local-cache'
& $compiler c++ (Join-Path $PSScriptRoot 'src\Companion.GcProbe\gc_probe.cpp') '-std=c++17' '-O2' '-shared' '-fno-exceptions' '-fno-rtti' '-Wall' '-Wextra' '-Werror' '-target' 'x86_64-windows-gnu' '-o' (Join-Path $destination 'VpcGcProbe.dll')
if ($LASTEXITCODE -ne 0) { throw 'Native GC probe build failed.' }
