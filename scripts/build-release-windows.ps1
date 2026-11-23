param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'windows\TetherBridge.Desktop\TetherBridge.Desktop.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Unsupported package version.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$output = (Resolve-Path -LiteralPath $output).Path
$checksums = Join-Path $output 'SHA256SUMS.txt'
if (Test-Path -LiteralPath $checksums) { throw 'Choose an empty release output directory.' }
$stageParent = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path
$stage = Join-Path $stageParent ('tetherbridge-release-build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$variants = [ordered]@{ 'framework-dependent' = 'false'; 'self-contained' = 'true' }
try {
    $archives = foreach ($variant in $variants.Keys) {
        $base = Join-Path $stage $variant
        $publish = Join-Path $base 'publish'
        $installer = Join-Path $base 'installer'
        $verify = Join-Path $base 'extracted'
        $installRoot = Join-Path $base 'installed'
        $programs = Join-Path $base 'programs'
        $name = "TetherBridge-$version-win-x64-$variant.zip"
        $archive = Join-Path $base $name
        $destination = Join-Path $output $name
        if (Test-Path -LiteralPath $destination) { throw "Release asset already exists: $destination" }

        Push-Location $repo
        try {
            & $Dotnet publish $project -c Release -r win-x64 --self-contained $variants[$variant] -o $publish | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Publishing $variant failed." }
        } finally { Pop-Location }

        & (Join-Path $PSScriptRoot 'package-windows.ps1') -PublishPath $publish -PackagePath $installer
        $runtime = Get-Content -LiteralPath (Join-Path $installer 'app\TetherBridge.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
        $frameworkNames = if ($variant -eq 'framework-dependent') { @($runtime.runtimeOptions.frameworks.name) } else { @($runtime.runtimeOptions.includedFrameworks.name) }
        if ($frameworkNames -notcontains 'Microsoft.WindowsDesktop.App' -or $runtime.runtimeOptions.tfm -ne 'net10.0') {
            throw "Package $variant does not use the expected .NET 10 Desktop runtime."
        }
        if ($variant -eq 'framework-dependent') {
            'Requires the Microsoft .NET 10 Desktop Runtime for Windows x64.' | Set-Content -LiteralPath (Join-Path $installer 'REQUIREMENTS.txt')
        }
        [IO.Compression.ZipFile]::CreateFromDirectory($installer, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $verify)

        $installScript = Join-Path $verify 'install.ps1'
        & $installScript -InstallRoot $installRoot -ProgramsDirectory $programs
        if (!(Test-Path -LiteralPath (Join-Path $installRoot "$version\TetherBridge.Desktop.exe"))) { throw "$variant install check failed." }
        & $installScript -Uninstall -InstallRoot $installRoot -ProgramsDirectory $programs
        if (Test-Path -LiteralPath (Join-Path $installRoot $version)) { throw "$variant uninstall check failed." }
        $archive
    }

    foreach ($archive in $archives) {
        $destination = Join-Path $output (Split-Path -Leaf $archive)
        # Cross-volume Copy with overwrite disabled also works on redirected workspaces.
        [IO.File]::Copy($archive, $destination, $false)
    }
    $archives | ForEach-Object {
        $name = Split-Path -Leaf $_
        $hash = (Get-FileHash -LiteralPath (Join-Path $output $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $name"
    } | Set-Content -LiteralPath $checksums -Encoding ascii
} finally {
    if (Test-Path -LiteralPath $stage) {
        $item = Get-Item -LiteralPath $stage -Force
        if ($item.FullName -eq $stage -and $item.Parent.FullName -eq $stageParent -and
            $item.Name -match '^tetherbridge-release-build-[0-9a-f]{32}$' -and
            !($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Remove-Item -LiteralPath $item.FullName -Recurse -Force
        }
    }
}
