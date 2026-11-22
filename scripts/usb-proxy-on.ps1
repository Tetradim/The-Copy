$ErrorActionPreference = "Stop"
$PhonePort = 10890
$LocalRelayPort = 10809

Write-Host "Starting USB forward to phone bridge..."
adb forward --no-rebind "tcp:$PhonePort" "tcp:$PhonePort"

Write-Host "Windows should point to local relay, not directly to phone:"
Write-Host "  HTTP/HTTPS proxy: 127.0.0.1:$LocalRelayPort"
Write-Host "The Windows companion saves and restores proxy settings automatically."
