[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请由用户在管理员 PowerShell 中运行此脚本。脚本不会自行提权。'
}
$repo = Split-Path -Parent $PSScriptRoot
$helper = Join-Path $repo 'third_party/PresentMon/PresentMon.exe'
$output = Join-Path $repo ('build/.cache/fps-diagnostic/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$results = foreach ($mode in @('display', 'present-only')) {
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
    [pscustomobject]@{ Mode=$mode; ExitCode=$process.ExitCode; TimedOut=$timedOut; Rows=$rows.Count
        EventsLost=$(if ($lost.Success) { [long]$lost.Groups[1].Value } else { 0 })
        Arguments=$arguments; Applications=@($rows.Application | Sort-Object -Unique); Error=$errorText }
    $process.Dispose()
}
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
$results | Select-Object Mode, ExitCode, TimedOut, Rows, EventsLost | Format-Table
Write-Host "诊断结果：$output"
