# Builds the three sandbox images (no push). Usage:
#   deploy/build.ps1 -Registry ghcr.io/andradev-io -Tag 0.1.0 [-Engine podman]
param(
    [Parameter(Mandatory)] [string] $Registry,
    [Parameter(Mandatory)] [string] $Tag,
    [string] $Engine = 'docker'
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

if (-not (Test-Path demo-data)) { throw 'demo-data/ is missing: the API image seeds the visitors'' workspaces from it' }

function Build([string[]] $BuildArgs) {
    & $Engine build @BuildArgs
    if ($LASTEXITCODE -ne 0) { throw "$Engine build failed" }
}

Build @('-f', 'deploy/Dockerfile.api-demo', '-t', "$Registry/config-server-api-demo:$Tag", '.')
Build @('--build-arg', 'VITE_API_BASE_URL=/', '-t', "$Registry/config-server-web:$Tag", 'src/Web')
Build @('-t', "$Registry/config-server-demo-idp:$Tag", 'tools/demo-idp')

Write-Host "`nBuilt:"
"api-demo", "web", "demo-idp" | ForEach-Object { Write-Host "  $Registry/config-server-${_}:$Tag" }
