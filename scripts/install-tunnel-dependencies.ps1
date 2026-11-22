[CmdletBinding(SupportsShouldProcess=$true)]
param([string]$SourceDirectory)
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) { $SourceDirectory = Join-Path $PSScriptRoot '..\third_party\windows-amd64-v2' }
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this dependency installer as administrator.' }
$source = [IO.Path]::GetFullPath($SourceDirectory)
$programFiles = [Environment]::GetFolderPath('ProgramFiles')
$root = Join-Path $programFiles 'TetherBridge'; $destination = Join-Path $root 'Tunnel'
$expected = @{
    'tun2socks.exe'='076B3C3D6A372BAE3F49F2B415A4105F70C30A3ED3CAAED7979390E649892559'
    'wintun.dll'='E5DA8447DC2C320EDC0FC52FA01885C103DE8C118481F683643CACC3220DAFCE'
    'WINTUN-LICENSE.txt'='183ADAC21E7D96C508C8FD34D394B7B6708BC81564AD1BAD61AB66143A008CD2'
    'TUN2SOCKS-LICENSE.txt'='796ABBF6D0258A01321F88F72C1D712417ACDD75342510F0796F7C567B6F05E5'
}
$trusted = @('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
function Assert-NoReparse([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) { if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point in path: $cursor" } }
        $parent = Split-Path -Parent $cursor
        if (!$parent -or $parent -eq $cursor) { break }; $cursor = $parent
    }
}
function Assert-TrustedAcl([string]$Path, [bool]$IncludeInheritOnly) {
    $security = Get-Acl -LiteralPath $Path
    $owner = $security.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($trusted -notcontains $owner) { throw "Untrusted owner: $Path" }
    $rules = $security.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])
    $dangerous = [uint32]([Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership) -bor [uint32]0x50000000
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -ne 'Allow') { continue }
        if (!$IncludeInheritOnly -and ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly)) { continue }
        if (([uint32]$rule.FileSystemRights -band $dangerous) -ne 0 -and $trusted -notcontains $rule.IdentityReference.Value) { throw "Untrusted write permission: $Path" }
    }
}
$originalWhatIf = $WhatIfPreference; $WhatIfPreference = $false
Assert-NoReparse $source; Assert-NoReparse $programFiles
Assert-TrustedAcl $programFiles $false
foreach ($name in $expected.Keys) {
    $file = Join-Path $source $name; Assert-NoReparse $file
    if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected[$name]) { throw "Pinned dependency verification failed: $name" }
}
if ((Get-AuthenticodeSignature -LiteralPath (Join-Path $source 'wintun.dll')).Status -ne 'Valid') { throw 'Wintun signature verification failed.' }
if (Test-Path -LiteralPath $root) { Assert-NoReparse $root; Assert-TrustedAcl $root $false }
if (Test-Path -LiteralPath $destination) { throw 'Tunnel installation already exists; no files were replaced.' }
$WhatIfPreference = $originalWhatIf
if (!$PSCmdlet.ShouldProcess($destination, 'Install verified tunnel dependencies without enabling a driver or changing routes')) { return }
$admins = New-Object Security.Principal.SecurityIdentifier([Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,$null)
$system = New-Object Security.Principal.SecurityIdentifier([Security.Principal.WellKnownSidType]::LocalSystemSid,$null)
$users = New-Object Security.Principal.SecurityIdentifier([Security.Principal.WellKnownSidType]::BuiltinUsersSid,$null)
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true,$false); $acl.SetOwner($admins)
$inherit = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($admins,'FullControl',$inherit,'None','Allow')))
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($system,'FullControl',$inherit,'None','Allow')))
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($users,'ReadAndExecute',$inherit,'None','Allow')))
if (!(Test-Path -LiteralPath $root)) { $null = [IO.Directory]::CreateDirectory($root,$acl) }
Assert-NoReparse $root; Assert-TrustedAcl $root $false
$staging = Join-Path $root ('.tunnel-staging-' + [Guid]::NewGuid().ToString('N'))
$published = $false
try {
    $null = [IO.Directory]::CreateDirectory($staging,$acl)
    Assert-NoReparse $staging; Assert-TrustedAcl $staging $true
    foreach ($name in $expected.Keys) {
        $dest = Join-Path $staging $name
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination $dest
        Assert-NoReparse $dest; Assert-TrustedAcl $dest $false
        if ((Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash -ne $expected[$name]) { throw "Installed dependency verification failed: $name" }
    }
    if ((Get-AuthenticodeSignature -LiteralPath (Join-Path $staging 'wintun.dll')).Status -ne 'Valid') { throw 'Installed Wintun signature verification failed.' }
    [IO.Directory]::Move($staging,$destination); $published = $true
    Write-Host 'Verified tunnel dependencies installed. No driver, route, DNS, proxy, or adapter was activated.'
} finally {
    if (!$published -and (Test-Path -LiteralPath $staging)) {
        $resolved = (Resolve-Path -LiteralPath $staging).Path
        if ($resolved -ne $staging -or (Split-Path -Parent $resolved) -ne $root -or (Split-Path -Leaf $resolved) -notmatch '^\.tunnel-staging-[0-9a-f]{32}$') { throw 'Refusing to clean unexpected staging path.' }
        Assert-NoReparse $resolved
        foreach ($name in $expected.Keys) { $file = Join-Path $resolved $name; if (Test-Path -LiteralPath $file -PathType Leaf) { Assert-NoReparse $file; Remove-Item -LiteralPath $file } }
        if (!(Get-ChildItem -LiteralPath $resolved -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $resolved }
    }
}
