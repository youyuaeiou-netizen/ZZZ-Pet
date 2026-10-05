param([string]$OutputPath = "build/generated/ellen-flat2d")

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$output = Join-Path $repoRoot $OutputPath
if (Test-Path -LiteralPath $output) {
    throw "Output already exists; move or rename it before rebuilding: $output"
}
Push-Location $repoRoot
try {
    python tools\build_flat2d_pack.py build --jobs workflow\video-jobs.json --output $output
    if ($LASTEXITCODE -ne 0) {
        throw "Animation build failed with exit code $LASTEXITCODE"
    }
    Write-Output "Generated pack: $output"
} finally {
    Pop-Location
}
