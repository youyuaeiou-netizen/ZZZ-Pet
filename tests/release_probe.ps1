[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExePath)

$ErrorActionPreference = 'Stop'
$appRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $appRoot ('build/runs/light-tools-release-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
New-Item -ItemType Directory -Path $runRoot | Out-Null
$PSVersionTable.PSVersion
(Get-Process -Id $PID).Path

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class PetReleaseWindows {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder text, int size);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static Dictionary<IntPtr,string> List(int pid) {
        var found = new Dictionary<IntPtr,string>();
        EnumWindows((h,l) => {
            GetWindowThreadProcessId(h, out var owner);
            if (owner == pid && IsWindowVisible(h)) {
                var text = new StringBuilder(256); GetWindowText(h, text, text.Capacity);
                found[h] = text.ToString();
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

$results = @()
foreach ($case in @('normal', 'invalid-tools')) {
    $dataRoot = Join-Path $runRoot $case
    New-Item -ItemType Directory -Path $dataRoot | Out-Null
    $toolPath = Join-Path $dataRoot 'tools-state.json'
    if ($case -eq 'invalid-tools') { [IO.File]::WriteAllText($toolPath, '{broken') }
    $process = Start-Process -FilePath $ExePath -ArgumentList @('--data-dir', ('"' + $dataRoot + '"')) -WindowStyle Hidden -PassThru
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(8)
        $windows = $null
        do {
            Start-Sleep -Milliseconds 200
            $process.Refresh()
            if ($process.HasExited) { throw "测试进程提前退出：$case" }
            $windows = [PetReleaseWindows]::List($process.Id)
        } until (($windows.Values -contains 'DesktopPet') -or [DateTime]::UtcNow -gt $deadline)
        if ($windows.Values -notcontains 'DesktopPet') { throw "桌宠窗口未出现：$case" }
        if ($case -eq 'invalid-tools') {
            Start-Sleep -Milliseconds 1400
            $windows = [PetReleaseWindows]::List($process.Id)
            if ($windows.Values -notcontains '艾莲布 · 对话') { throw '损坏工具数据未显示错误气泡。' }
        }
        $main = @($windows.GetEnumerator() | Where-Object Value -EQ 'DesktopPet')[0].Key
        [PetReleaseWindows]::PostMessage($main, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        if (-not $process.WaitForExit(5000)) { throw "测试进程未正常关闭：$case" }
        if ($process.ExitCode -ne 0) { throw "测试进程退出码异常：$($process.ExitCode)" }
        if (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'settings.json'))) { throw '隔离设置未写入。' }
        if ($case -eq 'invalid-tools') {
            if ([IO.File]::ReadAllText($toolPath) -cne '{broken') { throw '损坏工具数据被覆盖。' }
        } else {
            $state = Get-Content -LiteralPath $toolPath -Raw | ConvertFrom-Json
            if ($state.Focus.Running -or $state.SoundEnabled) { throw '首次退出状态或默认声音不正确。' }
        }
        $results += [ordered]@{ case = $case; status = 'PASS'; exitCode = $process.ExitCode; visibleWindows = @($windows.Values) }
    } finally {
        $process.Refresh()
        if (-not $process.HasExited) {
            foreach ($handle in [PetReleaseWindows]::List($process.Id).Keys) {
                [PetReleaseWindows]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            }
            $null = $process.WaitForExit(5000)
        }
        $process.Dispose()
    }
}
[ordered]@{ exe = $ExePath; version = (Get-Item -LiteralPath $ExePath).VersionInfo.ProductVersion; results = $results } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'verification.json') -Encoding utf8NoBOM
Write-Output "PASS: self-contained release startup, clean exit, isolated data and corrupt tools preservation. Evidence: $runRoot"

