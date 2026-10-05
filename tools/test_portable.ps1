[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReleaseFolder, [Parameter(Mandatory)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$ReleaseFolder = (Resolve-Path -LiteralPath $ReleaseFolder).Path
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path $EvidenceRoot) { throw '证据目录已存在，请使用新目录，不覆盖已有用户设置。' }
New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PortableWindowProbe {
    private delegate bool EnumCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    public static IntPtr Find(int processId) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) => { GetWindowThreadProcessId(window, out uint id);
            if (id == processId && IsWindowVisible(window)) { found = window; return false; } return true; }, IntPtr.Zero);
        return found;
    }
    public static bool Close(IntPtr window) => PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
}
'@
$packs = @(Get-ChildItem (Join-Path $ReleaseFolder 'characters') -Directory)
if ($packs.Count -ne 1 -or $packs[0].Name -ne 'ellen-flat2d') { throw '便携包必须只包含批准的 2D 角色。' }
$manifest = Get-Content (Join-Path $packs[0].FullName 'character.json') -Raw | ConvertFrom-Json
$frames = @($manifest.actions.frames.path | Sort-Object -Unique)
if ($frames.Count -ne 540 -or $manifest.actions.Count -ne 11) { throw '帧数或动作数不一致。' }
$sourcePack = Join-Path $repoRoot 'characters/ellen-flat2d'
foreach ($relative in @('character.json') + $frames) {
    if ((Get-FileHash (Join-Path $sourcePack $relative)).Hash -ne (Get-FileHash (Join-Path $packs[0].FullName $relative)).Hash) { throw "批准素材不一致：$relative" }
}
$results = @()
foreach ($case in @('fresh', 'unavailable-selection')) {
    $data = Join-Path $EvidenceRoot $case
    New-Item -ItemType Directory -Path $data | Out-Null
    if ($case -eq 'unavailable-selection') {
        '{"SchemaVersion":1,"SelectedCharacter":"unavailable-fixture","Scale":0.9,"AlwaysOnTop":false,"FuturePreference":{"enabled":true}}' | Set-Content (Join-Path $data 'settings.json') -Encoding utf8NoBOM
    }
    $exe = Join-Path $ReleaseFolder 'DesktopPet.exe'
    $process = Start-Process -FilePath $exe -ArgumentList @('--data-dir', ('"' + $data + '"')) -WorkingDirectory $ReleaseFolder -WindowStyle Hidden -PassThru
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 300
            $process.Refresh()
            if ($process.HasExited) { throw "启动失败：$case，退出代码 $($process.ExitCode)" }
            $logs = @(Get-ChildItem (Join-Path $data 'logs') -Filter '*.log' -ErrorAction SilentlyContinue)
            $shown = $logs.Count -gt 0 -and ((Get-Content $logs[0].FullName -Raw) -match '角色已显示：ellen-flat2d')
            $window = [PortableWindowProbe]::Find($process.Id)
        } until (($shown -and $window -ne 0) -or [DateTime]::UtcNow -ge $deadline)
        if (-not $shown -or $window -eq 0) { throw "未确认 2D 窗口显示：$case" }
        if (-not [PortableWindowProbe]::Close($window) -or -not $process.WaitForExit(10000)) { throw "测试进程不能正常退出：$case" }
        if ($process.ExitCode -ne 0) { throw "测试进程异常退出：$case" }
        $saved = Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json
        if ($saved.SelectedCharacter -ne 'ellen-flat2d') { throw "保存角色错误：$case" }
        if ($case -eq 'unavailable-selection' -and ($saved.Scale -ne 0.9 -or $saved.AlwaysOnTop -ne $false -or $saved.FuturePreference.enabled -ne $true)) { throw '用户的其他设置未被保留。' }
        $results += [ordered]@{ case = $case; selectedCharacter = $saved.SelectedCharacter; exitCode = $process.ExitCode; windowShown = $true }
    } finally {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id }
        $process.Dispose()
    }
}
[ordered]@{ actions = $manifest.actions.Count; framesMatched = $frames.Count; characters = @('ellen-flat2d'); startupChecks = $results; visualAcceptance = 'unchanged approved frames; final desktop appearance requires user confirmation' } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $EvidenceRoot 'verification.json') -Encoding utf8NoBOM
Write-Output 'PASS: 2D-only package, 540 approved frames identical, fresh startup and unavailable saved selection, normal exit and preserved unrelated preferences.'
