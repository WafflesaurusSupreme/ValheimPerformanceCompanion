[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Standard','Deep')][string]$Mode, [ValidateRange(5,90)][int]$SampleSeconds=10)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$games=@(Get-Process -Name valheim -ErrorAction Stop)
if($games.Count -ne 1){throw 'Expected one running game for the live capture check.'}
$gameId=$games[0].Id
function Invoke-Control([string]$Endpoint,[string]$Command,[int]$TimeoutSeconds=180) {
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',('ValheimPerformanceCompanion.'+$Endpoint+'.'+$gameId),[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
    $reader=$null;$writer=$null
    try {
        $pipe.Connect(3000)
        $writer=[IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),1024,$true);$writer.AutoFlush=$true
        $reader=[IO.StreamReader]::new($pipe,[Text.Encoding]::UTF8,$false,1024,$true)
        $writer.WriteLine($Command)
        $pending=$reader.ReadLineAsync()
        if(-not $pending.Wait($TimeoutSeconds*1000)){throw "$Endpoint request timed out: $Command"}
        if(-not $pending.Result){throw "$Endpoint closed without a response"}
        return $pending.Result
    }
    finally {if($reader){$reader.Dispose()};if($writer){$writer.Dispose()};$pipe.Dispose()}
}
$status=Invoke-Control 'Recorder' 'status' 15 | ConvertFrom-Json
if($status.State -ne 'Idle'){throw 'A capture is already active; preserve it.'}
$start=Invoke-Control 'Controller' ('start '+$Mode.ToLowerInvariant())
Write-Output $start
if(-not $start.StartsWith('Capture started:')){throw "Capture start failed: $start"}
$stopped=$false
try {
    Start-Sleep -Seconds $SampleSeconds
    $recording=Invoke-Control 'Recorder' 'status' 15 | ConvertFrom-Json
    if($recording.State -ne 'Recording' -or $recording.Frames -lt 2){throw 'Recorder did not produce frames'}
    "Recording $Mode frames=$($recording.Frames)"
    $stop=Invoke-Control 'Controller' 'stop';Write-Output $stop
    if(-not $stop.StartsWith('Capture stopped.')){throw "Capture finalization failed: $stop"}
    $stopped=$true
    $directory=$recording.Directory
    $required=@('recorder.json','configuration.json','completed.json','frames.csv','collector-result.json','cpu-samples.csv')
    if($Mode -eq 'Deep'){$required+=@('details.csv','methods.csv','characters.csv')}
    foreach($name in $required){if(-not(Test-Path -LiteralPath (Join-Path $directory $name))){throw "Missing capture output: $name"}}
    $metadata=Get-Content -LiteralPath (Join-Path $directory 'recorder.json') -Raw | ConvertFrom-Json
    $collector=Get-Content -LiteralPath (Join-Path $directory 'collector-result.json') -Raw | ConvertFrom-Json
    $completion=Get-Content -LiteralPath (Join-Path $directory 'completed.json') -Raw | ConvertFrom-Json
    if($metadata.Mode -ne $Mode -or $metadata.PluginVersion -ne '0.1.4' -or -not $collector.Success -or $completion.State -ne 'Complete'){throw 'Capture receipts did not confirm success'}
    $evidence=[ordered]@{Mode=$Mode;ProcessId=$gameId;Directory=$directory;Frames=$completion.Frames;Dropped=$completion.DroppedFrames;Collector=$collector.Message;Version=$metadata.PluginVersion;VerifiedUtc=[DateTime]::UtcNow.ToString('O')}
    $path=Join-Path $PSScriptRoot ('..\artifacts\live-'+$Mode.ToLowerInvariant()+'-014-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fffffff')+'.json')
    $evidence | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding utf8
    "PASS live $Mode capture: $directory"
}
finally {if(-not $stopped){Write-Output (Invoke-Control 'Controller' 'stop')}}
