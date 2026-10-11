$app = Join-Path $PSScriptRoot 'AshleyApp-HUD-Updated\Ashley.exe'
if (-not (Test-Path -LiteralPath $app)) {
    Write-Error 'Ashley is not built yet. Run Build-Ashley.ps1 first.'
    exit 1
}

Start-Process -FilePath $app
