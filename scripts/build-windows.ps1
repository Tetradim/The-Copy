$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'windows\TetherBridge.Desktop\TetherBridge.Desktop.csproj'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 10 SDK for Windows and run this script again.'
}
Push-Location $repo
try {
    $sdk = dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.') {
        throw 'Install a .NET 10 SDK matching global.json and run this script again.'
    }
    dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally {
    Pop-Location
}
