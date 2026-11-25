[CmdletBinding()]
param([string]$Model = 'qwen3.5:9b-64k', [string]$OutputDirectory, [string]$GatewayEndpoint = 'http://127.0.0.1:11435')
$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'build/model-control/evidence' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromMinutes(5)
$endpoint = $GatewayEndpoint.TrimEnd('/')
$checks = [Collections.Generic.List[object]]::new()
function PostAsync($route, $body, $token = [Threading.CancellationToken]::None) {
    $content = [Net.Http.StringContent]::new(($body | ConvertTo-Json -Depth 12 -Compress), [Text.Encoding]::UTF8, 'application/json')
    $client.PostAsync("$endpoint/$route", $content, $token)
}
function Await($task) {
    $response = $task.GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) { throw "$($response.StatusCode): $body" }
    $response.Dispose(); return $body
}
function Request($text, $max = 32) { @{ model=$Model; messages=@(@{role='user';content=$text});stream=$true;max_tokens=$max;reasoning_effort='none' } }
function Check($ok, $label, $data = $null) {
    if (-not $ok) { throw $label }; Write-Output "PASS $label"; $checks.Add(@{label=$label;data=$data})
}
try {
    $before = @((Invoke-RestMethod http://127.0.0.1:11434/api/tags).models.name)
    foreach ($n in @(65536,131072,204800)) {
        $null=Await (PostAsync 'control/settings' @{model=$Model;context=$n})
        $reply=Await (PostAsync 'v1/chat/completions' (Request 'Reply only OK.'))
        $actual=(Invoke-RestMethod http://127.0.0.1:11434/api/ps).models | Where-Object name -EQ $Model
        Check ($actual.context_length -eq $n -and $reply.Contains('[DONE]')) "live streaming context $n" @{actual=$actual.context_length}
    }
    $null=Await (PostAsync 'control/settings' @{model=$Model;context=65536})
    $long = Request 'Write all integers from 1 to 300, separated by commas.' 1200
    $first=PostAsync 'v1/chat/completions' $long
    $second=PostAsync 'v1/chat/completions' (Request 'Reply only OK.')
    $queued=$false
    for ($i=0;$i -lt 50;$i++) {
        $state=Invoke-RestMethod "$endpoint/control/status"
        if ($state.queued -gt 0) { $queued=$true; break }; if ($first.IsCompleted) { break }; Start-Sleep -Milliseconds 50
    }
    $null=Await $first; $null=Await $second
    Check $queued 'simultaneous requests queue'
    $first=PostAsync 'v1/chat/completions' $long
    for ($i=0;$i -lt 50;$i++) { if ((Invoke-RestMethod "$endpoint/control/status").active) { break }; Start-Sleep -Milliseconds 50 }
    $unload=PostAsync 'control/unload' @{model=$Model -replace '-\d+k$',''}
    Start-Sleep -Milliseconds 100
    Check (-not $unload.IsCompleted) 'unload waits live inference'
    $null=Await $first; $null=Await $unload
    Check (@((Invoke-RestMethod http://127.0.0.1:11434/api/ps).models).Count -eq 0) 'family unload releases resident alias'
    Check ((Invoke-RestMethod "$endpoint/control/status").online) 'Ollama stays online after unload'
    $null=Await (PostAsync 'v1/chat/completions' (Request 'Reply only OK.'))
    Check (@((Invoke-RestMethod http://127.0.0.1:11434/api/ps).models).Count -gt 0) 'next request reloads model'
    $cancel=[Threading.CancellationTokenSource]::new()
    $first=PostAsync 'v1/chat/completions' $long $cancel.Token
    for ($i=0;$i -lt 50;$i++) { if ((Invoke-RestMethod "$endpoint/control/status").active) { break }; Start-Sleep -Milliseconds 50 }
    $cancel.Cancel()
    try { $null=Await $first; throw 'cancel did not abort request' } catch { if ($_.Exception.ToString() -notmatch 'cancel') { throw } }
    $null=Await (PostAsync 'v1/chat/completions' (Request 'Reply only OK.'))
    Check (-not (Invoke-RestMethod "$endpoint/control/status").active) 'cancel releases live inference queue'
    $after=@((Invoke-RestMethod http://127.0.0.1:11434/api/tags).models.name)
    Check (-not (Compare-Object $before $after)) 'no model tags created or deleted'
} finally {
    $checks | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory 'live-gateway.json') -Encoding utf8NoBOM
    $client.Dispose()
}
