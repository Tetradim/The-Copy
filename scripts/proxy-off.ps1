$ErrorActionPreference = "Stop"
$RegPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings"

Write-Host "Disabling current-user WinINet proxy. Prefer using the app restore button when available."
Set-ItemProperty -Path $RegPath -Name ProxyEnable -Value 0
Set-ItemProperty -Path $RegPath -Name ProxyServer -Value ""

Write-Host "Proxy disabled. Open Windows Settings if another app manages proxy policy."
