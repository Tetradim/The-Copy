param(
    [switch]$Uninstall,
    [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TetherBridge\App'),
    [string]$ProgramsDirectory = ([Environment]::GetFolderPath('Programs'))
)
$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $PSScriptRoot 'payload-sha256.json'
if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Installer manifest is missing.' }
$version = [string](Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).version
if ($version -notmatch '^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$') { throw 'Invalid installer version.' }
$installRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$target = Join-Path $installRoot $version
$shortcutPath = Join-Path ([IO.Path]::GetFullPath($ProgramsDirectory)) 'TetherBridge.lnk'
$payload = Join-Path $PSScriptRoot 'app'
$installedManifest = Join-Path $target '.install-manifest.json'
function Get-SafeFile([string]$Base, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative -match '(^|[\\/])\.\.([\\/]|$)') { throw 'Unsafe payload path.' }
    foreach ($part in ($Relative -split '[\\/]')) {
        if (!$part -or $part -match '[<>:"|?*\x00-\x1f]|[ .]$' -or
            $part -match '^(CON|PRN|AUX|NUL|COM[0-9\u00B9\u00B2\u00B3]|LPT[0-9\u00B9\u00B2\u00B3]|CONIN\$|CONOUT\$)(\.|$)') {
            throw 'Unsafe payload path.'
        }
    }
    $root = [IO.Path]::GetFullPath($Base).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $full = [IO.Path]::GetFullPath((Join-Path $Base $Relative))
    if (!$full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload path escapes its directory.' }
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Installer paths cannot traverse reparse points.' }
        }
        $parent = Split-Path -Parent $cursor
        if (!$parent -or $parent -eq $cursor) { break }
        $cursor = $parent
    }
    return $full
}
function Read-Manifest([string]$Path, [string]$ExpectedVersion = $version, [string]$Base = $target) {
    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($manifest.version -ne $ExpectedVersion -or !$manifest.files -or @($manifest.files).Count -gt 2000) { throw 'Invalid installer manifest.' }
    $names = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        $name = [string]$entry.path
        $canonical = Get-SafeFile $Base $name
        if (!$names.Add($canonical) -or [string]$entry.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid or duplicate installer manifest entry.' }
    }
    if (!$names.Contains((Join-Path $Base 'TetherBridge.Desktop.exe'))) { throw 'Installer manifest is missing the desktop executable.' }
    return $manifest
}
function Test-Running {
    foreach ($process in Get-Process -Name 'TetherBridge.Desktop' -ErrorAction SilentlyContinue) {
        try { if (!$process.Path) { throw 'Process path is unavailable.' }; if ($process.Path.StartsWith($installRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { return $true } }
        catch { throw 'Cannot determine whether an installed TetherBridge process is running. Close it before continuing.' }
    }
    return $false
}
function Get-ShortcutTarget {
    $null = Get-SafeFile $ProgramsDirectory 'TetherBridge.lnk'
    if (!(Test-Path -LiteralPath $shortcutPath)) { return $null }
    $shell = New-Object -ComObject WScript.Shell
    return $shell.CreateShortcut($shortcutPath).TargetPath
}
function Test-ManagedShortcut([string]$Executable) {
    $prior = Split-Path -Parent $Executable
    $priorVersion = Split-Path -Leaf $prior
    if ($priorVersion -notmatch '^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$' -or
        [version]$priorVersion -ge [version]$version) { return $false }
    $expectedDirectory = Join-Path $installRoot $priorVersion
    $expectedExecutable = Get-SafeFile $expectedDirectory 'TetherBridge.Desktop.exe'
    if (!$Executable.Equals($expectedExecutable, [StringComparison]::OrdinalIgnoreCase) -or
        !(Test-Path -LiteralPath $expectedExecutable -PathType Leaf)) { return $false }

    $previousManifestPath = Get-SafeFile $expectedDirectory '.install-manifest.json'
    if (!(Test-Path -LiteralPath $previousManifestPath -PathType Leaf)) { return $false }
    $previous = Read-Manifest $previousManifestPath $priorVersion $expectedDirectory
    $exe = $previous.files | Where-Object { (Get-SafeFile $expectedDirectory ([string]$_.path)) -eq $expectedExecutable }
    return (Get-FileHash -LiteralPath $expectedExecutable -Algorithm SHA256).Hash -eq $exe.sha256
}
if (Test-Running) { throw 'Exit TetherBridge from its tray menu before installing or uninstalling.' }
if ($Uninstall) {
    if (!(Test-Path -LiteralPath $installedManifest)) { throw 'Installed manifest is missing; no files were removed.' }
    $manifest = Read-Manifest $installedManifest
    $shortcutTarget = Get-ShortcutTarget
    if ($shortcutTarget -and $shortcutTarget.Equals((Join-Path $target 'TetherBridge.Desktop.exe'), [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $shortcutPath }
    $modified = $false
    foreach ($entry in $manifest.files) {
        $file = Get-SafeFile $target ([string]$entry.path)
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            $item = Get-Item -LiteralPath $file -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { $modified = $true; continue }
            Remove-Item -LiteralPath $file
        }
    }
    if (!$modified) { Remove-Item -LiteralPath $installedManifest }
    $directories = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        $directory = Split-Path -Parent (Get-SafeFile $target ([string]$entry.path))
        while ($directory -and !$directory.Equals($target, [StringComparison]::OrdinalIgnoreCase)) {
            $null = $directories.Add($directory)
            $directory = Split-Path -Parent $directory
        }
    }
    foreach ($directory in @($directories | Sort-Object { $_.Length } -Descending)) {
        if ((Test-Path -LiteralPath $directory -PathType Container) -and !(Get-ChildItem -LiteralPath $directory -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $directory }
    }
    if (!(Get-ChildItem -LiteralPath $target -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $target }
    if ($modified) { Write-Host 'Modified files and the uninstall manifest were retained. User settings were preserved.' }
    else { Write-Host 'This TetherBridge version was removed. User settings were preserved.' }
    exit 0
}
if (!(Test-Path -LiteralPath $manifestPath) -or !(Test-Path -LiteralPath $payload)) { throw 'Installer payload is missing.' }
$manifest = Read-Manifest $manifestPath
$shortcutTarget = Get-ShortcutTarget
if ($shortcutTarget -and !$shortcutTarget.Equals((Join-Path $target 'TetherBridge.Desktop.exe'), [StringComparison]::OrdinalIgnoreCase) -and
    !(Test-ManagedShortcut $shortcutTarget)) { throw 'A shortcut for an unrelated or newer build exists; it was left in place.' }
foreach ($entry in $manifest.files) {
    $source = Get-SafeFile $payload ([string]$entry.path)
    if (!(Test-Path -LiteralPath $source -PathType Leaf) -or ((Get-Item -LiteralPath $source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Payload verification failed: $($entry.path)" }
}
New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
if (!(Test-Path -LiteralPath $target)) {
    $staging = Join-Path $installRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging | Out-Null
    foreach ($entry in $manifest.files) {
        $destination = Get-SafeFile $staging ([string]$entry.path)
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath (Get-SafeFile $payload ([string]$entry.path)) -Destination $destination
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Installed file verification failed: $($entry.path)" }
    }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $staging '.install-manifest.json')
    [IO.Directory]::Move($staging, $target)
} else {
    if (!(Test-Path -LiteralPath $installedManifest)) { throw 'Existing version has no installer record; it was left unchanged.' }
    $installed = Read-Manifest $installedManifest
    $expectedSet = @($manifest.files | ForEach-Object { (Get-SafeFile $target ([string]$_.path)).ToLowerInvariant() + '|' + ([string]$_.sha256).ToUpperInvariant() } | Sort-Object)
    $installedSet = @($installed.files | ForEach-Object { (Get-SafeFile $target ([string]$_.path)).ToLowerInvariant() + '|' + ([string]$_.sha256).ToUpperInvariant() } | Sort-Object)
    if (($expectedSet -join "`n") -ne ($installedSet -join "`n")) { throw 'Existing installer record differs from this package; it was left unchanged.' }
    foreach ($entry in $manifest.files) {
        $existing = Get-SafeFile $target ([string]$entry.path)
        if (!(Test-Path -LiteralPath $existing -PathType Leaf) -or (Get-FileHash -LiteralPath $existing -Algorithm SHA256).Hash -ne $entry.sha256) { throw 'Existing version differs from this package; it was left unchanged.' }
    }
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $shortcutPath) | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $target 'TetherBridge.Desktop.exe'
$shortcut.WorkingDirectory = $target
$shortcut.Description = 'TetherBridge desktop companion'
$shortcut.Save()
Write-Host "Installed TetherBridge $version for the current Windows user. No drivers or network settings were changed."
