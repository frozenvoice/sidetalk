#Requires -Version 7.0
<#
.SYNOPSIS
    Prepare Setup.exe and a complete installer-free portable ZIP from one publish.
.DESCRIPTION
    Runs the existing full self-contained build once, then the pinned installer
    compiler. Nothing is installed, launched or uploaded. Only a fully validated
    pair replaces artifacts/release. A failed run never reports old assets as new.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $root 'artifacts'
$release = Join-Path $artifacts 'release'
$staging = Join-Path $artifacts '.distributions-stage'
$stagedRelease = Join-Path $staging 'release'
$previousRelease = Join-Path $staging 'previous-release'
$buildLock = $null
$safeToCleanStaging = $false
$stage = 'preflight'

function Assert-DistributionPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Distribution path escaped artifacts: $full"
    }
    for ($ancestor = $full; $ancestor; $ancestor = Split-Path -Parent $ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Linked distribution paths are not supported: $ancestor"
        }
    }
}

function Assert-DistributionTree([string]$Path) {
    Assert-DistributionPath $Path
    if ((Test-Path -LiteralPath $Path -PathType Container) -and (Get-ChildItem -LiteralPath $Path -Recurse -Force -Attributes ReparsePoint)) {
        throw "Linked distribution files are not supported: $Path"
    }
}

function Move-DistributionItem([string]$Source, [string]$Destination) {
    Assert-DistributionPath $Source
    Assert-DistributionPath $Destination
    Move-Item -LiteralPath $Source -Destination $Destination
}

function Get-DistributionVersion([string]$Path) {
    return ([string][Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion).Split('+')[0].Trim()
}

function Get-DistributionPayload([string]$Source, [string]$Version) {
    Assert-DistributionTree $Source
    $required = @('Sidetalk.exe', 'Sidetalk.dll', 'Sidetalk.Core.dll', 'Sidetalk.deps.json', 'Sidetalk.runtimeconfig.json',
        'README.md', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll', 'wpfgfx_cor3.dll',
        'tools/IsolationProbe/Sidetalk.IsolationProbe.exe', 'tools/IsolationProbe/Sidetalk.IsolationProbe.dll',
        'tools/IsolationProbe/Sidetalk.IsolationProbe.deps.json', 'tools/IsolationProbe/Sidetalk.IsolationProbe.runtimeconfig.json',
        'tools/IsolationProbe/coreclr.dll', 'tools/IsolationProbe/hostfxr.dll', 'tools/IsolationProbe/hostpolicy.dll')
    foreach ($path in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $Source $path) -PathType Leaf)) { throw "Publish is missing required payload file: $path" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Source 'docs') -PathType Container)) { throw 'Publish is missing docs.' }
    foreach ($config in @('Sidetalk.runtimeconfig.json', 'tools/IsolationProbe/Sidetalk.IsolationProbe.runtimeconfig.json')) {
        $options = (Get-Content -LiteralPath (Join-Path $Source $config) -Raw | ConvertFrom-Json).runtimeOptions
        $names = @($options.PSObject.Properties.Name)
        if ($names -contains 'framework' -or $names -contains 'frameworks' -or $names -notcontains 'includedFrameworks') {
            throw "Publish is not self-contained: $config"
        }
        $frameworks = @($options.includedFrameworks | ForEach-Object { $_.name })
        if ($frameworks -notcontains 'Microsoft.NETCore.App' -or
            ($config -eq 'Sidetalk.runtimeconfig.json' -and $frameworks -notcontains 'Microsoft.WindowsDesktop.App')) {
            throw "Publish has incomplete bundled frameworks: $config"
        }
    }
    $versioned = @('Sidetalk.exe', 'Sidetalk.dll', 'Sidetalk.Core.dll',
        'tools/IsolationProbe/Sidetalk.IsolationProbe.exe', 'tools/IsolationProbe/Sidetalk.IsolationProbe.dll')
    $forbidden = @('KakaoTalk.exe', 'Start.exe', 'SandMan.exe', 'SbieCtrl.exe', 'SbieSvc.exe', 'SbieDll.dll', 'SbieDrv.sys', 'SbieIni.exe')
    foreach ($file in @(Get-ChildItem -LiteralPath $Source -Recurse -File -Force | Sort-Object FullName)) {
        if ($forbidden -contains $file.Name) { throw "Publish contains a third-party executable that must not be redistributed: $($file.Name)" }
        $path = [IO.Path]::GetRelativePath($Source, $file.FullName).Replace('\', '/')
        $entry = [ordered]@{ path = $path; length = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        if ($versioned -contains $path) {
            $entry.productVersion = Get-DistributionVersion $file.FullName
            if ($entry.productVersion -ne $Version) { throw "Publish version mismatch in ${path}: expected $Version, got $($entry.productVersion)" }
        }
        [pscustomobject]$entry
    }
}

function Assert-CompleteDistributionArchive([string]$Archive, [object[]]$Payload) {
    Assert-DistributionPath $Archive
    $expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $Payload) { $expected.Add($file.path, $file) }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            $path = $entry.FullName.Replace('\', '/')
            if ($path.StartsWith('/') -or $path.Contains(':') -or @($path.Split('/') | Where-Object { $_ -in @('.', '..') }).Count) {
                throw "Archive has an unsafe path: $path"
            }
            if ($path.EndsWith('/')) { continue }
            if (-not $seen.Add($path)) { throw "Archive has duplicate file paths: $path" }
            if (-not $expected.ContainsKey($path)) { throw "Archive has an unexpected file: $path" }
            $file = $expected[$path]
            if ($entry.Length -ne $file.length) { throw "Archive length mismatch: $path" }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
            finally { $stream.Dispose() }
            if ($hash -cne $file.sha256) { throw "Archive content mismatch: $path" }
        }
        if ($seen.Count -ne $expected.Count) {
            $missing = @($expected.Keys | Where-Object { -not $seen.Contains($_) }) -join ', '
            throw "Archive is missing publish files: $missing"
        }
    } finally { $zip.Dispose() }
}

