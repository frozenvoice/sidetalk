[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ResultPath,

    [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'SIDETALK-BuildTools\dotnet'),

    # Microsoft's official dotnet/install-scripts at an immutable commit. The
    # SHA-256 below is the exact content served for that commit.
    [string]$InstallerUri = 'https://raw.githubusercontent.com/dotnet/install-scripts/e5cf1dd2d1540ed05ac84f8eb8c5cdec2807621e/src/dotnet-install.ps1',

    [string]$InstallerSha256 = '3bb07bc8025211836c1e4f9d3f6a044e55b1fb6eec518a6c78851d04e210442b',

    [ValidateRange(1, 3600)]
    [int]$LockTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
# Native exit codes are read explicitly; never let them turn into terminating errors.
$PSNativeCommandUseErrorActionPreference = $false
$script:stage = 'Read global.json'

function Enter-Stage([string]$Name) {
    $script:stage = $Name
    Write-Host "[dotnet/bootstrap] $Name"
}

function ConvertTo-SdkVersion([string]$Text) {
    if ([string]$Text -notmatch '\A(\d+)\.(\d+)\.(\d+)\z') {
        return $null
    }
    return [pscustomobject]@{
        Major = [int]$Matches[1]
        Minor = [int]$Matches[2]
        Band = [int][Math]::Floor([int]$Matches[3] / 100)
        Patch = [int]$Matches[3]
    }
}

# Mirrors the global.json roll-forward families so an SDK that `dotnet --version`
# reports is accepted only when global.json itself would accept it.
function Test-SdkSatisfies([string]$Actual) {
    $have = ConvertTo-SdkVersion $Actual
    if ($null -eq $have) {
        return $false
    }
    $want = $script:requested
    $sameMinor = $have.Major -eq $want.Major -and $have.Minor -eq $want.Minor
    switch ($script:rollForward) {
        'disable' { return $sameMinor -and $have.Patch -eq $want.Patch }
        { $_ -in @('patch', 'latestPatch') } { return $sameMinor -and $have.Band -eq $want.Band -and $have.Patch -ge $want.Patch }
        { $_ -in @('feature', 'latestFeature') } { return $sameMinor -and $have.Patch -ge $want.Patch }
        { $_ -in @('minor', 'latestMinor') } { return $have.Major -eq $want.Major -and ($have.Minor -gt $want.Minor -or ($have.Minor -eq $want.Minor -and $have.Patch -ge $want.Patch)) }
        default {
            return $have.Major -gt $want.Major -or ($have.Major -eq $want.Major -and
                ($have.Minor -gt $want.Minor -or ($have.Minor -eq $want.Minor -and $have.Patch -ge $want.Patch)))
        }
    }
}

# Returns the SDK version that the given dotnet.exe resolves from the
# repository root (where global.json applies), or $null when it does not
# satisfy global.json. Existence of dotnet.exe alone is never enough.
function Get-SelectedSdkVersion([string]$DotnetPath) {
    if ([string]::IsNullOrWhiteSpace($DotnetPath) -or -not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
        return $null
    }

    Push-Location -LiteralPath $repoRoot
    try {
        $global:LASTEXITCODE = 0
        $output = @(& $DotnetPath --version 2>$null)
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0 -or $output.Count -eq 0) {
            return $null
        }
        $version = ([string]$output[-1]).Trim()
        if (Test-SdkSatisfies $version) {
            return $version
        }
        return $null
    }
    catch {
        return $null
    }
    finally {
        $global:LASTEXITCODE = 0
        Pop-Location
    }
}

function Write-SelectedSdk([string]$DotnetPath) {
    $full = [IO.Path]::GetFullPath($DotnetPath)
    # build-local.cmd reads this file with `set /p`, which decodes bytes with
    # the console code page (e.g. 949), not UTF-8. Encode for that code page
    # so non-ASCII profile paths survive, and refuse paths it cannot carry.
    $encoding = [Console]::OutputEncoding
    $bytes = $encoding.GetBytes($full)
    if ($encoding.GetString($bytes) -ne $full) {
        throw "The SDK path cannot be represented in console code page $($encoding.CodePage): $full"
    }
    [IO.File]::WriteAllBytes($resultFile, $bytes)
}

