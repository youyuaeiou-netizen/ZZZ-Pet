[CmdletBinding()]
param([string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'build/releases' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
[xml]$props = Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw
$version = [string]$props.Project.PropertyGroup.Version
$name = "DesktopPet-$version-win-x64"
$folder = Join-Path $OutputRoot $name
$zip = "$folder.zip"
if ((Test-Path $folder) -or (Test-Path $zip)) { throw '发布产物已存在，请使用新的输出目录；不覆盖旧包。' }
$artifacts = Join-Path $OutputRoot 'artifacts'
$publish = Join-Path $artifacts 'publish'
$worker = Join-Path $artifacts 'worker'
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
& dotnet publish (Join-Path $repoRoot 'src/DesktopPet/DesktopPet.csproj') -c Release -r win-x64 --self-contained true --artifacts-path $artifacts -p:StageReferencedCharacters=true -p:ShareDesktopRuntime=true -o $publish
if ($LASTEXITCODE -ne 0) { throw '桌宠构建失败。' }
& dotnet publish (Join-Path $repoRoot 'src/DesktopPet.MonitorWorker/DesktopPet.MonitorWorker.csproj') -c Release -r win-x64 --self-contained true --artifacts-path $artifacts -p:RestoreLockedMode=true -p:ShareDesktopRuntime=true -o $worker
if ($LASTEXITCODE -ne 0) { throw '监控后台构建失败。' }
foreach ($file in Get-ChildItem $worker -Recurse -File | Where-Object Extension -NE '.pdb') {
    $relative = [IO.Path]::GetRelativePath($worker, $file.FullName)
    $target = Join-Path $publish $relative
    if (Test-Path $target) {
        if ((Get-FileHash $target).Hash -ne (Get-FileHash $file.FullName).Hash) { throw "共享运行库不一致：$relative" }
    } else {
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
New-Item -ItemType Directory -Path $folder | Out-Null
foreach ($file in Get-ChildItem $publish -Recurse -File | Where-Object Extension -NE '.pdb') {
    $relative = [IO.Path]::GetRelativePath($publish, $file.FullName)
    if ($relative -match '^characters[\\/]') { throw 'publish 不应带入未筛选角色。' }
    $target = Join-Path $folder $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
# Only the approved 2D pack is distributed.
$packRoot = Join-Path $repoRoot 'characters/ellen-flat2d'
$manifestPath = Join-Path $packRoot 'character.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$frames = @($manifest.actions.frames.path | Sort-Object -Unique)
if ($frames.Count -ne 540 -or $manifest.packVersion -ne '0.4.1') { throw '批准的 2D 帧包不匹配。' }
$targetPack = Join-Path $folder 'characters/ellen-flat2d'
New-Item -ItemType Directory -Path $targetPack -Force | Out-Null
Copy-Item -LiteralPath $manifestPath -Destination $targetPack
foreach ($relative in $frames) {
    $source = [IO.Path]::GetFullPath((Join-Path $packRoot $relative))
    if (-not $source.StartsWith($packRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetExtension($source) -ne '.png') { throw "帧路径越界：$relative" }
    $target = Join-Path $targetPack $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
Copy-Item (Join-Path $repoRoot 'LICENSE') $folder
Copy-Item (Join-Path $repoRoot 'workflow/LICENSE-ASSETS.md') (Join-Path $folder 'LICENSE-ASSETS.md')
Copy-Item (Join-Path $repoRoot 'third_party') $folder -Recurse
Copy-Item (Join-Path $repoRoot 'docs/朋友试用说明.md') (Join-Path $folder '朋友试用说明.txt')
@'
@echo off
start "" "%~dp0DesktopPet.exe" --character ellen-flat2d
'@ | Set-Content (Join-Path $folder 'Start-2D-Pet.cmd') -Encoding ascii
# Preserve notices for the exact restored dependencies and self-contained runtime.
$assets = Get-Content (Join-Path $artifacts 'obj/DesktopPet.MonitorWorker/project.assets.json') -Raw | ConvertFrom-Json
$appAssets = Get-Content (Join-Path $artifacts 'obj/DesktopPet/project.assets.json') -Raw | ConvertFrom-Json
$packageRoot = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
$deps = Get-Content (Join-Path $publish 'DesktopPet.deps.json') -Raw | ConvertFrom-Json
$runtimePackages = @($deps.libraries.PSObject.Properties.Name | Where-Object { $_.StartsWith('runtimepack.') } | ForEach-Object {
    $identity = $_.Substring('runtimepack.'.Length)
    [pscustomobject]@{ Name = $identity; Value = [pscustomobject]@{ type = 'package'; path = $identity.ToLowerInvariant() } }
})
$notices = Join-Path $folder 'third_party/dependency-notices'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
$packages = @($assets.libraries.PSObject.Properties) + @($appAssets.libraries.PSObject.Properties) + $runtimePackages
$noticeManifest = @($packages | Where-Object { $_.Value.type -eq 'package' } | Sort-Object Name -Unique | ForEach-Object {
    $packagePath = Join-Path $packageRoot $_.Value.path
    $spec = Get-ChildItem $packagePath -Filter '*.nuspec' | Select-Object -First 1
    if (-not $spec) { throw "依赖元数据缺失：$($_.Name)" }
    $id = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($_.Name))).Substring(0,16).ToLowerInvariant()
    $target = Join-Path $notices $id
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item $spec.FullName (Join-Path $target 'metadata.nuspec')
    Get-ChildItem $packagePath -File | Where-Object Name -Match 'license|notice|copying' | ForEach-Object { Copy-Item $_.FullName $target }
    [ordered]@{ package = $_.Name; noticeDirectory = $id }
})
if (-not ($noticeManifest.package -match '^Microsoft.NETCore.App.Runtime.win-x64/') -or -not ($noticeManifest.package -match '^Microsoft.WindowsDesktop.App.Runtime.win-x64/')) { throw '运行时许可清单缺失。' }
$noticeManifest | ConvertTo-Json | Set-Content (Join-Path $notices 'packages.json') -Encoding utf8NoBOM
$inventory = @(Get-ChildItem $folder -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($folder, $_.FullName).Replace('\','/'); bytes = $_.Length; sha256 = (Get-FileHash $_.FullName).Hash.ToLowerInvariant() }
})
[ordered]@{ version = $version; defaultCharacter = 'ellen-flat2d'; selfContained = $true; characters = @([ordered]@{ directory = 'ellen-flat2d'; packVersion = $manifest.packVersion; frameCount = $frames.Count }); files = $inventory } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $folder 'release-manifest.json') -Encoding utf8NoBOM
Compress-Archive -LiteralPath $folder -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zip))" | Set-Content "$zip.sha256" -Encoding utf8NoBOM
Write-Output "ZIP: $zip"
Write-Output "SHA256: $hash"
