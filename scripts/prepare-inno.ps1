[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'SIDETALK-BuildTools\innosetup'),

    # Official Inno Setup release, pinned by version, SHA-256 and publisher. The same
    # compiler release and hashes WebPier uses.
    [string]$InstallerUri = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe',

    [string]$InstallerSha256 = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732',

    [ValidateRange(1, 3600)]
    [int]$LockTimeoutSeconds = 180
)

# Build tool only: prepares a private, portable Inno Setup compiler for
# build-installer.ps1 and prints the ISCC.exe path. It never registers Inno Setup
# in Installed Apps, never changes a system-wide Inno Setup, and is never run by
# SIDETALK itself or by SIDETALK-Setup.exe.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$script:stage = 'Start'
$version = '6.7.3'
# Files ISCC.exe loads to compile SIDETALK.iss, pinned to the official 6.7.3 release.
$pinnedFiles = [ordered]@{
    'ISCC.exe' = '0A8757031B33777E4C9CBFFEE40F11A5062B36D25CBE144C1DB73B6102B80AD7'
    'ISCmplr.dll' = '85A1E3090D3A5B85319F001B7C8F9ECFAD45F37EFF030A67BBE29EF58B7AA2C3'
    'ISPP.dll' = 'BDE04BE7F4A55CA56AFB6B06600D7CBE8326FAC468AC3F2885B80EEFB3ED2A7A'
    'Setup.e32' = 'A80D75BA8D8C336050C37F7707DE8BB017CC93597329E0EA18D474156B3E0DA5'
    'SetupLdr.e32' = '5475964893ADBB33CCC420AD4D88BBDFF27DFAACA7580CD6058EA2798893490D'
    'Default.isl' = '42A5F6F7DBBDDF26CC278F67DB5D894235CE1D126A6856702E31BD02023A1316'
    'Languages\Korean.isl' = 'CB56D6EA5C082BCF6B4ACC1AE4C303BA91F662B1D0BFE05E4D3902C38C41E02A'
}
$treeManifestName = '.sidetalk-tree.sha256'

function Enter-Stage([string]$Name) {
    $script:stage = $Name
    Write-Host "[inno/bootstrap] $Name"
}

function Assert-NotLinked([string]$Path) {
    for ($ancestor = [IO.Path]::GetFullPath($Path); $ancestor; $ancestor = Split-Path -Parent $ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to use build tools through a linked directory: $ancestor"
        }
    }
}

function Get-TreeLines([string]$Root) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    foreach ($file in @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Sort-Object FullName)) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked file in the Inno Setup cache: $($file.FullName)" }
        $relative = $file.FullName.Substring($prefix.Length)
        if ($relative -eq $treeManifestName) { continue }
        '{0} {1}' -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash, $relative
    }
}

# A cache is used only when every pinned file and every file recorded when it was
# prepared still has its exact bytes. Anything else is incomplete or changed.
function Test-CompilerTree([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return $false }
    try {
        Assert-NotLinked $Root
        if (@(Get-ChildItem -LiteralPath $Root -Recurse -Force -Attributes ReparsePoint).Count) { return $false }
        foreach ($entry in $pinnedFiles.GetEnumerator()) {
            $path = Join-Path $Root $entry.Key
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Value) { return $false }
        }
        $manifest = Join-Path $Root $treeManifestName
        if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { return $false }
        $expected = @(Get-Content -LiteralPath $manifest -Encoding utf8 | Where-Object { $_ })
        $actual = @(Get-TreeLines $Root)
        return $expected.Count -gt 0 -and (($expected -join "`n") -ceq ($actual -join "`n"))
    }
    catch {
        return $false
    }
}

function Remove-LeftoverDirectory([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) { return }
    $linked = ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        @(Get-ChildItem -LiteralPath $Path -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue).Count -gt 0
    if ($linked) {
        Write-Warning "Leaving a linked leftover folder untouched: $Path"
        return
    }
    try { Remove-Item -LiteralPath $Path -Recurse -Force }
    catch { Write-Warning "Could not remove the leftover Inno Setup folder $Path. It will be retried next time." }
}

