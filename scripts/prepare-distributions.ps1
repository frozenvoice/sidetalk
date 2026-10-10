#Requires -Version 7.0
<#
.SYNOPSIS
    Prepare Windows Setup.exe and an installer-free portable ZIP for one SIDETALK version.

.DESCRIPTION
    Reuses the tested self-contained publish and pinned Inno Setup package path.
    Nothing is installed, launched or uploaded to GitHub. KakaoTalk and Sandboxie
    are external runtime prerequisites, never redistributed by either artifact.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $root 'artifacts'
$release = Join-Path $artifacts 'release'
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$version = [string]($props.Project.PropertyGroup | ForEach-Object { $_.SidetalkVersion } | Where-Object { $_ } | Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid SIDETALK release version: '$version'" }

$portableName = "SIDETALK-$version-win-x64-portable.zip"
$portable = Join-Path $release $portableName
$portableHashFile = $portable + '.sha256'
foreach ($path in @($portable, $portableHashFile)) {
    if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite a previous portable distribution: $path" }
}
New-Item -ItemType Directory -Force -Path $release | Out-Null
$staging = Join-Path $release ('.portable-stage-' + [guid]::NewGuid().ToString('N') + '.zip')

try {
    # Keep all existing full build/verification and install-package checks intact.
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build.ps1') -SelfContained
    if ($LASTEXITCODE -ne 0) { throw "Self-contained build failed with exit code $LASTEXITCODE" }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build-installer.ps1')
    if ($LASTEXITCODE -ne 0) { throw "Installer packaging failed with exit code $LASTEXITCODE" }

    $sourceZip = Join-Path $artifacts "SIDETALK-$version-win-x64-self-contained.zip"
    $sourceExe = Join-Path $artifacts 'publish\win-x64\Sidetalk.exe'
    $setup = Join-Path $release 'SIDETALK-Setup.exe'
    foreach ($file in @($sourceZip, $sourceExe, $setup, ($setup + '.sha256'))) {
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing required distribution input: $file" }
    }
    # The ZIP is the same complete self-contained publish used by Setup.
    $zip = [IO.Compression.ZipFile]::OpenRead($sourceZip)
    try {
        $files = @($zip.Entries | Where-Object { $_.FullName -ceq 'Sidetalk.exe' })
        if ($files.Count -ne 1) { throw 'Self-contained ZIP must contain one root Sidetalk.exe' }
        $exeStream = $files[0].Open()
        try { $zippedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($exeStream)) }
        finally { $exeStream.Dispose() }
    } finally { $zip.Dispose() }
    if ($zippedHash -ine (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash) {
        throw 'Portable ZIP executable does not match the installer publish source.'
    }

    Copy-Item -LiteralPath $sourceZip -Destination $staging -ErrorAction Stop
    $portableHash = (Get-FileHash -LiteralPath $staging -Algorithm SHA256).Hash.ToLowerInvariant()
    Move-Item -LiteralPath $staging -Destination $portable -ErrorAction Stop
    [IO.File]::WriteAllText($portableHashFile, "$portableHash  $portableName" + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    $setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashLines = @("$portableHash  $portableName", "$setupHash  SIDETALK-Setup.exe") | Sort-Object
    [IO.File]::WriteAllText((Join-Path $release 'SHA256SUMS.txt'),
        (($hashLines -join [Environment]::NewLine) + [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    Write-Host "Installer: $setup"
    Write-Host "Portable: $portable"
    Write-Host "Checksums: $(Join-Path $release 'SHA256SUMS.txt')"
    Write-Host 'Upload the two distributions as assets on the SAME GitHub Release after Windows verification.'
} finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Force }
}
