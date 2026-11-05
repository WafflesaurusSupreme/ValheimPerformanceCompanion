#requires -Version 7.0
# Development-only elevated fixture. Never included in a recipient package.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$DestinationDirectory,
    [Parameter(Mandatory)][string]$FinishSignal,
    [Parameter(Mandatory)][ValidatePattern('^S-1-[0-9-]+$')][string]$AuthorizedUserSid
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$serviceName = 'ValheimPerformanceCompanionCollector'
$destination = [IO.Path]::GetFullPath($DestinationDirectory).TrimEnd('\')
$programFiles = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\')
if ([IO.Path]::GetDirectoryName($destination) -ne $programFiles -or [IO.Path]::GetFileName($destination) -notmatch '^VpcCollectorValidation-[a-f0-9]{32}$') {
    throw 'The validation destination must be an exact new VpcCollectorValidation GUID directory in Program Files.'
}
if (Test-Path -LiteralPath $destination) { throw 'Validation destination already exists.' }
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw 'An existing collector service must not be replaced by validation.' }
$source = (Resolve-Path -LiteralPath $SourceDirectory -ErrorAction Stop).Path
$pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
$pending.Push([IO.DirectoryInfo]::new($source))
while ($pending.Count -gt 0) {
    $directory = $pending.Pop()
    if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Validation source cannot contain linked directories.' }
    foreach ($entry in $directory.EnumerateFileSystemInfos()) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Validation source cannot contain links.' }
        if ($entry -is [IO.DirectoryInfo]) { $pending.Push($entry) }
    }
}
function Invoke-ServiceCommand {
    param([string[]]$CommandArguments, [int[]]$AllowedExitCodes = @(0))
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32\sc.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in $CommandArguments) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    try {
        if (-not $child.WaitForExit(30000)) { throw 'Service control command exceeded its deadline.' }
        if ($child.ExitCode -notin $AllowedExitCodes) { throw "Service control failed: $($child.ExitCode)" }
    } finally { $child.Dispose() }
}
$createdService = $false
$startedAt = Get-Date
$outcome = @{ Installed = $false; ServiceRemoved = $false; Error = $null }
function Write-InstallationReceipt {
    $receipt = Join-Path $destination 'validation-installation.json'
    $temporary = $receipt + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temporary,($outcome | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary,$receipt,$true)
}
try {
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $security.SetOwner($administrators)
    $security.SetAccessRuleProtection($true,$false)
    $inherit = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),[Security.AccessControl.FileSystemRights]::FullControl,$inherit,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
    }
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),[Security.AccessControl.FileSystemRights]::ReadAndExecute,$inherit,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($destination),$security)
    foreach ($entry in Get-ChildItem -LiteralPath $source -Force) { Copy-Item -LiteralPath $entry.FullName -Destination $destination -Recurse }
    $fileSecurity = [Security.AccessControl.FileSecurity]::new()
    $fileSecurity.SetOwner($administrators)
    $fileSecurity.SetAccessRuleProtection($true,$false)
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $fileSecurity.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.AccessControlType]::Allow))
    }
    $fileSecurity.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),[Security.AccessControl.FileSystemRights]::ReadAndExecute,[Security.AccessControl.AccessControlType]::Allow))
    foreach ($entry in Get-ChildItem -LiteralPath $destination -Recurse -Force) {
        if ($entry.PSIsContainer) { Set-Acl -LiteralPath $entry.FullName -AclObject $security }
        else { Set-Acl -LiteralPath $entry.FullName -AclObject $fileSecurity }
    }
    [IO.File]::WriteAllText((Join-Path $destination 'collector-users.txt'),$AuthorizedUserSid + "`n",[Text.UTF8Encoding]::new($false))
    $binary = '"' + (Join-Path $destination 'VpcCollectorService.exe') + '"'
    Invoke-ServiceCommand -CommandArguments @('create',$serviceName,'binPath=',$binary,'start=','demand','obj=','LocalSystem')
    $createdService = $true
    Invoke-ServiceCommand -CommandArguments @('start',$serviceName)
    $service = Get-Service -Name $serviceName
    $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(30))
    $outcome.Installed = $true
    Write-InstallationReceipt
    $wait = [Diagnostics.Stopwatch]::StartNew()
    $restartRequest = Join-Path (Split-Path -Parent $FinishSignal) 'restart.request'
    $restarted = $false
    while (-not (Test-Path -LiteralPath $FinishSignal) -and $wait.Elapsed.TotalMinutes -lt 8) {
        if (-not $restarted -and (Test-Path -LiteralPath $restartRequest)) {
            if ((Get-Item -LiteralPath $restartRequest).Length -gt 64) { throw 'Restart verification marker exceeds its size limit.' }
            $requestedSession = [IO.File]::ReadAllText($restartRequest)
            $sessionValue = [guid]::Empty
            if (-not [guid]::TryParseExact($requestedSession,'N',[ref]$sessionValue)) { throw 'Invalid restart verification session.' }
            Invoke-ServiceCommand -CommandArguments @('stop',$serviceName)
            $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(160))
            Invoke-ServiceCommand -CommandArguments @('start',$serviceName)
            $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(30))
            $restartReceipt = Join-Path $destination 'validation-restarted.json'
            $staged = $restartReceipt + '.tmp'
            [IO.File]::WriteAllText($staged,(@{ SessionId = $requestedSession } | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
            [IO.File]::Move($staged,$restartReceipt)
            $restarted = $true
        }
        Start-Sleep -Milliseconds 200
    }
} catch {
    $outcome.Error = $_.Exception.ToString()
    $serviceErrors = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1001; StartTime = $startedAt } -MaxEvents 30 -ErrorAction SilentlyContinue | Where-Object ProviderName -EQ 'Valheim Performance Companion Collector')
    foreach ($serviceError in $serviceErrors) { foreach ($detail in $serviceError.Properties) { $outcome.Error += "`n" + [string]$detail.Value } }
} finally {
    if ($createdService) {
        try {
            Invoke-ServiceCommand -CommandArguments @('stop',$serviceName) -AllowedExitCodes @(0,1062)
            $service = Get-Service -Name $serviceName
            $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(160))
            Invoke-ServiceCommand -CommandArguments @('delete',$serviceName)
            $outcome.ServiceRemoved = $true
        } catch { $outcome.Error = ([string]$outcome.Error + "`nCleanup: " + $_.Exception.ToString()).Trim() }
    }
    if (Test-Path -LiteralPath $destination) {
        Write-InstallationReceipt
    }
}
if ($outcome.Error) { exit 1 }
