[CmdletBinding()]
param(
    # Build SIDETALK-Setup.exe only; the Windows installation is not changed.
    [switch]$NoInstall,
    # Install without offering "SIDETALK 실행" on the last Setup page.
    [switch]$NoLaunch,
    # Dot-source the functions for tests without running anything.
    [switch]$LoadOnly
)

# Local developer flow after scripts\build.ps1 -SelfContained has published the current
# source: compile SIDETALK-Setup.exe, open it, wait for the person to finish or cancel,
# then confirm the installed files are exactly this build. Nothing here stops a process,
# and %LOCALAPPDATA%\SIDETALK (profile, boxes, history) is only read to prove it did not
# change. KakaoTalk and Sandboxie are never inspected or touched.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'build-common.ps1')

$script:InstallStage = 'installer'
$script:InnoExitCodes = @{
    Cancelled = @(2, 5)   # before installing / during installing (Inno rolls back)
    Blocked = 7           # PrepareToInstall refused: running SIDETALK, downgrade, location
}

function Get-DefaultInstallRoot {
    Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Programs\SIDETALK'
}

function Get-DefaultProgramsDir { [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs) }

function Get-UserDataRoot {
    Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'SIDETALK'
}

# What the person may rely on when this run stops at a given stage.
function Get-InstallStageGuidance([string]$Stage) {
    switch ($Stage) {
        'installer' { 'SIDETALK-Setup.exe was not produced or replaced. Setup was not started; the installed SIDETALK is unchanged.' }
        'install-preflight' { 'Setup was not started. The installed SIDETALK and every running program are unchanged.' }
        'install' { 'SIDETALK-Setup.exe ran. Read its log before assuming the previous installation is intact.' }
        'verify-install' { 'Setup reported success but the installation is not exactly this build. Do not assume the previous version is intact.' }
        default { 'See the output above.' }
    }
}

function Get-OwnedRunningProcesses([string]$InstallRoot) {
    $root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\'
    foreach ($process in @(Get-Process -Name 'Sidetalk', 'Sidetalk.IsolationProbe' -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $process.Path } catch { }
        # An unreadable path cannot be ruled out as the installed copy.
        if (-not $path -or $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
            [pscustomobject]@{ Id = $process.Id; Path = $(if ($path) { $path } else { '(unknown path)' }) }
        }
    }
}

# Read-only fingerprint of SIDETALK's own data root, used to prove Setup left it alone.
function Get-UserDataFingerprint([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return 'absent' }
    $profile = Join-Path $Root 'profile.json'
    $profileHash = if (Test-Path -LiteralPath $profile -PathType Leaf) { (Get-FileHash -LiteralPath $profile -Algorithm SHA256).Hash } else { 'none' }
    $entries = @(Get-ChildItem -LiteralPath $Root -Force | Sort-Object Name | ForEach-Object { $_.Name }) -join '|'
    "profile=$profileHash;entries=$entries"
}

function Resolve-SetupOutcome([int]$ExitCode) {
    if ($ExitCode -eq 0) { return 'Installed' }
    if ($script:InnoExitCodes.Cancelled -contains $ExitCode) { return 'Cancelled' }
    if ($ExitCode -eq $script:InnoExitCodes.Blocked) { return 'Blocked' }
    'Failed'
}

function Invoke-SetupAndWait([string]$SetupPath, [string[]]$Arguments) {
    Write-Host ("    > {0} {1}" -f $SetupPath, ($Arguments -join ' '))
    $process = Start-Process -FilePath $SetupPath -ArgumentList $Arguments -PassThru
    if (-not $process) { throw "Could not start $SetupPath" }
    try {
        $process.WaitForExit()
        return [int]$process.ExitCode
    }
    finally { $process.Dispose() }
}

function Get-ShortcutTarget([string]$Path) {
    $shell = New-Object -ComObject WScript.Shell
    try { return [string]$shell.CreateShortcut($Path).TargetPath }
    finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
}

function Assert-InstalledBuild {
    param(
        [Parameter(Mandatory)][string]$PublishDir,
        [Parameter(Mandatory)][string]$InstallRoot,
        [Parameter(Mandatory)][string]$ProgramsDir,
        [Parameter(Mandatory)][string]$Version,
        [string]$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{AF6EF9DE-E012-4945-BBD1-39ED6572282D}_is1'
    )
    $installedExe = Join-Path $InstallRoot 'Sidetalk.exe'
    foreach ($relative in @('Sidetalk.exe', 'Sidetalk.dll', 'Sidetalk.Core.dll', 'tools\IsolationProbe\Sidetalk.IsolationProbe.exe')) {
        $installed = Join-Path $InstallRoot $relative
        if (-not (Test-Path -LiteralPath $installed -PathType Leaf)) { throw "Setup finished but $installed is missing." }
        $expected = (Get-FileHash -LiteralPath (Join-Path $PublishDir $relative) -Algorithm SHA256).Hash
        $actual = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "Installed $relative is not this build (SHA-256 $actual, expected $expected)." }
    }
    $shortcut = Join-Path $ProgramsDir 'SIDETALK.lnk'
    if (-not (Test-Path -LiteralPath $shortcut -PathType Leaf)) { throw "The Start Menu shortcut is missing: $shortcut" }
    $target = Get-ShortcutTarget $shortcut
    if (-not [IO.Path]::GetFullPath($target).Equals([IO.Path]::GetFullPath($installedExe), [StringComparison]::OrdinalIgnoreCase)) {
        throw "The Start Menu shortcut runs $target, not $installedExe."
    }
    if (-not (Test-Path -LiteralPath $UninstallKey)) { throw "The Installed Apps registration is missing: $UninstallKey" }
    $registration = Get-ItemProperty -LiteralPath $UninstallKey
    if ([string]$registration.DisplayVersion -ne $Version) { throw "Installed Apps lists version '$($registration.DisplayVersion)', expected $Version." }
    if ([string]$registration.DisplayName -ne 'SIDETALK') { throw "Installed Apps lists '$($registration.DisplayName)', expected SIDETALK." }
    [pscustomobject]@{ Executable = $installedExe; Shortcut = $shortcut; Registration = $UninstallKey }
}