function Assert-MicrosoftSignature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($null -eq $signature) {
        throw 'No Authenticode result was returned for the bootstrapped dotnet.exe.'
    }
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "The bootstrapped dotnet.exe signature is not valid (status: $($signature.Status))."
    }
    $certificate = $signature.SignerCertificate
    if ($null -eq $certificate) {
        throw 'The bootstrapped dotnet.exe signature has no signer certificate.'
    }
    $organization = '(^|,\s*)O=Microsoft Corporation(\s*,|$)'
    if ([string]$certificate.Subject -notmatch $organization -or [string]$certificate.Issuer -notmatch $organization) {
        throw "The bootstrapped dotnet.exe is not signed by Microsoft Corporation (subject: $($certificate.Subject))."
    }
}

# Removes a previous attempt's leftover folder inside the private SDK root.
# Only called while holding the install lock, so no other setup can own it.
function Remove-LeftoverDirectory([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) {
        return
    }
    $linked = ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        @(Get-ChildItem -LiteralPath $Path -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue).Count -gt 0
    if ($linked) {
        Write-Warning "Leaving a linked leftover folder untouched: $Path"
        return
    }
    try {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    catch {
        Write-Warning "Could not remove the leftover SDK setup folder $Path. It will be retried next time."
    }
}

try {
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $globalJsonPath = Join-Path $repoRoot 'global.json'
    if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
        throw 'global.json was not found next to the SIDETALK build scripts.'
    }

    $globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
    $requestedVersion = [string]$globalJson.sdk.version
    $script:requested = ConvertTo-SdkVersion $requestedVersion
    if ($null -eq $script:requested) {
        throw 'global.json does not contain a supported exact SDK version.'
    }
    $script:rollForward = if ([string]::IsNullOrWhiteSpace([string]$globalJson.sdk.rollForward)) { 'latestPatch' } else { [string]$globalJson.sdk.rollForward }

    $resultFile = [IO.Path]::GetFullPath($ResultPath)
    $resultDirectory = Split-Path -Parent $resultFile
    if ([string]::IsNullOrWhiteSpace($resultDirectory)) {
        throw 'The SDK result path must include a directory.'
    }
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null

    Enter-Stage 'Check installed SDK'
    $installed = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $installed) {
        $version = Get-SelectedSdkVersion $installed.Source
        if ($version) {
            Write-Host "Using the installed SDK $version selected by global.json: $($installed.Source)"
            Write-SelectedSdk $installed.Source
            exit 0
        }
    }

    Enter-Stage 'Check private SDK cache'
    $installRootFull = [IO.Path]::GetFullPath($InstallRoot)
    $installDir = Join-Path $installRootFull $requestedVersion
    $localDotnet = Join-Path $installDir 'dotnet.exe'
    $version = Get-SelectedSdkVersion $localDotnet
    if ($version) {
        Write-Host "Using the cached SIDETALK SDK $version`: $installDir"
        Write-SelectedSdk $localDotnet
        exit 0
    }

    New-Item -ItemType Directory -Path $installRootFull -Force | Out-Null
    $rootItem = Get-Item -LiteralPath $installRootFull -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to install build tools through a linked directory: $installRootFull"
    }

    $lockPath = Join-Path $installRootFull '.install.lock'
    $lock = $null
    $staging = $null
    $quarantine = $null
    try {
        Enter-Stage 'Acquire setup lock'
        $deadline = [DateTime]::UtcNow.AddSeconds($LockTimeoutSeconds)
        while ($null -eq $lock) {
            try {
                $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            }
            catch [IO.IOException] {
                if ([DateTime]::UtcNow -ge $deadline) {
                    throw 'Another SIDETALK SDK setup is still running. Wait for it to finish and retry.'
                }
                Start-Sleep -Milliseconds 400
            }
        }

        # Another build may have finished the same SDK while this one waited.
        $version = Get-SelectedSdkVersion $localDotnet
        if ($version) {
            Write-Host "Using the cached SIDETALK SDK $version`: $installDir"
            Write-SelectedSdk $localDotnet
            exit 0
        }

        Enter-Stage 'Clean previous attempt'
        # Interrupted downloads are never trusted or adopted, only replaced.
        foreach ($leftover in @(Get-ChildItem -LiteralPath $installRootFull -Directory -Force -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like '.staging-*' -or $_.Name -like "$requestedVersion.invalid-*" })) {
            Remove-LeftoverDirectory $leftover.FullName
        }
        if (Test-Path -LiteralPath $installDir) {
            $existing = Get-Item -LiteralPath $installDir -Force
            if (($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to replace a linked SDK cache directory: $installDir"
            }
        }

        $staging = Join-Path $installRootFull ('.staging-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $staging | Out-Null
        $installer = Join-Path $staging 'dotnet-install.ps1'

        Write-Host "Required .NET SDK $requestedVersion is missing."
        Write-Host 'Installing a private SIDETALK build copy once. No administrator rights or system-wide SDK changes are required.'
        Enter-Stage 'Download'
        Invoke-WebRequest -Uri $InstallerUri -OutFile $installer
        $actualHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
        if ($actualHash -ne $InstallerSha256) {
            throw "The downloaded Microsoft dotnet-install script does not match the pinned SHA-256 (got $actualHash)."
        }
        # Remove only the downloaded-script zone marker so this child script
        # can run under build-local.cmd's RemoteSigned policy.
        Unblock-File -LiteralPath $installer

        Enter-Stage 'Install'
        # dotnet-install.ps1 returns without `exit` on success, so a stale code
        # (e.g. from the unsatisfied system SDK above) must not be read back.
        $global:LASTEXITCODE = 0
        & $installer -Version $requestedVersion -InstallDir $staging -Architecture x64 -NoPath
        $installerExit = $LASTEXITCODE
        if ($installerExit -ne 0) {
            throw "Microsoft dotnet-install failed with exit code $installerExit."
        }
        Remove-Item -LiteralPath $installer -Force

        Enter-Stage 'Verify staged SDK'
        $stagedDotnet = Join-Path $staging 'dotnet.exe'
        $stagedVersion = Get-SelectedSdkVersion $stagedDotnet
        if (-not $stagedVersion) {
            throw "The downloaded SDK does not satisfy global.json ($requestedVersion)."
        }

        Enter-Stage 'Verify signature'
        Assert-MicrosoftSignature $stagedDotnet

        Enter-Stage 'Promote cache'
        if (Test-Path -LiteralPath $installDir) {
            $quarantine = $installDir + '.invalid-' + [Guid]::NewGuid().ToString('N')
            Move-Item -LiteralPath $installDir -Destination $quarantine
        }
        try {
            Move-Item -LiteralPath $staging -Destination $installDir
            $staging = $null
        }
        catch {
            if ($quarantine -and -not (Test-Path -LiteralPath $installDir) -and (Test-Path -LiteralPath $quarantine)) {
                Move-Item -LiteralPath $quarantine -Destination $installDir
                $quarantine = $null
            }
            throw
        }

        $version = Get-SelectedSdkVersion $localDotnet
        if (-not $version) {
            throw 'The SIDETALK SDK cache could not be verified after installation.'
        }
        if ($quarantine) {
            Remove-LeftoverDirectory $quarantine
            $quarantine = $null
        }

        Write-SelectedSdk $localDotnet
        Enter-Stage "Ready: SDK $version at $installDir"
    }
    finally {
        if ($null -ne $lock) {
            $lock.Dispose()
        }
        if ($staging -and (Test-Path -LiteralPath $staging)) {
            Remove-LeftoverDirectory $staging
        }
    }
    exit 0
}
catch {
    Write-Host "[dotnet/bootstrap] Failed during: $($script:stage)"
    Write-Host "  $($_.Exception.Message)"
    exit 1
}
