<#
    Rebuilds the whole app: React UI into the API's wwwroot, then publishes
    the API to dist/. Run this after changing any source.
#>

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot

Write-Host ''
Write-Host '  Building CatMeme Maker' -ForegroundColor Magenta
Write-Host '  ---------------------------------' -ForegroundColor DarkGray

Write-Host '  [1/2] Building web UI...' -ForegroundColor Gray
Push-Location (Join-Path $Root 'web')
try {
    if (-not (Test-Path 'node_modules')) {
        Write-Host '        installing npm packages...' -ForegroundColor DarkGray
        & npm install
        if ($LASTEXITCODE -ne 0) { throw 'npm install failed' }
    }
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw 'web build failed' }
}
finally { Pop-Location }

Write-Host '  [2/2] Publishing API...' -ForegroundColor Gray
Push-Location (Join-Path $Root 'src\CatMeme.Api')
try {
    & dotnet publish -c Release -o (Join-Path $Root 'dist') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
}
finally { Pop-Location }

# The published app reads .env from its own folder.
$envSrc = Join-Path $Root '.env'
if (Test-Path $envSrc) {
    Copy-Item $envSrc (Join-Path $Root 'dist\.env') -Force
}

Write-Host ''
Write-Host '  Build complete.' -ForegroundColor Green
Write-Host ''
