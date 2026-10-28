#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Encrypt','Decrypt')][string]$Mode,
    [Parameter(Mandatory)][string]$InputFile,
    [Parameter(Mandatory)][string]$OutputFile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $env:VPC_BUILD_INPUTS_PASSPHRASE -or $env:VPC_BUILD_INPUTS_PASSPHRASE.Length -lt 32 -or $env:VPC_BUILD_INPUTS_PASSPHRASE -match '[\r\n]') { throw 'VPC_BUILD_INPUTS_PASSPHRASE must contain at least 32 characters and no newlines.' }
$source = (Resolve-Path -LiteralPath $InputFile).Path
$destination = [IO.Path]::GetFullPath($OutputFile)
if (Test-Path -LiteralPath $destination) { throw 'Encryption output must not already exist.' }
$gpg = Join-Path $env:ProgramFiles 'Git\usr\bin\gpg.exe'
if (-not (Test-Path -LiteralPath $gpg -PathType Leaf)) { throw 'GnuPG is required (included with Git for Windows on the hosted runner).' }
$gpgconf = Join-Path (Split-Path -Parent $gpg) 'gpgconf.exe'
if (-not (Test-Path -LiteralPath $gpgconf -PathType Leaf)) { throw 'The matching gpgconf utility is required to stop the isolated agent.' }
[IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
# Git for Windows uses bounded IPC socket paths; keep the isolated home short
# even when the checkout or output path contains long names or Unicode.
$isolatedHome = Join-Path ([IO.Path]::GetTempPath()) ('vpc-gpg-' + [guid]::NewGuid().ToString('N').Substring(0,12))
[IO.Directory]::CreateDirectory($isolatedHome) | Out-Null
function ConvertTo-GitPath([string]$Path) {
    if ($Path -notmatch '^([A-Za-z]):[\\/](.*)$') { throw 'GnuPG paths must be on a local Windows drive.' }
    '/' + $Matches[1].ToLowerInvariant() + '/' + $Matches[2].Replace('\','/')
}
$gpgHome = ConvertTo-GitPath $isolatedHome
$start = [Diagnostics.ProcessStartInfo]::new($gpg)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in @('--no-options','--homedir',$gpgHome,'--batch','--no-tty','--pinentry-mode','loopback','--passphrase-fd','0','--no-symkey-cache','--output',(ConvertTo-GitPath $destination))) { $start.ArgumentList.Add($argument) }
if ($Mode -eq 'Encrypt') {
    foreach ($argument in @('--symmetric','--cipher-algo','AES256','--compress-algo','none')) { $start.ArgumentList.Add($argument) }
} else { $start.ArgumentList.Add('--decrypt') }
$start.ArgumentList.Add((ConvertTo-GitPath $source))
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
try {
    if (-not $process.Start()) { throw 'Could not start GnuPG.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.StandardInput.WriteLine($env:VPC_BUILD_INPUTS_PASSPHRASE)
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(300000)) { $process.Kill($true); throw 'GnuPG exceeded its five-minute limit.' }
    $null = $stdout.GetAwaiter().GetResult()
    $diagnostic = $stderr.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "GnuPG $Mode failed (exit $($process.ExitCode)): $diagnostic" }
} finally {
    $process.Dispose()
    & $gpgconf --homedir $gpgHome --kill gpg-agent
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Could not stop the isolated GnuPG agent.' }
}
if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw 'GnuPG did not create the expected output.' }