try {
    $rootFull = [IO.Path]::GetFullPath($InstallRoot)
    $target = Join-Path $rootFull $version
    $compiler = Join-Path $target 'ISCC.exe'

    Enter-Stage 'Check private compiler cache'
    if (Test-CompilerTree $target) {
        Write-Host "Using the cached Inno Setup $version compiler: $target"
        Write-Output $compiler
        exit 0
    }

    New-Item -ItemType Directory -Path $rootFull -Force | Out-Null
    Assert-NotLinked $rootFull
    $lock = $null
    $staging = $null
    try {
        Enter-Stage 'Acquire setup lock'
        $deadline = [DateTime]::UtcNow.AddSeconds($LockTimeoutSeconds)
        while ($null -eq $lock) {
            try { $lock = [IO.File]::Open((Join-Path $rootFull '.install.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
            catch [IO.IOException] {
                if ([DateTime]::UtcNow -ge $deadline) { throw 'Another SIDETALK Inno Setup preparation is still running. Wait for it to finish and retry.' }
                Start-Sleep -Milliseconds 400
            }
        }
        if (Test-CompilerTree $target) {
            Write-Output $compiler
            exit 0
        }

        Enter-Stage 'Clean previous attempt'
        foreach ($leftover in @(Get-ChildItem -LiteralPath $rootFull -Directory -Force -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like '.staging-*' -or $_.Name -like "$version.invalid-*" })) {
            Remove-LeftoverDirectory $leftover.FullName
        }

        $staging = Join-Path $rootFull ('.staging-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $staging | Out-Null
        $package = Join-Path $staging "innosetup-$version.exe"
        $tree = Join-Path $staging 'compiler'

        Write-Host "Inno Setup $version is needed to build SIDETALK-Setup.exe. Preparing a private portable copy once."
        Enter-Stage 'Download'
        Invoke-WebRequest -UseBasicParsing -Uri $InstallerUri -OutFile $package
        $actualHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
        if ($actualHash -ne $InstallerSha256) {
            throw "The downloaded Inno Setup package does not match the pinned SHA-256 (got $actualHash)."
        }

        Enter-Stage 'Verify signature'
        $signature = Get-AuthenticodeSignature -LiteralPath $package
        if ($null -eq $signature -or $signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            throw "The Inno Setup package signature is not valid (status: $($signature.Status))."
        }
        if ($null -eq $signature.SignerCertificate -or [string]$signature.SignerCertificate.Subject -notmatch '(^|,\s*)CN=Pyrsys B\.V\.(\s*,|$)') {
            throw 'The Inno Setup package is not signed by its official publisher (Pyrsys B.V.).'
        }

        Enter-Stage 'Extract'
        # /PORTABLE=1 is the official package's portable mode: no uninstaller, no
        # Installed Apps entry, no file associations. Nothing outside $tree changes.
        $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/CURRENTUSER', '/SP-',
            '/NOICONS', '/PORTABLE=1', ('/DIR="' + $tree + '"'), ('/LOG="' + (Join-Path $staging 'extract.log') + '"'))
        $process = Start-Process -FilePath $package -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "The Inno Setup package exited with code $($process.ExitCode)." }
        if (Test-Path -LiteralPath (Join-Path $tree 'unins000.exe')) {
            throw 'The Inno Setup package did not run in portable mode.'
        }

        Enter-Stage 'Verify compiler'
        foreach ($entry in $pinnedFiles.GetEnumerator()) {
            $path = Join-Path $tree $entry.Key
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Value) {
                throw "The prepared compiler file does not match the pinned Inno Setup $version release: $($entry.Key)"
            }
        }
        [IO.File]::WriteAllLines((Join-Path $tree $treeManifestName), [string[]]@(Get-TreeLines $tree), [Text.UTF8Encoding]::new($false))

        Enter-Stage 'Promote cache'
        if (Test-Path -LiteralPath $target) {
            if ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to replace a linked compiler cache: $target"
            }
            Move-Item -LiteralPath $target -Destination ($target + '.invalid-' + [Guid]::NewGuid().ToString('N'))
        }
        Move-Item -LiteralPath $tree -Destination $target
        if (-not (Test-CompilerTree $target)) { throw 'The Inno Setup compiler cache could not be verified after preparation.' }
        Enter-Stage "Ready: Inno Setup $version at $target"
        Write-Output $compiler
    }
    finally {
        if ($null -ne $lock) { $lock.Dispose() }
        if ($staging -and (Test-Path -LiteralPath $staging)) { Remove-LeftoverDirectory $staging }
    }
    exit 0
}
catch {
    Write-Host "[inno/bootstrap] Failed during: $($script:stage)"
    Write-Host "  $($_.Exception.Message)"
    exit 1
}
