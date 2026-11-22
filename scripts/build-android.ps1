$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $Root
try {
  if (Test-Path ".\gradlew.bat") {
    .\gradlew.bat :android:app:assembleDebug
  } elseif (Get-Command gradle -ErrorAction SilentlyContinue) {
    gradle :android:app:assembleDebug
  } else {
    Write-Host "Gradle is not installed and this scaffold does not include a generated wrapper."
    Write-Host "Open the project root in Android Studio or install Gradle, then run :android:app:assembleDebug."
    exit 2
  }
} finally {
  Pop-Location
}
