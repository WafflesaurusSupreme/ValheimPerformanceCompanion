#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$WorkloadExecutable,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$RecoveryChecks
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if ([Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run the integration driver unelevated; only its isolated installer requests UAC.'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Integration output must be new.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$destination = Join-Path $env:ProgramFiles ('VpcCollectorValidation-' + [guid]::NewGuid().ToString('N'))
$signal = Join-Path $output 'finish.signal'
$start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh -ErrorAction Stop).Source)
$start.UseShellExecute = $true
$start.Verb = 'runas'
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
foreach ($argument in @('-NoLogo','-NoProfile','-File',(Join-Path $PSScriptRoot 'Install-CollectorValidation.ps1'),'-SourceDirectory',(Resolve-Path -LiteralPath $PublishDirectory).Path,'-DestinationDirectory',$destination,'-FinishSignal',$signal,'-AuthorizedUserSid',$identity.User.Value)) { $start.ArgumentList.Add($argument) }
$installer = [Diagnostics.Process]::Start($start)
$workload = $null
$failure = $null
try {
    $wait = [Diagnostics.Stopwatch]::StartNew()
    $receipt = Join-Path $destination 'validation-installation.json'
    while (-not (Test-Path -LiteralPath $receipt)) {
        if ($installer.HasExited -or $wait.Elapsed.TotalSeconds -gt 90) { throw "Validation installation failed or timed out. Protected output: $destination" }
        Start-Sleep -Milliseconds 200
    }
    $state = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    if (-not $state.Installed) { throw $state.Error }
    $workloadStart = [Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $WorkloadExecutable).Path)
    $workloadStart.UseShellExecute = $false
    $workloadStart.CreateNoWindow = $true
    $workload = [Diagnostics.Process]::Start($workloadStart)
    if ($RecoveryChecks) {
        for ($orphan = 1; $orphan -le 9; $orphan++) {
            $orphanDirectory = Join-Path $output ('Orphan-' + $orphan.ToString('00'))
            $orphanStart = [Diagnostics.ProcessStartInfo]::new((Join-Path $destination 'ValheimPerformanceCompanion.exe'))
            $orphanStart.UseShellExecute = $false
            $orphanStart.CreateNoWindow = $true
            foreach ($argument in @('--abandon-collector-check',[string]$workload.Id,$orphanDirectory)) { $orphanStart.ArgumentList.Add($argument) }
            $orphanClient = [Diagnostics.Process]::Start($orphanStart)
            try {
                if (-not $orphanClient.WaitForExit(100000) -or $orphanClient.ExitCode -ne 0) { throw "Orphan lifecycle check $orphan failed." }
            } finally { if (-not $orphanClient.HasExited) { $orphanClient.Kill(); $orphanClient.WaitForExit() }; $orphanClient.Dispose() }
            Write-Output "PASS abandoned desktop session $orphan of 9; collector remains available."
        }
    }
    $modes = @('Standard','Deep')
    if ($RecoveryChecks) { $modes += 'Restart' }
    foreach ($mode in $modes) {
        $capture = Join-Path $output $mode
        $check = [Diagnostics.ProcessStartInfo]::new((Join-Path $destination 'ValheimPerformanceCompanion.exe'))
        $check.UseShellExecute = $false
        $check.CreateNoWindow = $true
        $captureMode = if ($mode -eq 'Restart') { 'Standard' } else { $mode }
        foreach ($argument in @('--check-installed-collector',[string]$workload.Id,$capture,$captureMode)) { $check.ArgumentList.Add($argument) }
        if ($mode -eq 'Restart') { $check.ArgumentList.Add('restart') }
        $client = [Diagnostics.Process]::Start($check)
        try {
            if (-not $client.WaitForExit(360000)) { throw "$mode collector verification exceeded its deadline." }
            $result = Get-Content -LiteralPath (Join-Path $capture 'installed-collector-check.json') -Raw | ConvertFrom-Json
            $result | ConvertTo-Json -Depth 5 | Write-Output
            if ($client.ExitCode -ne 0 -or -not $result.Success -or $result.DesktopElevated) { throw "$mode capture failed unelevated verification." }
        } finally { if (-not $client.HasExited) { $client.Kill(); $client.WaitForExit() }; $client.Dispose() }
    }
} catch { $failure = $_ }
finally {
    if ($null -ne $workload) {
        if (-not $workload.HasExited) { $workload.Kill(); $workload.WaitForExit() }
        $workload.Dispose()
    }
    [IO.File]::WriteAllText($signal,'Completed',[Text.UTF8Encoding]::new($false))
    if (-not $installer.WaitForExit(180000)) { $failure = "Validation installer is still performing collector cleanup: $destination" }
    elseif ($installer.ExitCode -ne 0) { $failure = ([string]$failure + "`nValidation installer reported a failure: $destination").Trim() }
    else {
        $cleanup = Get-Content -LiteralPath (Join-Path $destination 'validation-installation.json') -Raw | ConvertFrom-Json
        if (-not $cleanup.ServiceRemoved -or $cleanup.Error) { $failure = "Validation service cleanup failed: $($cleanup.Error)" }
    }
    $installer.Dispose()
    Write-Output "Protected validation payload retained: $destination"
}
if ($failure) { throw $failure }
Write-Output 'PASS installed service: unelevated Standard CPU sampling and Deep disk/context-switch evidence; no per-capture elevation.'
