$ErrorActionPreference = "Stop"
$Project = Join-Path $PSScriptRoot "..\windows\TetherBridge.Desktop\TetherBridge.Desktop.csproj"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  Write-Host "dotnet SDK is not installed. Install the .NET 8 SDK on Windows, then rerun this script."
  exit 2
}
$Sdks = dotnet --list-sdks
if (-not $Sdks) {
  Write-Host "dotnet is present, but no SDK is installed. Install the .NET 8 SDK, not just the runtime, then rerun this script."
  exit 2
}
dotnet build $Project -c Release
