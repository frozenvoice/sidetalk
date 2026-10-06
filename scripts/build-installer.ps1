[CmdletBinding()]
param(
    # The self-contained publish produced by scripts\build.ps1 -SelfContained.
    [string]$SourceDir,
    [string]$ISCCPath,
    # Optional real code signing. Unsigned builds never claim a signature.
    [string]$SignToolPath,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

# Compiles artifacts\release\SIDETALK-Setup.exe from an existing publish folder. This
# never installs, starts or stops anything: the current Windows installation and every
# running program are unchanged whether this succeeds or fails. The previous Setup
# artifact is replaced only after the new one compiled and verified.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'build-common.ps1')
$timer = [Diagnostics.Stopwatch]::StartNew()
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = Join-Path $repo 'artifacts'
$release = Join-Path $artifacts 'release'
$setupName = 'SIDETALK-Setup.exe'
$pinnedCompilerHash = '0A8757031B33777E4C9CBFFEE40F11A5062B36D25CBE144C1DB73B6102B80AD7'
# Third-party programs SIDETALK controls but never redistributes.
$forbiddenNames = @('KakaoTalk.exe', 'Start.exe', 'SandMan.exe', 'SbieCtrl.exe', 'SbieSvc.exe', 'SbieDll.dll', 'SbieDrv.sys', 'SbieIni.exe')
$requiredFiles = @('Sidetalk.exe', 'Sidetalk.dll', 'Sidetalk.Core.dll', 'Sidetalk.deps.json', 'Sidetalk.runtimeconfig.json',
    'README.md', 'tools\IsolationProbe\Sidetalk.IsolationProbe.exe', 'tools\IsolationProbe\Sidetalk.IsolationProbe.dll')
$staging = Join-Path $artifacts ('.installer-' + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $staging 'previous'
$buildLock = $null
$safeToCleanStaging = $true

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

function Assert-UnlinkedTree([string]$Root, [string]$What) {
    for ($ancestor = $Root; $ancestor; $ancestor = Split-Path -Parent $ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "$What is reached through a linked directory: $ancestor"
        }
    }
    $links = @(Get-ChildItem -LiteralPath $Root -Recurse -Force -Attributes ReparsePoint)
    if ($links.Count) { throw "$What contains a linked file or folder: $($links[0].FullName)" }
}

function Move-ArtifactItem([string]$Source, [string]$Destination) {
    Assert-ArtifactPath $Source
    Assert-ArtifactPath $Destination
    Move-Item -LiteralPath $Source -Destination $Destination
}

function Get-ProductVersion([string]$Path) {
    return ([string][Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion).Split('+')[0].Trim()
}

Push-Location -LiteralPath $repo
try {
    Start-SidetalkStage 'installer/preflight' 50
    $source = [IO.Path]::GetFullPath($(if ($SourceDir) { $SourceDir } else { Join-Path $artifacts 'publish\win-x64' }))
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Installer source was not found: $source. Run scripts\build.ps1 -SelfContained first." }
    Assert-UnlinkedTree $source 'Installer source'
    foreach ($required in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $source $required) -PathType Leaf)) { throw "Installer source is missing $required`: $source" }
    }
    $foreign = @(Get-ChildItem -LiteralPath $source -Recurse -File -Force | Where-Object { $forbiddenNames -contains $_.Name })
    if ($foreign.Count) { throw "Installer source contains a KakaoTalk or Sandboxie file that SIDETALK must not redistribute: $($foreign[0].FullName)" }
    if (-not (Test-Path -LiteralPath (Join-Path $source 'docs') -PathType Container)) { throw "Installer source is missing docs: $source" }

    [xml]$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
    $expectedVersion = [string]($props.Project.PropertyGroup | ForEach-Object { $_.SidetalkVersion } | Where-Object { $_ } | Select-Object -First 1)
    $version = Get-ProductVersion (Join-Path $source 'Sidetalk.dll')
    if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw "Invalid SIDETALK product version in Sidetalk.dll: '$version'" }
    if ($version -ne $expectedVersion) { throw "Installer source is version $version but Directory.Build.props is $expectedVersion. Publish the current source first." }
    if ((Get-ProductVersion (Join-Path $source 'Sidetalk.exe')) -ne $version) { throw 'Sidetalk.exe and Sidetalk.dll report different versions.' }
    $versionInfoVersion = "$version.0"

    $iscc = if ($ISCCPath) { [IO.Path]::GetFullPath($ISCCPath) } else {
        $prepared = @(& (Join-Path $PSScriptRoot 'prepare-inno.ps1'))
        if ($LASTEXITCODE -ne 0) { throw 'The pinned Inno Setup compiler could not be prepared. See [inno/bootstrap] above.' }
        [string]$prepared[-1]
    }
    if (-not (Test-Path -LiteralPath $iscc -PathType Leaf) -or (Get-FileHash -LiteralPath $iscc -Algorithm SHA256).Hash -ne $pinnedCompilerHash) {
        throw "ISCC.exe is not the pinned Inno Setup 6.7.3 compiler: $iscc"
    }

    $signing = [bool]$SignToolPath -or [bool]$CertificateThumbprint
    if ($signing) {
        if (-not $SignToolPath -or -not $CertificateThumbprint) { throw 'Signing requires both -SignToolPath and -CertificateThumbprint.' }
        if (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) { throw "Signing tool was not found: $SignToolPath" }
        if ($CertificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'Certificate thumbprint must be a 40-hex SHA-1 thumbprint.' }
        if ($TimestampUrl -notmatch '^https?://') { throw 'TimestampUrl must be an HTTP(S) URL.' }
    }

    $setup = Join-Path $release $setupName
    $hashFile = $setup + '.sha256'
    $unsignedFile = $setup + '.unsigned.txt'
    foreach ($path in @($setup, $hashFile, $unsignedFile, $staging)) { Assert-ArtifactPath $path }
    New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
    $lockPath = Join-Path $artifacts '.publish.lock'
    Assert-ArtifactPath $lockPath
    # The same lock as build.ps1: the publish source cannot be replaced mid-compile.
    $buildLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    Complete-SidetalkStage

    Start-SidetalkStage 'installer/compile' 51
    New-Item -ItemType Directory -Path $staging | Out-Null
    $arguments = @('/Qp', "/O$staging", '/FSIDETALK-Setup', "/DAppVersion=$version", "/DVersionInfoVersion=$versionInfoVersion", "/DSourceDir=$source")
    if ($signing) {
        $quote = { param([string]$Value) '$q' + $Value + '$q' }
        $signCommand = '{0} sign /fd SHA256 /sha1 {1} /tr {2} /td SHA256 /d $qSIDETALK Setup$q $f' -f `
            (& $quote ([IO.Path]::GetFullPath($SignToolPath))), $CertificateThumbprint, (& $quote $TimestampUrl)
        $arguments += @('/DSidetalkSigningEnabled', "/Ssidetalksign=$signCommand")
    }
    $arguments += (Join-Path $repo 'installer\SIDETALK.iss')
    & $iscc @arguments
    if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE. The previous $setupName was kept." }
    Complete-SidetalkStage

    Start-SidetalkStage 'installer/verify' 52
    $stagedSetup = Join-Path $staging $setupName
    if (-not (Test-Path -LiteralPath $stagedSetup -PathType Leaf)) { throw 'ISCC.exe reported success but produced no SIDETALK-Setup.exe.' }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($stagedSetup)
    # Inno pads these resource strings with spaces; ProductVersion is the text version.
    if (([string]$info.ProductName).Trim() -ne 'SIDETALK' -or ([string]$info.ProductVersion).Trim() -ne $version -or
        ([string]$info.FileDescription).Trim() -ne 'SIDETALK Setup') {
        throw "Compiled installer resource does not match SIDETALK $version (product '$($info.ProductName)', version '$($info.ProductVersion)', description '$($info.FileDescription)')."
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $stagedSetup
    if ($signing) {
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or $null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint.ToUpperInvariant()) {
            throw 'Compiled installer is not signed by the requested certificate.'
        }
    } else {
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) { throw "Unsigned build unexpectedly carries a signature ($($signature.Status))." }
        [IO.File]::WriteAllText((Join-Path $staging ($setupName + '.unsigned.txt')),
            "SIDETALK-Setup.exe $version is unsigned. Windows SmartScreen may warn before it runs.`r`nCompiler: Inno Setup 6.7.3 (pinned).`r`n",
            [Text.UTF8Encoding]::new($false))
    }
    $hash = (Get-FileHash -LiteralPath $stagedSetup -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $staging ($setupName + '.sha256')), "$hash  $setupName`r`n", [Text.UTF8Encoding]::new($false))
    Complete-SidetalkStage

    Start-SidetalkStage 'installer/replace' 53
    New-Item -ItemType Directory -Path $release -Force | Out-Null
    Assert-ArtifactPath $release
    New-Item -ItemType Directory -Path $backup | Out-Null
    $safeToCleanStaging = $false
    $moved = [Collections.Generic.List[string]]::new()
    $backedUp = [Collections.Generic.List[string]]::new()
    try {
        foreach ($target in @($setup, $hashFile, $unsignedFile)) {
            if (Test-Path -LiteralPath $target) {
                Move-ArtifactItem $target (Join-Path $backup (Split-Path -Leaf $target))
                $backedUp.Add($target)
            }
        }
        $publish = @($setupName, ($setupName + '.sha256'))
        if (-not $signing) { $publish += $setupName + '.unsigned.txt' }
        foreach ($name in $publish) {
            Move-ArtifactItem (Join-Path $staging $name) (Join-Path $release $name)
            $moved.Add((Join-Path $release $name))
        }
        $safeToCleanStaging = $true
    } catch {
        $promotionError = $_
        try {
            foreach ($target in $moved) { Move-ArtifactItem $target (Join-Path $staging (Split-Path -Leaf $target)) }
            foreach ($target in $backedUp) { Move-ArtifactItem (Join-Path $backup (Split-Path -Leaf $target)) $target }
            $safeToCleanStaging = $true
        } catch {
            $failure = [InvalidOperationException]::new("Installer replacement and rollback failed. Preserved files: $staging. Replacement: $promotionError. Rollback: $_", $promotionError.Exception)
            $failure.Data['SidetalkExitCode'] = 54
            throw $failure
        }
        throw $promotionError
    }
    Complete-SidetalkStage
    Write-Host "Installer: $setup"
    Write-Host "SHA256: $hash"
    Write-Host ("Authenticode: {0}" -f $(if ($signing) { "valid ($($CertificateThumbprint.ToUpperInvariant()))" } else { 'unsigned developer build' }))
    Write-Host ("Installer build complete ({0:F2}s). Nothing was installed." -f $timer.Elapsed.TotalSeconds)
} catch {
    exit (Write-SidetalkFailure $_)
} finally {
    try {
        if ($safeToCleanStaging -and (Test-Path -LiteralPath $staging)) {
            Assert-ArtifactPath $staging
            if (Get-ChildItem -LiteralPath $staging -Recurse -Force -Attributes ReparsePoint) { throw 'Linked files in installer staging.' }
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    } catch {
        Write-Warning "Could not clean installer staging; files preserved at $staging. $_"
    } finally {
        if ($null -ne $buildLock) { $buildLock.Dispose() }
        Pop-Location
    }
}
