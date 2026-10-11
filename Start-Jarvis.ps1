$nodeCommand = Get-Command node -ErrorAction SilentlyContinue
if (-not $nodeCommand) {
    Write-Error 'Node.js is required to run this prototype. Install the current Node.js LTS release, then run this file again.'
    exit 1
}

Set-Location -LiteralPath $PSScriptRoot
& $nodeCommand.Source (Join-Path $PSScriptRoot 'JarvisServer.js')
