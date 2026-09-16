param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repo 'artifacts'
$output = Join-Path $artifacts 'publish/win-x64'
$staging = Join-Path $artifacts ('.publish-' + [Guid]::NewGuid().ToString('N'))
$stagedOutput = Join-Path $staging 'win-x64'
$previousOutput = Join-Path $staging 'previous-win-x64'
$stagedZip = Join-Path $staging 'release.zip'
$previousZip = Join-Path $staging 'previous.zip'
$safeToCleanStaging = $true
$buildLock = $null

function Assert-ArtifactPath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the artifacts directory: $fullPath"
    }
    for ($ancestor = $fullPath; $ancestor.Length -ge $artifacts.Length; $ancestor = Split-Path -Parent $ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Linked artifact paths are not supported: $ancestor"
        }
    }
}

function Assert-ArtifactTree([string]$Path) {
    Assert-ArtifactPath $Path
    if (Get-ChildItem -LiteralPath $Path -Recurse -Force -Attributes ReparsePoint) {
        throw "Linked artifact files are not supported: $Path"
    }
}

function Move-ArtifactItem([string]$Source, [string]$Destination) {
    Assert-ArtifactPath $Source
    Assert-ArtifactPath $Destination
    Move-Item -LiteralPath $Source -Destination $Destination
}

function Assert-OutputNotRunning {
    foreach ($process in @(Get-Process -Name Sidetalk, Sidetalk.IsolationProbe -ErrorAction SilentlyContinue)) {
        if (-not $process.Path -or $process.Path.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Close SIDETALK and its IsolationProbe before replacing $output."
        }
    }
}

Push-Location -LiteralPath $repo
try {
    Assert-ArtifactPath $output
    Assert-ArtifactPath $staging
    $lockPath = Join-Path $artifacts '.publish.lock'
    Assert-ArtifactPath $lockPath
    New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
    $buildLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    Assert-OutputNotRunning

    dotnet build src/Sidetalk.App/Sidetalk.App.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project tests/Sidetalk.Tests/Sidetalk.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Safety tests failed.' }
    dotnet build src/Sidetalk.IsolationProbe/Sidetalk.IsolationProbe.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Isolation probe build failed.' }
    $flavor = if ($SelfContained) { 'self-contained' } else { 'desktop-runtime' }
    $bundled = if ($SelfContained) { 'true' } else { 'false' }
    dotnet publish src/Sidetalk.App/Sidetalk.App.csproj -c Release -r win-x64 --self-contained $bundled -o $stagedOutput --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $probeOutput = Join-Path $stagedOutput 'tools/IsolationProbe'
    dotnet publish src/Sidetalk.IsolationProbe/Sidetalk.IsolationProbe.csproj -c Release -r win-x64 --self-contained $bundled -o $probeOutput --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Isolation probe publish failed.' }
    Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $stagedOutput
    Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $stagedOutput -Recurse -Force
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stagedOutput 'Sidetalk.dll')).ProductVersion.Split('+')[0]
    if ($version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') { throw "Invalid app version: $version" }
    $zip = Join-Path $artifacts "SIDETALK-$version-win-x64-$flavor.zip"
    Assert-ArtifactPath $zip
    Assert-ArtifactTree $stagedOutput
    Compress-Archive -Path (Join-Path $stagedOutput '*') -DestinationPath $stagedZip
    $zipHash = Get-FileHash -LiteralPath $stagedZip -Algorithm SHA256

    # Keep the old application and archive until every build/package step succeeds.
    New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
    Assert-OutputNotRunning
    $safeToCleanStaging = $false
    $installedOutput = $false
    try {
        if (Test-Path -LiteralPath $output) { Move-ArtifactItem $output $previousOutput }
        if (Test-Path -LiteralPath $zip) { Move-ArtifactItem $zip $previousZip }
        Move-ArtifactItem $stagedOutput $output
        $installedOutput = $true
        Move-ArtifactItem $stagedZip $zip
        $safeToCleanStaging = $true
    } catch {
        $promotionError = $_
        try {
            if ($installedOutput) { Move-ArtifactItem $output $stagedOutput }
            if (Test-Path -LiteralPath $previousOutput) { Move-ArtifactItem $previousOutput $output }
            if (Test-Path -LiteralPath $previousZip) { Move-ArtifactItem $previousZip $zip }
            $safeToCleanStaging = $true
        } catch {
            throw "Publish replacement and rollback failed. Preserved files: $staging. Replacement: $promotionError. Rollback: $_"
        }
        throw $promotionError
    }

    [pscustomobject]@{ Algorithm = 'SHA256'; Hash = $zipHash.Hash; Path = $zip }
    Write-Output ('App: ' + (Join-Path $output 'Sidetalk.exe'))
} finally {
    try {
        if ($safeToCleanStaging -and (Test-Path -LiteralPath $staging)) {
            Assert-ArtifactTree $staging
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    } catch {
        Write-Warning "Could not clean staging; files preserved at $staging. $_"
    } finally {
        if ($null -ne $buildLock) { $buildLock.Dispose() }
        Pop-Location
    }
}
