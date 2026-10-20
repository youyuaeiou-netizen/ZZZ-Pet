[CmdletBinding()]
param(
    [string]$OutputRoot,
    [string]$Dotnet = 'dotnet',
    [string]$PluginVersion = '1.1.1',
    [string]$ReleaseTag
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'build/local-model-plugin' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (-not $ReleaseTag) { [xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props') -Raw; $ReleaseTag = 'v' + [string]$props.Project.PropertyGroup.Version }
$name = "pet-local-model-$PluginVersion-win-x64"
$zip = Join-Path $OutputRoot "$name.zip"
if (Test-Path -LiteralPath $zip) { throw '产物已存在，请选择新的输出目录。' }
$stage = Join-Path $OutputRoot $name
New-Item -ItemType Directory -Path $stage -Force | Out-Null
& $Dotnet publish (Join-Path $repo 'src/DesktopPet.ModelGateway/DesktopPet.ModelGateway.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $stage 'payload')
if ($LASTEXITCODE -ne 0) { throw '网关发布失败。' }
$files = [ordered]@{}
Get-ChildItem (Join-Path $stage 'payload') -Recurse -File | Where-Object Extension -NE '.pdb' | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/')
    $files[$relative] = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()
}
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $stage 'payload/LICENSE.txt')
$files['payload/LICENSE.txt'] = (Get-FileHash -LiteralPath (Join-Path $stage 'payload/LICENSE.txt')).Hash.ToLowerInvariant()
$assets = Get-Content (Join-Path $repo 'src/DesktopPet.ModelGateway/obj/project.assets.json') -Raw | ConvertFrom-Json
$packageRoot = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
$deps = Get-Content (Join-Path $stage 'payload/DesktopPet.ModelGateway.deps.json') -Raw | ConvertFrom-Json
foreach ($runtime in $deps.libraries.PSObject.Properties.Name | Where-Object { $_.StartsWith('runtimepack.') }) {
    $identity = $runtime.Substring('runtimepack.'.Length).ToLowerInvariant()
    $directory = Join-Path $packageRoot $identity
    $notices = @(Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -match 'license|notice|copying|\.nuspec$' })
    if ($notices.Count -eq 0) { throw "运行时许可元数据缺失：$identity" }
    foreach ($notice in $notices) {
        $relative = 'payload/third_party/' + $identity.Replace('/', '-') + '/' + $notice.Name
        $target = Join-Path $stage $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $notice.FullName -Destination $target
        $files[$relative] = (Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant()
    }
}
[ordered]@{ Id = 'pet-local-model'; Version = $PluginVersion; MinimumPetVersion = '0.3.1.5'; Files = $files } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'plugin.json') -Encoding utf8NoBOM
# Include only manifest-listed files, avoiding pdb or unpublished output.
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($relative in @('plugin.json') + @($files.Keys)) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $stage $relative), $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
$sha = (Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant()
[ordered]@{ Id = 'pet-local-model'; Version = $PluginVersion; Url = "https://github.com/youyuaeiou-netizen/ZZZ-Pet/releases/download/$ReleaseTag/$name.zip"; Sha256 = $sha } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'pet-local-model-catalog.json') -Encoding utf8NoBOM
"$sha  $name.zip" | Set-Content -LiteralPath "$zip.sha256" -Encoding utf8NoBOM
Write-Output "插件包：$zip"
Write-Output '发布时将 ZIP、SHA256 和 pet-local-model-catalog.json 上传到同一 Release；本脚本不上传。'
