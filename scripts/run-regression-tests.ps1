$ErrorActionPreference = "Stop"
$Python = Get-Command python -ErrorAction SilentlyContinue
if (-not $Python) { $Python = Get-Command python3 -ErrorAction SilentlyContinue }
if (-not $Python) { $Python = Get-Command py -ErrorAction SilentlyContinue }
if (-not $Python) {
  Write-Host "Python was not found. Install Python 3 or run scripts/run-regression-tests.py manually." -ForegroundColor Red
  exit 2
}
& $Python.Source (Join-Path $PSScriptRoot "run-regression-tests.py")
