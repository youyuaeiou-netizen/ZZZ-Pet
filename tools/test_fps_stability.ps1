[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputRoot, [ValidateRange(150, 1800)][int]$Seconds = 300,
    [string]$DotnetRoot = 'C:\pet\dev-tools\dotnet')
$ErrorActionPreference = 'Stop'
if (-not [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请在管理员 PowerShell 中运行；脚本不会自行提权。'
}
$repo = Split-Path -Parent $PSScriptRoot
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw '证据目录已存在，请指定新目录。' }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$env:DOTNET_ROOT = $DotnetRoot
$render = $null
function Start-Renderer([string]$label, [int]$duration) {
    Start-Process pwsh.exe -ArgumentList @('-NoProfile', '-File', "$repo\tests\fps_present_probe.ps1", '-Seconds', $duration) -WindowStyle Hidden -RedirectStandardOutput "$OutputRoot\$label-render.txt" -RedirectStandardError "$OutputRoot\$label-render-error.txt" -PassThru
}
try {
    'sustained capture and faults' | Set-Content "$OutputRoot\stage.txt"
    $render = Start-Renderer 'stability' ($Seconds + 15)
    Start-Sleep -Seconds 2
    $test = Start-Process "$repo\tests\DesktopPet.Fps.Tests\bin\Release\net10.0-windows\DesktopPet.Fps.Tests.exe" -ArgumentList @('--live', $OutputRoot, $render.Id, $Seconds) -WindowStyle Hidden -RedirectStandardOutput "$OutputRoot\test.txt" -RedirectStandardError "$OutputRoot\test-error.txt" -PassThru
    if (-not $test.WaitForExit(($Seconds + 40) * 1000)) { $test.Kill(); throw 'FPS 可靠性验收超时。' }
    if ($test.ExitCode -ne 0) { throw "FPS 可靠性验收失败：$($test.ExitCode)" }
    if (-not $render.WaitForExit(40000)) { throw '渲染窗口退出超时。' }
    'production worker and taskbar display' | Set-Content "$OutputRoot\stage.txt"
    $render = Start-Renderer 'ui' 35
    Start-Sleep -Seconds 2
    $ui = Start-Process "$repo\tests\DesktopPet.MonitorUi\bin\Release\net10.0-windows\win-x64\DesktopPet.MonitorUi.exe" -ArgumentList @("$OutputRoot\ui", '--fps-live', $render.Id) -WindowStyle Hidden -RedirectStandardOutput "$OutputRoot\ui-test.txt" -RedirectStandardError "$OutputRoot\ui-test-error.txt" -PassThru
    if (-not $ui.WaitForExit(35000)) { $ui.Kill(); throw '真实任务栏显示验收超时。' }
    if ($ui.ExitCode -ne 0) { throw "真实任务栏显示验收失败：$($ui.ExitCode)" }
    if (-not $render.WaitForExit(40000)) { throw '渲染窗口退出超时。' }
    'passed' | Set-Content "$OutputRoot\stage.txt"
} catch { $_ | Out-String | Set-Content "$OutputRoot\failure.txt"; 'failed' | Set-Content "$OutputRoot\stage.txt"; exit 1 }
finally { if ($render -and -not $render.HasExited) { $render.Kill() } }
