$sdk = Join-Path $PSScriptRoot 'work\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) {
    $installedSdk = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($installedSdk) { $sdk = $installedSdk.Source }
}
if (-not (Test-Path -LiteralPath $sdk)) {
    Write-Error 'The .NET SDK is not available. Run the Ashley setup steps in README.md first.'
    exit 1
}

$env:NUGET_PACKAGES = Join-Path $PSScriptRoot 'work\nuget'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot 'work\dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:APPDATA = Join-Path $PSScriptRoot 'work\appdata\roaming'
$env:LOCALAPPDATA = Join-Path $PSScriptRoot 'work\appdata\local'
New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA | Out-Null
$project = Join-Path $PSScriptRoot 'DesktopApp\Ashley.Desktop.csproj'
$nugetConfig = Join-Path $PSScriptRoot 'DesktopApp\NuGet.Config'
$publish = Join-Path $PSScriptRoot 'AshleyApp-HUD-Latest'
& $sdk restore $project --configfile $nugetConfig
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $sdk publish $project --no-restore --configuration Release --runtime win-x64 --self-contained false --output $publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Ashley is ready: $publish\Ashley.exe" -ForegroundColor Green
