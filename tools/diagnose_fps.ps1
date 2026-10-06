[CmdletBinding()]
param([switch]$Offline)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请由用户在管理员 PowerShell 中运行此脚本。脚本不会自行提权。'
}
$repo = Split-Path -Parent $PSScriptRoot
$helper = Join-Path $repo 'third_party/PresentMon/PresentMon.exe'
$output = Join-Path $repo ('build/.cache/fps-diagnostic/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$results = @()
$modes = if ($Offline) { @() } else { @('display', 'present-only') }
foreach ($mode in $modes) {
    $session = 'DesktopPet.FPS.' + [guid]::NewGuid().ToString('N')
    $csv = Join-Path $output "$mode.csv"
    $errorFile = Join-Path $output "$mode.stderr.txt"
    # FPS uses present intervals; GPU duration and input latency are unnecessary.
    # no_track_gpu is also required for no_track_display to take effect.
    $arguments = @('--session_name', $session, '--output_stdout', '--no_console_stats', '--v1_metrics', '--no_track_gpu', '--no_track_input', '--timed', '10', '--terminate_after_timed')
    if ($mode -eq 'present-only') { $arguments += '--no_track_display' }
    $process = Start-Process -FilePath $helper -ArgumentList $arguments -WindowStyle Hidden -RedirectStandardOutput $csv -RedirectStandardError $errorFile -PassThru
    $timedOut = -not $process.WaitForExit(25000)
    if ($timedOut) { $process.Kill(); $process.WaitForExit() }
    $rows = @(Import-Csv -LiteralPath $csv)
    $errorText = Get-Content -LiteralPath $errorFile -Raw
    $lost = [regex]::Match([string]$errorText, '(\d+) ETW events were lost')
    $results += [pscustomobject]@{ Mode=$mode; ExitCode=$process.ExitCode; TimedOut=$timedOut; Rows=$rows.Count
        EventsLost=$(if ($lost.Success) { [long]$lost.Groups[1].Value } else { 0 })
        Arguments=$arguments; Applications=@($rows.Application | Sort-Object -Unique); Error=$errorText }
    $process.Dispose()
}
if ($Offline) {
    # Own file-mode session only; never stop or reconfigure another application's trace.
    $session = 'DesktopPet.FPS.' + [guid]::NewGuid().ToString('N')
    $etl = Join-Path $output 'frames.etl'
    $providers = Join-Path $output 'providers.txt'
    @('{CA11C036-0102-4A2D-A6AD-F03CFED5D3C9} 0x2 5',
      '{783ACA0A-790E-4D7F-8451-AA850511C6B9} 0xffffffffffffffff 5') | Set-Content -LiteralPath $providers -Encoding ascii
    $created = $false
    try {
        $startLog = & logman.exe create trace $session -o $etl -f bincirc -max 16 -bs 64 -nb 16 64 -pf $providers -ets 2>&1
        $startLog | Set-Content -LiteralPath (Join-Path $output 'trace-start.txt')
        if ($LASTEXITCODE -ne 0) { throw "文件模式采集启动失败：$startLog" }
        $created = $true
        Write-Host '正在记录十秒帧事件，请保持视频播放。'
        Start-Sleep -Seconds 10
        & logman.exe query $session -ets 2>&1 | Set-Content -LiteralPath (Join-Path $output 'trace-statistics.txt')
    }
    finally {
        if ($created) { & logman.exe stop $session -ets 2>&1 | Set-Content -LiteralPath (Join-Path $output 'trace-stop.txt') }
    }
    $csv = Join-Path $output 'offline.csv'
    $errorFile = Join-Path $output 'offline.stderr.txt'
    $arguments = @('--etl_file', $etl, '--output_stdout', '--no_console_stats', '--v1_metrics', '--no_track_gpu', '--no_track_input', '--no_track_display')
    $process = Start-Process -FilePath $helper -ArgumentList $arguments -WindowStyle Hidden -RedirectStandardOutput $csv -RedirectStandardError $errorFile -PassThru
    $timedOut = -not $process.WaitForExit(25000)
    if ($timedOut) { $process.Kill(); $process.WaitForExit() }
    $rows = @(Import-Csv -LiteralPath $csv)
    $exitCode = $process.ExitCode; $process.Dispose()
    $eventsCsv = Join-Path $output 'events.csv'
    & tracerpt.exe $etl -o $eventsCsv -of CSV -summary (Join-Path $output 'events-summary.txt') -y 2>&1 | Set-Content -LiteralPath (Join-Path $output 'events-decode.txt')
    $decodeExit = $LASTEXITCODE
    $rawEvents = if (Test-Path -LiteralPath $eventsCsv) { @(Import-Csv -LiteralPath $eventsCsv).Count } else { 0 }
    $results += [pscustomobject]@{ Mode='offline'; ExitCode=$exitCode; TimedOut=$timedOut; Rows=$rows.Count; RawEvents=$rawEvents
        DecodeExitCode=$decodeExit; Arguments=$arguments; Applications=@($rows.Application | Sort-Object -Unique)
        Error=(Get-Content -LiteralPath $errorFile -Raw) }
}
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
$results | Select-Object Mode, ExitCode, TimedOut, Rows, EventsLost, RawEvents | Format-Table
Write-Host "诊断结果：$output"
