[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ResultPath,

    [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'SIDETALK-BuildTools\dotnet'),

    [string]$InstallerUri = 'https://raw.githubusercontent.com/dotnet/install-scripts/e5cf1dd2d1540ed05ac84f8eb8c5cdec2807621e/src/dotnet-install.ps1'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$globalJsonPath = Join-Path $repoRoot 'global.json'
if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
    throw 'global.json was not found next to the SIDETALK build scripts.'
}

$globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
$requestedVersion = [string]$globalJson.sdk.version
if ([string]::IsNullOrWhiteSpace($requestedVersion) -or $requestedVersion -notmatch '\A\d+\.\d+\.\d+\z') {
    throw 'global.json does not contain a supported exact SDK version.'
}

$resultFile = [IO.Path]::GetFullPath($ResultPath)
$resultDirectory = Split-Path -Parent $resultFile
if ([string]::IsNullOrWhiteSpace($resultDirectory)) {
    throw 'The SDK result path must include a directory.'
}
New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null

function Test-SelectedSdk([string]$DotnetPath) {
    if ([string]::IsNullOrWhiteSpace($DotnetPath) -or -not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
        return $false
    }

    Push-Location $repoRoot
    try {
        $output = @(& $DotnetPath --version 2>$null)
        $exitCode = $LASTEXITCODE
        return $exitCode -eq 0 -and $output.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$output[-1])
    }
    catch {
        return $false
    }
    finally {
        Pop-Location
    }
}

function Write-SelectedSdk([string]$DotnetPath) {
    [IO.File]::WriteAllText($resultFile, [IO.Path]::GetFullPath($DotnetPath), [Text.UTF8Encoding]::new($false))
}

$installed = Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if ($installed -and (Test-SelectedSdk $installed.Source)) {
    Write-Host "Using the installed SDK selected by global.json: $($installed.Source)"
    Write-SelectedSdk $installed.Source
    exit 0
}

$installRootFull = [IO.Path]::GetFullPath($InstallRoot)
$installDir = Join-Path $installRootFull $requestedVersion
$localDotnet = Join-Path $installDir 'dotnet.exe'
if (Test-SelectedSdk $localDotnet) {
    Write-Host "Using the cached SIDETALK SDK $requestedVersion."
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
$deadline = [DateTime]::UtcNow.AddMinutes(3)
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

$staging = $null
$quarantine = $null
try {
    if (Test-SelectedSdk $localDotnet) {
        Write-SelectedSdk $localDotnet
        exit 0
    }

    $staging = Join-Path $installRootFull ('.staging-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging | Out-Null
    $installer = Join-Path $staging 'dotnet-install.ps1'

    Write-Host "Required .NET SDK $requestedVersion is missing."
    Write-Host "Installing a private SIDETALK build copy once. No administrator rights or system-wide SDK changes are required."
    Invoke-WebRequest -Uri $InstallerUri -OutFile $installer
    if ((Get-Item -LiteralPath $installer).Length -lt 10000) {
        throw 'The downloaded Microsoft dotnet-install script is unexpectedly small.'
    }
    # The source URL is pinned to an immutable commit in Microsoft's official
    # dotnet/install-scripts repository. Remove only the downloaded-script zone
    # marker so this child script can run under build-local.cmd's RemoteSigned policy.
    Unblock-File -LiteralPath $installer

    & $installer -Version $requestedVersion -InstallDir $staging -Architecture x64 -NoPath
    $installerExit = $LASTEXITCODE
    if ($installerExit -ne 0) {
        throw "Microsoft dotnet-install failed with exit code $installerExit."
    }

    Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue
    $stagedDotnet = Join-Path $staging 'dotnet.exe'
    if (-not (Test-SelectedSdk $stagedDotnet)) {
        throw "The downloaded SDK does not satisfy global.json ($requestedVersion)."
    }

    $signature = Get-AuthenticodeSignature -FilePath $stagedDotnet
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
        throw 'The bootstrapped dotnet.exe does not have a valid Microsoft signature.'
    }

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

    if ($quarantine -and (Test-Path -LiteralPath $quarantine)) {
        Remove-Item -LiteralPath $quarantine -Recurse -Force
        $quarantine = $null
    }

    $localDotnet = Join-Path $installDir 'dotnet.exe'
    if (-not (Test-SelectedSdk $localDotnet)) {
        throw 'The SIDETALK SDK cache could not be verified after installation.'
    }

    Write-Host "SIDETALK private SDK ready: $installDir"
    Write-SelectedSdk $localDotnet
}
finally {
    $lock?.Dispose()
    if ($staging -and (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
