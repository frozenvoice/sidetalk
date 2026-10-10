param(
    [switch]$SelfContained,
    # prepare-distributions holds the existing publish lock across both packagers.
    [IO.FileStream]$BuildLock
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-common.ps1')
$timer = [Diagnostics.Stopwatch]::StartNew()
$repo = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repo 'artifacts'
$output = Join-Path $artifacts 'publish/win-x64'
$staging = Join-Path $artifacts ('.publish-' + [Guid]::NewGuid().ToString('N'))
$stagedOutput = Join-Path $staging 'win-x64'
$previousOutput = Join-Path $staging 'previous-win-x64'
$stagedZip = Join-Path $staging 'release.zip'
$previousZip = Join-Path $staging 'previous.zip'
$safeToCleanStaging = $true
$heldBuildLock = $null
$ownsBuildLock = $false

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
    Start-SidetalkStage 'package/preflight' 20
    Assert-ArtifactPath $output
    Assert-ArtifactPath $staging
    $lockPath = Join-Path $artifacts '.publish.lock'
    Assert-ArtifactPath $lockPath
    New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
    if ($null -ne $BuildLock) {
        if (-not $BuildLock.CanRead -or -not $BuildLock.CanWrite -or -not $BuildLock.CanSeek -or
            -not [string]::Equals([IO.Path]::GetFullPath($BuildLock.Name), $lockPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'BuildLock must be the live artifacts/.publish.lock stream held by the distribution caller.'
        }
        $heldBuildLock = $BuildLock
    } else {
        $heldBuildLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $ownsBuildLock = $true
    }
    Assert-OutputNotRunning

    Complete-SidetalkStage
    Invoke-SidetalkVerification
    $flavor = if ($SelfContained) { 'self-contained' } else { 'desktop-runtime' }
    $bundled = if ($SelfContained) { 'true' } else { 'false' }
    # Publish restores/builds for its own RID and runtime mode; generic builds cannot replace it.
    Start-SidetalkStage 'publish/app' 21
    Invoke-SidetalkDotnet @('publish', 'src/Sidetalk.App/Sidetalk.App.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', $bundled, '-o', $stagedOutput, '--nologo')
    Complete-SidetalkStage
    $probeOutput = Join-Path $stagedOutput 'tools/IsolationProbe'
    Start-SidetalkStage 'publish/isolation-probe' 22
    Invoke-SidetalkDotnet @('publish', 'src/Sidetalk.IsolationProbe/Sidetalk.IsolationProbe.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', $bundled, '-o', $probeOutput, '--nologo')
    Complete-SidetalkStage
    Start-SidetalkStage 'package/documents' 23
    Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $stagedOutput
    Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $stagedOutput -Recurse -Force
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stagedOutput 'Sidetalk.dll')).ProductVersion.Split('+')[0]
    if ($version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') { throw "Invalid app version: $version" }
    Complete-SidetalkStage
    Start-SidetalkStage 'package/archive' 30
    $zip = Join-Path $artifacts "SIDETALK-$version-win-x64-$flavor.zip"
    Assert-ArtifactPath $zip
    Assert-ArtifactTree $stagedOutput
    Compress-Archive -Path (Join-Path $stagedOutput '*') -DestinationPath $stagedZip
    $zipHash = Get-FileHash -LiteralPath $stagedZip -Algorithm SHA256

    Complete-SidetalkStage
    Start-SidetalkStage 'package/replace' 40
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
            $failure = [InvalidOperationException]::new("Publish replacement and rollback failed. Preserved files: $staging; current output: $output; archive: $zip. Replacement: $promotionError. Rollback: $_", $promotionError.Exception)
            $failure.Data['SidetalkExitCode'] = 41
            throw $failure
        }
        throw $promotionError
    }

    Complete-SidetalkStage
    [pscustomobject]@{ Algorithm = 'SHA256'; Hash = $zipHash.Hash; Path = $zip }
    Write-Output ('App: ' + (Join-Path $output 'Sidetalk.exe'))
    Write-Host ("Packaging complete ({0:F2}s)." -f $timer.Elapsed.TotalSeconds)
} catch {
    exit (Write-SidetalkFailure $_)
} finally {
    try {
        if ($safeToCleanStaging -and (Test-Path -LiteralPath $staging)) {
            Assert-ArtifactTree $staging
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    } catch {
        Write-Warning "Could not clean staging; files preserved at $staging. $_"
    } finally {
        if ($ownsBuildLock -and $null -ne $heldBuildLock) { $heldBuildLock.Dispose() }
        Pop-Location
    }
}
