[CmdletBinding()]
param([string]$PetDirectory, [string]$GatewayExecutable)
$ErrorActionPreference = 'Stop'
if ($GatewayExecutable) { $exe = [IO.Path]::GetFullPath($GatewayExecutable) }
else {
    if (-not $PetDirectory) { throw '请指定 PetDirectory 或 GatewayExecutable。' }
    $exe = Join-Path ([IO.Path]::GetFullPath($PetDirectory)) 'model-gateway\DesktopPet.ModelGateway.exe'
}
if (-not (Test-Path -LiteralPath $exe)) { throw "缺少已发布的网关：$exe" }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path -Parent $exe)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
$principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName 'DesktopPet Local Model Gateway' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName 'DesktopPet Local Model Gateway'
if (Get-ScheduledTask -TaskName 'Ollama Local Model Server' -ErrorAction SilentlyContinue) {
    $ollamaAction = New-ScheduledTaskAction -Execute $exe -Argument '--ollama-service' -WorkingDirectory (Split-Path -Parent $exe)
    Set-ScheduledTask -TaskName 'Ollama Local Model Server' -Action $ollamaAction | Out-Null
}
Write-Output '已注册当前用户登录启动网关：127.0.0.1:11435。'
