param(
    [string]$PublishPath,
    [string]$PackagePath
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publish = if ($PublishPath) { [IO.Path]::GetFullPath($PublishPath) } else { Join-Path $root 'dist\windows-selfcontained' }
$exe = Get-Item -LiteralPath (Join-Path $publish 'TetherBridge.Desktop.exe')
$productVersion = $exe.VersionInfo.ProductVersion
if ($productVersion -notmatch '^(?<release>[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5})(?:\+[0-9A-Za-z.-]+)?$') {
    throw 'The Windows executable has no supported release version.'
}
$version = $Matches['release']
$package = if ($PackagePath) { [IO.Path]::GetFullPath($PackagePath) } else { Join-Path $root "dist\TetherBridge-$version-installer" }
$publish = $publish.TrimEnd('\')
$package = $package.TrimEnd('\')
if (!(Test-Path -LiteralPath (Join-Path $publish 'TetherBridge.Desktop.exe') -PathType Leaf)) {
    throw 'Publish the Windows build first.'
}
if ($package.Equals($publish, [StringComparison]::OrdinalIgnoreCase) -or
    $package.StartsWith($publish + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The installer package cannot be created inside its own input.'
}
if (Test-Path -LiteralPath $package) { throw "Package directory already exists: $package" }

function Assert-PlainPath([string]$Path) {
    for ($directory = [IO.DirectoryInfo]::new($Path); $null -ne $directory; $directory = $directory.Parent) {
        if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Package paths cannot traverse reparse points: $($directory.FullName)"
        }
    }
}
Assert-PlainPath $publish
$parent = Split-Path -Parent $package
Assert-PlainPath $parent
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$parent = (Resolve-Path -LiteralPath $parent).Path
$package = Join-Path $parent (Split-Path -Leaf $package)
$stage = Join-Path $parent ('.tetherbridge-package-' + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $stage 'app'
try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    New-Item -ItemType Directory -Path $app | Out-Null
    $source = Get-Item -LiteralPath $publish -Force
    $entries = @(Get-ChildItem -LiteralPath $publish -Recurse -Force)
    if ((@($source) + $entries) | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
        throw 'Release payload cannot contain reparse points.'
    }
    Get-ChildItem -LiteralPath $publish -Force | Copy-Item -Destination $app -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $root 'installer\install.ps1') -Destination (Join-Path $stage 'install.ps1')
    $files = Get-ChildItem -LiteralPath $app -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        @{ path = $_.FullName.Substring($app.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }
    @{ version=$version; files=@($files) } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'payload-sha256.json') -Encoding utf8
    [IO.Directory]::Move($stage, $package)
}
finally {
    if (Test-Path -LiteralPath $stage) {
        $item = Get-Item -LiteralPath $stage -Force
        if ($item.FullName -eq $stage -and $item.Parent.FullName -eq $parent -and
            $item.Name -match '^\.tetherbridge-package-[0-9a-f]{32}$' -and
            !($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Remove-Item -LiteralPath $stage -Recurse -Force
        }
    }
}
Write-Host "Installer package prepared at $package."