function Invoke-InstallLocal {
    param(
        [switch]$NoInstall,
        [switch]$NoLaunch,
        # Tests pass disposable locations; the defaults are the real per-user ones.
        [string]$InstallRoot = (Get-DefaultInstallRoot),
        [string]$ProgramsDir = (Get-DefaultProgramsDir),
        [string]$DataRoot = (Get-UserDataRoot)
    )
    $repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    $publish = Join-Path $repo 'artifacts\publish\win-x64'
    $setup = Join-Path $repo 'artifacts\release\SIDETALK-Setup.exe'
    $installRoot = $InstallRoot
    $programs = $ProgramsDir

    $script:InstallStage = 'installer'
    & (Join-Path $PSScriptRoot 'build-installer.ps1')
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Write-Host (Get-InstallStageGuidance 'installer')
        return $code
    }
    if ($NoInstall) {
        Write-Host "Setup ready: $setup"
        Write-Host 'Not installed (-NoInstall). The installed SIDETALK, if any, is unchanged.'
        return 0
    }

    Start-SidetalkStage 'install/preflight' 60
    $script:InstallStage = 'install-preflight'
    $running = @(Get-OwnedRunningProcesses $installRoot)
    if ($running.Count) {
        foreach ($item in $running) { Write-Host "Running from the installation: PID $($item.Id) $($item.Path)" }
        throw 'Close SIDETALK and its IsolationProbe normally, then run build-local.cmd again. Nothing was stopped; KakaoTalk and Sandboxie do not need to close.'
    }
    $version = ([string][Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'Sidetalk.dll')).ProductVersion).Split('+')[0]
    $dataRoot = $DataRoot
    $dataBefore = Get-UserDataFingerprint $dataRoot
    Complete-SidetalkStage

    Start-SidetalkStage 'install/setup' 61
    $script:InstallStage = 'install'
    $logDir = Join-Path $repo 'artifacts\release\logs'
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $log = Join-Path $logDir ('install-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
    $arguments = @("/LOG=`"$log`"")
    if ($NoLaunch) { $arguments += '/SIDETALKNOLAUNCH' }
    Write-Host 'Opening SIDETALK-Setup.exe. Approve or cancel the installation in its window.'
    $setupExit = Invoke-SetupAndWait $setup $arguments
    switch (Resolve-SetupOutcome $setupExit) {
        'Cancelled' {
            Write-Host "Installation cancelled in Setup (exit $setupExit). Inno Setup rolls back a cancelled install; see $log."
            $failure = [InvalidOperationException]::new('Installation was cancelled; this is not a successful build-local run.')
            $failure.Data['SidetalkExitCode'] = 62
            throw $failure
        }
        'Blocked' {
            $failure = [InvalidOperationException]::new("Setup refused to install before copying files (exit 7). See its message and $log. The previous installation is unchanged.")
            $failure.Data['SidetalkExitCode'] = 64
            throw $failure
        }
        'Failed' { throw "SIDETALK-Setup.exe failed (exit $setupExit). See $log." }
    }
    Complete-SidetalkStage

    Start-SidetalkStage 'install/verify' 63
    $script:InstallStage = 'verify-install'
    $result = Assert-InstalledBuild -PublishDir $publish -InstallRoot $installRoot -ProgramsDir $programs -Version $version
    $dataAfter = Get-UserDataFingerprint $dataRoot
    if ($dataAfter -ne $dataBefore) { throw "SIDETALK user data changed during Setup ($dataBefore -> $dataAfter)." }
    Complete-SidetalkStage
    Write-Host "SIDETALK $version is installed: $($result.Executable)"
    Write-Host "Start Menu: $($result.Shortcut)"
    Write-Host "Installed Apps: $($result.Registration)"
    Write-Host "User data unchanged: $dataRoot"
    Write-Host 'Installation is not readiness: SIDETALK still checks KakaoTalk, Sandboxie and its box before any launch.'
    return 0
}

if ($LoadOnly) { return }
try {
    exit (Invoke-InstallLocal -NoInstall:$NoInstall -NoLaunch:$NoLaunch)
} catch {
    $code = Write-SidetalkFailure $_
    Write-Host (Get-InstallStageGuidance $script:InstallStage)
    exit $code
}
