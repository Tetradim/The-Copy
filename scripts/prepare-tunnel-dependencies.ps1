param([string]$Destination)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Destination)) { $Destination = Join-Path $PSScriptRoot '..\third_party\windows-amd64-v2' }
$destination = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar)
$parent = Split-Path -Parent $destination
if (Test-Path -LiteralPath $destination) { throw 'Dependency destination exists; it was not overwritten.' }
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$staging = Join-Path $parent ('.tb-deps-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    $wintunZip = Join-Path $staging 'wintun.zip'; $tunZip = Join-Path $staging 'tun2socks.zip'
    Invoke-WebRequest 'https://www.wintun.net/builds/wintun-0.14.1.zip' -OutFile $wintunZip
    Invoke-WebRequest 'https://github.com/xjasonlyu/tun2socks/releases/download/v2.7.0/tun2socks-windows-amd64.zip' -OutFile $tunZip
    if ((Get-FileHash -LiteralPath $wintunZip -Algorithm SHA256).Hash -ne '07C256185D6EE3652E09FA55C0B673E2624B565E02C4B9091C79CA7D2F24EF51') { throw 'Wintun archive hash mismatch.' }
    if ((Get-FileHash -LiteralPath $tunZip -Algorithm SHA256).Hash -ne 'C5D46E9452F6C9CC7C15AB9158D6D6A0169CEECD6BCA019CE476B49337D2BE43') { throw 'tun2socks archive hash mismatch.' }
    Expand-Archive -LiteralPath $wintunZip -DestinationPath (Join-Path $staging 'wintun')
    Expand-Archive -LiteralPath $tunZip -DestinationPath (Join-Path $staging 'tun2socks')
    $wintunDll = Join-Path $staging 'wintun\wintun\bin\amd64\wintun.dll'
    if ((Get-AuthenticodeSignature -LiteralPath $wintunDll).Status -ne 'Valid') { throw 'Wintun DLL signature verification failed.' }
    $publish = Join-Path $staging 'publish'; New-Item -ItemType Directory -Path $publish | Out-Null
    Copy-Item -LiteralPath $wintunDll -Destination (Join-Path $publish 'wintun.dll')
    Copy-Item -LiteralPath (Join-Path $staging 'wintun\wintun\LICENSE.txt') -Destination (Join-Path $publish 'WINTUN-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $staging 'tun2socks\tun2socks-windows-amd64.exe') -Destination (Join-Path $publish 'tun2socks.exe')
    Invoke-WebRequest 'https://raw.githubusercontent.com/xjasonlyu/tun2socks/v2.7.0/LICENSE' -OutFile (Join-Path $publish 'TUN2SOCKS-LICENSE.txt')
    $hashes = @{
        'wintun.dll'='E5DA8447DC2C320EDC0FC52FA01885C103DE8C118481F683643CACC3220DAFCE'
        'tun2socks.exe'='076B3C3D6A372BAE3F49F2B415A4105F70C30A3ED3CAAED7979390E649892559'
        'WINTUN-LICENSE.txt'='183ADAC21E7D96C508C8FD34D394B7B6708BC81564AD1BAD61AB66143A008CD2'
        'TUN2SOCKS-LICENSE.txt'='796ABBF6D0258A01321F88F72C1D712417ACDD75342510F0796F7C567B6F05E5'
    }
    foreach ($name in $hashes.Keys) { if ((Get-FileHash -LiteralPath (Join-Path $publish $name) -Algorithm SHA256).Hash -ne $hashes[$name]) { throw "Dependency verification failed: $name" } }
    Move-Item -LiteralPath $publish -Destination $destination
    Write-Host "Verified dependencies saved to $destination. No driver has been installed."
} finally {
    if (Test-Path -LiteralPath $staging) {
        $resolved = (Resolve-Path -LiteralPath $staging).Path
        if ($resolved -eq $staging -and (Split-Path -Parent $resolved) -eq $parent -and (Split-Path -Leaf $resolved) -match '^\.tb-deps-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        } else { throw 'Refusing to remove unexpected dependency staging path.' }
    }
}