function Get-DistributionSource {
    $commit = [string](& git -C $root rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-fA-F]{40}$') { throw 'Could not identify the source commit.' }
    $changes = @(& git -C $root status --porcelain=v1 --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect source cleanliness.' }
    return [pscustomobject]@{ commit = $commit.ToLowerInvariant(); dirty = [bool]$changes.Count }
}

try {
    [xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
    $versionNode = $props.SelectSingleNode('/Project/PropertyGroup/SidetalkVersion')
    $version = if ($null -ne $versionNode) { $versionNode.InnerText.Trim() } else { '' }
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid SIDETALK release version: '$version'" }
    foreach ($path in @($release, $staging, (Join-Path $artifacts '.publish.lock'))) { Assert-DistributionTree $path }
    New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
    # A live shared handle spans verification, publish, compile, comparison and
    # promotion. Child scripts validate and borrow it without disposing it.
    $buildLock = [IO.File]::Open((Join-Path $artifacts '.publish.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    if (Test-Path -LiteralPath $staging) { throw "Previous distribution staging requires inspection before retrying: $staging" }
    $sourceState = Get-DistributionSource
    New-Item -ItemType Directory -Path $stagedRelease -Force | Out-Null
    $safeToCleanStaging = $true
    $portableName = "SIDETALK-$version-win-x64-portable.zip"
    # release can also contain logs and artifacts from other work. Replace only
    # this pair and its evidence; carry all unrelated entries through unchanged.
    $ownedNames = @('SIDETALK-Setup.exe', 'SIDETALK-Setup.exe.sha256', 'SIDETALK-Setup.exe.unsigned.txt',
        $portableName, ($portableName + '.sha256'), 'SHA256SUMS.txt', 'SIDETALK-distributions.json')
    if (Test-Path -LiteralPath $release) {
        foreach ($item in @(Get-ChildItem -LiteralPath $release -Force)) {
            if ($ownedNames -notcontains $item.Name) {
                Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $stagedRelease $item.Name) -Recurse -Force
            }
        }
    }

    $stage = 'self-contained build'
    $global:LASTEXITCODE = 0
    & (Join-Path $PSScriptRoot 'build.ps1') -SelfContained -BuildLock $buildLock
    if ($LASTEXITCODE -ne 0) { throw "Self-contained build failed with exit code $LASTEXITCODE" }
    $source = Join-Path $artifacts 'publish/win-x64'
    $sourceZip = Join-Path $artifacts "SIDETALK-$version-win-x64-self-contained.zip"
    $stage = 'complete publish and archive comparison'
    $payload = @(Get-DistributionPayload $source $version)
    Assert-CompleteDistributionArchive $sourceZip $payload
    $portable = Join-Path $stagedRelease $portableName
    Copy-Item -LiteralPath $sourceZip -Destination $portable
    Assert-CompleteDistributionArchive $portable $payload

    $stage = 'installer packaging'
    $global:LASTEXITCODE = 0
    & (Join-Path $PSScriptRoot 'build-installer.ps1') -SourceDir $source -ReleaseDir $stagedRelease -BuildLock $buildLock
    if ($LASTEXITCODE -ne 0) { throw "Installer packaging failed with exit code $LASTEXITCODE" }
    $setup = Join-Path $stagedRelease 'SIDETALK-Setup.exe'
    if ((Get-DistributionVersion $setup) -ne $version) { throw 'Installer and portable product versions differ.' }
    $stage = 'final integrity and provenance'
    $afterPayload = @(Get-DistributionPayload $source $version)
    if (($payload | ConvertTo-Json -Depth 5 -Compress) -cne ($afterPayload | ConvertTo-Json -Depth 5 -Compress)) { throw 'Publish changed while preparing distributions.' }
    $afterSourceState = Get-DistributionSource
    if ($sourceState.commit -ne $afterSourceState.commit -or $sourceState.dirty -ne $afterSourceState.dirty) { throw 'Source commit or cleanliness changed while preparing distributions.' }
    $assetManifest = @(foreach ($name in @('SIDETALK-Setup.exe', $portableName)) {
        $path = Join-Path $stagedRelease $name
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $hashPath = $path + '.sha256'
        if ($name -eq 'SIDETALK-Setup.exe' -and
            ((Get-Content -LiteralPath $hashPath -Raw).Trim() -cne "$hash  $name")) { throw 'Installer checksum file does not match the compiled installer.' }
        [IO.File]::WriteAllText($hashPath, "$hash  $name" + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        [pscustomobject]@{ name = $name; length = (Get-Item -LiteralPath $path).Length; sha256 = $hash }
    })
    $hashLines = @($assetManifest | ForEach-Object { "$($_.sha256)  $($_.name)" } | Sort-Object)
    [IO.File]::WriteAllText((Join-Path $stagedRelease 'SHA256SUMS.txt'), ($hashLines -join [Environment]::NewLine) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    $manifest = [ordered]@{
        schemaVersion = 1; version = $version; runtimeIdentifier = 'win-x64'; selfContained = $true
        sourceCommit = $sourceState.commit; sourceDirty = $sourceState.dirty; createdUtc = [DateTime]::UtcNow.ToString('o')
        appExecutableSha256 = ($payload | Where-Object path -ceq 'Sidetalk.exe').sha256
        payload = $payload; artifacts = $assetManifest
    }
    [IO.File]::WriteAllText((Join-Path $stagedRelease 'SIDETALK-distributions.json'), ($manifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

    $stage = 'transactional release promotion'
    $safeToCleanStaging = $false
    $promoted = $false
    try {
        if (Test-Path -LiteralPath $release) { Move-DistributionItem $release $previousRelease }
        Move-DistributionItem $stagedRelease $release
        $promoted = $true
        $safeToCleanStaging = $true
    } catch {
        $promotionError = $_
        try {
            if ($promoted) { Move-DistributionItem $release $stagedRelease }
            if (Test-Path -LiteralPath $previousRelease) { Move-DistributionItem $previousRelease $release }
            $safeToCleanStaging = $true
        } catch {
            throw "Distribution replacement and rollback failed. Preserved files: $staging. Replacement: $promotionError. Rollback: $_"
        }
        throw $promotionError
    }
    Write-Host "Installer: $(Join-Path $release 'SIDETALK-Setup.exe')"
    Write-Host "Portable: $(Join-Path $release $portableName)"
    Write-Host "Manifest: $(Join-Path $release 'SIDETALK-distributions.json')"
    Write-Host "Source: $($sourceState.commit); dirty: $($sourceState.dirty)"
    Write-Host 'Distributions prepared. Publishing requires the exact clean commit Windows verification gate.'
} catch {
    [Console]::Error.WriteLine("Distribution preparation failed during ${stage}: $($_.Exception.Message). No new release pair was reported ready.")
    exit 1
} finally {
    try {
        if ($safeToCleanStaging -and (Test-Path -LiteralPath $staging)) {
            Assert-DistributionTree $staging
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    } catch { Write-Warning "Could not clean distribution staging; preserved at $staging. $_" }
    finally { if ($null -ne $buildLock) { $buildLock.Dispose() } }
}
