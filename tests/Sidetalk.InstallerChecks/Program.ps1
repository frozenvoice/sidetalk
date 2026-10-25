[CmdletBinding()]
param(
    [switch]$KeepFixtureOnFailure
)

# Real Inno Setup regression for installer\SIDETALK.iss and scripts\build-installer.ps1.
# Uses the pinned private compiler (scripts\prepare-inno.ps1) and the current Release build
# output as payload (run scripts\verify.ps1 first). Every install uses its own
# SIDETALKInstallerTest-* AppId, install folder, Start Menu folder, desktop folder and
# setup mutex under %TEMP%. The real installation, real Start Menu/desktop entries and
# %LOCALAPPDATA%\SIDETALK are only read, to prove they did not change. Nothing here starts
# Sidetalk.exe, KakaoTalk or any Sandboxie program.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('sidetalk-installer-checks-' + [Guid]::NewGuid().ToString('N'))
$failures = [Collections.Generic.List[string]]::new()
$timer = [Diagnostics.Stopwatch]::StartNew()
$productionAppId = 'AF6EF9DE-E012-4945-BBD1-39ED6572282D'
$innoBlocked = 7

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "$Message (expected '$Expected', got '$Actual')" }
}
function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

function Invoke-Case([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        Write-Host "PASS $Name"
    } catch {
        $failures.Add("$Name`: $($_.Exception.Message)")
        Write-Warning "FAIL $Name`: $($_.Exception.Message)"
    }
}

# Read-only snapshot of everything a fixture must never touch.
function Get-RealStateSnapshot {
    $local = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $rows = [ordered]@{}
    foreach ($path in @(
            (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) 'SIDETALK.lnk'),
            (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) 'SIDETALK.lnk'),
            (Join-Path $local 'Programs\SIDETALK\Sidetalk.exe'),
            (Join-Path $local 'SIDETALK\profile.json'))) {
        $rows[$path] = if (Test-Path -LiteralPath $path -PathType Leaf) { Get-Hash $path } else { 'absent' }
    }
    $data = Join-Path $local 'SIDETALK'
    $rows['data-entries'] = if (Test-Path -LiteralPath $data) { (@(Get-ChildItem -LiteralPath $data -Force | Sort-Object Name | ForEach-Object Name) -join '|') } else { 'absent' }
    $key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$productionAppId}_is1"
    $rows['registration'] = if (Test-Path -LiteralPath $key) { [string](Get-ItemProperty -LiteralPath $key).DisplayVersion } else { 'absent' }
    $rows | ConvertTo-Json -Compress
}

function New-Recorder([string]$Path) {
    # Stands in for "Sidetalk.exe --uninstall-owned-integration" inside fixture installs, so
    # the uninstall cleanup call is observed without reading any real user shortcut.
    $source = Join-Path $fixtureRoot 'FixtureCleanup.cs'
    Set-Content -LiteralPath $source -Encoding ascii -Value @'
using System;
using System.IO;
static class FixtureCleanup {
    static int Main(string[] args) {
        string log = Environment.GetEnvironmentVariable("SIDETALK_FIXTURE_CLEANUP_LOG");
        string self = System.Reflection.Assembly.GetEntryAssembly().Location;
        if (!string.IsNullOrEmpty(log)) File.AppendAllText(log, self + "|" + string.Join(" ", args) + "|" + File.Exists(Path.Combine(Path.GetDirectoryName(self), "Sidetalk.exe")) + "\r\n");
        string code = Environment.GetEnvironmentVariable("SIDETALK_FIXTURE_CLEANUP_EXIT");
        return string.IsNullOrEmpty(code) ? 0 : int.Parse(code);
    }
}
'@
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $out = & $csc /nologo /target:exe ("/out:$Path") $source 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Could not compile the cleanup recorder: $out" }
}

function New-Payload([string]$Destination, [string]$BuildMarker, [string[]]$ExtraDocs = @()) {
    $app = Join-Path $repo 'src\Sidetalk.App\bin\Release\net10.0-windows'
    $probe = Join-Path $repo 'src\Sidetalk.IsolationProbe\bin\Release\net10.0-windows'
    foreach ($required in @((Join-Path $app 'Sidetalk.exe'), (Join-Path $probe 'Sidetalk.IsolationProbe.exe'))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Release build output is missing ($required). Run pwsh -File scripts\verify.ps1 first." }
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -Path (Join-Path $app '*') -Destination $Destination -Recurse -Force
    New-Item -ItemType Directory -Path (Join-Path $Destination 'tools\IsolationProbe') -Force | Out-Null
    Copy-Item -Path (Join-Path $probe '*') -Destination (Join-Path $Destination 'tools\IsolationProbe') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $Destination
    New-Item -ItemType Directory -Path (Join-Path $Destination 'docs') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $Destination 'docs\fixture-build.txt') -Value $BuildMarker
    foreach ($doc in $ExtraDocs) { Set-Content -LiteralPath (Join-Path $Destination "docs\$doc") -Value $doc }
    Copy-Item -LiteralPath $script:recorder -Destination (Join-Path $Destination 'fixture-cleanup.exe')
}

function Invoke-Iscc([string[]]$Defines, [string]$Output) {
    New-Item -ItemType Directory -Path $Output -Force | Out-Null
    $arguments = @('/Qp', "/O$Output", '/FSIDETALK-Setup') + @($Defines | ForEach-Object { "/D$_" }) + (Join-Path $repo 'installer\SIDETALK.iss')
    $log = @(& $script:iscc @arguments 2>&1 | ForEach-Object { [string]$_ })
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Setup = (Join-Path $Output 'SIDETALK-Setup.exe'); Log = ($log -join "`n") }
}

function Compile-Fixture([string]$Version, [string]$Payload, [string]$Name) {
    $result = Invoke-Iscc @("AppVersion=$Version", "VersionInfoVersion=$Version.0", "SourceDir=$Payload", "AppId=$($script:appId)",
        "FixtureNamespaceSHA256=$($script:namespace)", "DefaultInstallDir=$($script:installDir)", "StartMenuDir=$($script:programs)",
        "DesktopDir=$($script:desktop)", 'IntegrationCleanupExe=fixture-cleanup.exe') (Join-Path $fixtureRoot "setup-$Name")
    if ($result.ExitCode -ne 0) { throw "Fixture compile $Name failed ($($result.ExitCode)): $($result.Log)" }
    $result.Setup
}

function Invoke-Setup([string]$Setup, [string]$Name, [string[]]$Extra = @()) {
    $log = Join-Path $fixtureRoot "$Name.log"
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/SIDETALKNOLAUNCH', "/LOG=`"$log`"") + $Extra
    $process = Start-Process -FilePath $Setup -ArgumentList $arguments -Wait -PassThru
    [pscustomobject]@{ ExitCode = $process.ExitCode; Log = $log }
}

function Invoke-Uninstall([string]$Name) {
    $uninstaller = Join-Path $script:installDir 'unins000.exe'
    $log = Join-Path $fixtureRoot "$Name.log"
    $process = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"") -Wait -PassThru
    # The uninstaller finishes in a relaunched temporary copy; wait for its outcome.
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while ([DateTime]::UtcNow -lt $deadline -and (Test-Path -LiteralPath $script:uninstallKey) -and $process.ExitCode -eq 0) { Start-Sleep -Milliseconds 250 }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline -and (Test-Path -LiteralPath $uninstaller) -and -not (Test-Path -LiteralPath $script:uninstallKey)) { Start-Sleep -Milliseconds 250 }
    [pscustomobject]@{ ExitCode = $process.ExitCode; Log = $log }
}

function Get-ShortcutInfo([string]$Path) {
    $shell = New-Object -ComObject WScript.Shell
    $app = New-Object -ComObject Shell.Application
    try {
        $link = $shell.CreateShortcut($Path)
        $item = $app.Namespace((Split-Path -Parent $Path)).ParseName((Split-Path -Leaf $Path))
        [pscustomobject]@{
            Target = [string]$link.TargetPath
            WorkingDirectory = [string]$link.WorkingDirectory
            AppUserModelId = [string]$item.ExtendedProperty('System.AppUserModel.ID')
        }
    } finally {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($app)
    }
}

function Assert-Installed([string]$Payload, [string]$Version, [string]$What) {
    foreach ($relative in @('Sidetalk.exe', 'Sidetalk.dll', 'Sidetalk.Core.dll', 'tools\IsolationProbe\Sidetalk.IsolationProbe.exe', 'docs\fixture-build.txt', 'README.md')) {
        $installed = Join-Path $script:installDir $relative
        Assert-True (Test-Path -LiteralPath $installed -PathType Leaf) "$What did not install $relative"
        Assert-Equal (Get-Hash (Join-Path $Payload $relative)) (Get-Hash $installed) "$What installed $relative bytes"
    }
    $exe = Join-Path $script:installDir 'Sidetalk.exe'
    $menu = Join-Path $script:programs 'SIDETALK.lnk'
    Assert-True (Test-Path -LiteralPath $menu -PathType Leaf) "$What did not create the Programs-root SIDETALK shortcut"
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $script:programs 'SIDETALK') -PathType Container)) "$What created a Start Menu folder instead of a root item"
    $info = Get-ShortcutInfo $menu
    Assert-Equal $exe $info.Target "$What Start Menu target"
    Assert-Equal $script:installDir $info.WorkingDirectory "$What Start Menu working directory"
    Assert-Equal $script:appUserModelId $info.AppUserModelId "$What Start Menu AppUserModelID"
    Assert-True (Test-Path -LiteralPath $script:uninstallKey) "$What did not register in Installed Apps ($($script:uninstallKey))"
    $registration = Get-ItemProperty -LiteralPath $script:uninstallKey
    Assert-Equal 'SIDETALK' ([string]$registration.DisplayName) "$What DisplayName"
    Assert-Equal $Version ([string]$registration.DisplayVersion) "$What DisplayVersion"
    Assert-Equal 'SIDETALK' ([string]$registration.Publisher) "$What Publisher"
    Assert-Equal $exe ([string]$registration.DisplayIcon).Split(',')[0].Trim('"') "$What DisplayIcon"
    Assert-Equal ($script:installDir + '\') ([string]$registration.InstallLocation) "$What InstallLocation"
    Assert-True ([string]$registration.UninstallString -like "*$($script:installDir)\unins000.exe*") "$What UninstallString"
    Assert-True (-not (Test-Path -LiteralPath "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$($script:appId)}_is1")) "$What registered machine-wide"
}

function Assert-DataSentinel([string]$What) {
    Assert-Equal $script:sentinelHash (Get-Hash $script:sentinel) "$What changed the user-data sentinel"
    Assert-Equal $script:boxHash (Get-Hash $script:boxSentinel) "$What changed the box sentinel"
}

function New-BuilderRepo([string]$Name) {
    $root = Join-Path $fixtureRoot "builder $Name"
    foreach ($dir in @('scripts', 'installer', 'assets\installer', 'src\Sidetalk.App\Assets', 'artifacts\release')) { New-Item -ItemType Directory -Path (Join-Path $root $dir) -Force | Out-Null }
    foreach ($file in @('scripts\build-installer.ps1', 'scripts\build-common.ps1', 'scripts\prepare-inno.ps1', 'installer\SIDETALK.iss', 'installer\SetupTheme.iss', 'src\Sidetalk.App\Assets\Sidetalk.ico', 'Directory.Build.props')) {
        Copy-Item -LiteralPath (Join-Path $repo $file) -Destination (Join-Path $root $file)
    }
    Copy-Item -Path (Join-Path $repo 'assets\installer\setup-*.png') -Destination (Join-Path $root 'assets\installer')
    New-Payload (Join-Path $root 'artifacts\publish\win-x64') 'builder'
    Remove-Item -LiteralPath (Join-Path $root 'artifacts\publish\win-x64\fixture-cleanup.exe')
    Copy-Item -Path (Join-Path $repo 'docs\*') -Destination (Join-Path $root 'artifacts\publish\win-x64\docs') -Recurse
    foreach ($name in @('SIDETALK-Setup.exe', 'SIDETALK-Setup.exe.sha256', 'SIDETALK-Setup.exe.unsigned.txt')) {
        Set-Content -LiteralPath (Join-Path $root "artifacts\release\$name") -Value "previous $name"
    }
    $root
}

function Invoke-Builder([string]$Root, [string]$Compiler = $script:iscc) {
    $output = @(& (Get-Command pwsh).Source -NoLogo -NoProfile -File (Join-Path $Root 'scripts\build-installer.ps1') -ISCCPath $Compiler 2>&1 | ForEach-Object { [string]$_ })
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
}

function Get-ReleaseSnapshot([string]$Root) {
    (@(Get-ChildItem -LiteralPath (Join-Path $Root 'artifacts\release') -File | Sort-Object Name | ForEach-Object { "$($_.Name)=$(Get-Hash $_.FullName)" }) -join ';')
}

function Assert-BuilderRefused([string]$Root, [int]$Code, [string]$Needle, [string]$What) {
    $before = Get-ReleaseSnapshot $Root
    $run = Invoke-Builder $Root
    Assert-Equal $Code $run.ExitCode "$What exit code`n$($run.Output)"
    Assert-True ($run.Output.Contains($Needle)) "$What message missing '$Needle':`n$($run.Output)"
    Assert-Equal $before (Get-ReleaseSnapshot $Root) "$What changed the previous Setup artifacts"
    Assert-Equal 0 @(Get-ChildItem -LiteralPath (Join-Path $Root 'artifacts') -Directory -Force -Filter '.installer-*').Count "$What left installer staging"
}

$realBefore = $null
try {
    New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
    $realBefore = Get-RealStateSnapshot
    $prepared = @(& (Join-Path $repo 'scripts\prepare-inno.ps1'))
    if ($LASTEXITCODE -ne 0) { throw 'The pinned Inno Setup compiler could not be prepared.' }
    $script:iscc = [string]$prepared[-1]
    $script:recorder = Join-Path $fixtureRoot 'fixture-cleanup.exe'
    New-Recorder $script:recorder
    $identityText = Get-Content -LiteralPath (Join-Path $repo 'src\Sidetalk.App\AppIdentity.cs') -Raw
    $script:appUserModelId = [regex]::Match($identityText, 'AppUserModelId = "([^"]+)"').Groups[1].Value

    $script:appId = 'SIDETALKInstallerTest-' + [Guid]::NewGuid().ToString('N')
    $script:namespace = (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($script:appId))) -Algorithm SHA256).Hash.ToLowerInvariant()
    $script:installDir = Join-Path $fixtureRoot 'LocalAppData\Programs\SIDETALK'
    $script:programs = Join-Path $fixtureRoot 'StartMenu\Programs'
    $script:desktop = Join-Path $fixtureRoot 'Desktop'
    $script:uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$($script:appId)}_is1"
    New-Item -ItemType Directory -Path $script:programs, $script:desktop -Force | Out-Null
    # Stand-ins for %LOCALAPPDATA%\SIDETALK data next to the fixture install.
    $script:sentinel = Join-Path $fixtureRoot 'LocalAppData\SIDETALK\profile.json'
    $script:boxSentinel = Join-Path $fixtureRoot 'LocalAppData\SIDETALK\Boxes\SidetalkFixture\account.dat'
    New-Item -ItemType Directory -Path (Split-Path -Parent $script:boxSentinel) -Force | Out-Null
    Set-Content -LiteralPath $script:sentinel -Value '{"synthetic":"profile"}'
    Set-Content -LiteralPath $script:boxSentinel -Value 'synthetic box data'
    $script:sentinelHash = Get-Hash $script:sentinel
    $script:boxHash = Get-Hash $script:boxSentinel

    Invoke-Case 'installer script guards: production AppId, fixture namespace and required defines' {
        $payload = Join-Path $fixtureRoot 'payload-guards'
        New-Payload $payload 'guards'
        $common = @('AppVersion=0.2.0', 'VersionInfoVersion=0.2.0.0', "SourceDir=$payload")
        $cases = @(
            @{ Name = 'fixture with production AppId'; Defines = $common + @("AppId=$productionAppId", "FixtureNamespaceSHA256=$($script:namespace)", "DefaultInstallDir=$($script:installDir)", "StartMenuDir=$($script:programs)", "DesktopDir=$($script:desktop)"); Needle = 'separate SIDETALKInstallerTest' },
            @{ Name = 'fixture without its own install dir'; Defines = $common + @("AppId=$($script:appId)", "FixtureNamespaceSHA256=$($script:namespace)", "StartMenuDir=$($script:programs)", "DesktopDir=$($script:desktop)"); Needle = 'own DefaultInstallDir' },
            @{ Name = 'production with another AppId'; Defines = $common + @("AppId=$($script:appId)"); Needle = 'Only the isolated installer regression' },
            @{ Name = 'missing version'; Defines = @("SourceDir=$payload"); Needle = 'AppVersion is required' })
        foreach ($case in $cases) {
            $result = Invoke-Iscc $case.Defines (Join-Path $fixtureRoot ('guard-' + [Guid]::NewGuid().ToString('N')))
            Assert-True ($result.ExitCode -ne 0) "$($case.Name) compiled"
            Assert-True ($result.Log.Contains($case.Needle)) "$($case.Name) did not report '$($case.Needle)': $($result.Log)"
            Assert-True (-not (Test-Path -LiteralPath $result.Setup)) "$($case.Name) produced a Setup"
        }
        $iss = Get-Content -LiteralPath (Join-Path $repo 'installer\SIDETALK.iss') -Raw
        Assert-True ($iss -notmatch '(?m)^\s*\[UninstallDelete\]') 'installer deletes user data on uninstall'
        Assert-True ($iss -match '(?m)^PrivilegesRequired=lowest\s*$') 'installer is not per-user'
        Assert-True ($iss -match '(?m)^CloseApplications=no\s*$') 'installer may close applications'
        Assert-True ($iss -match '(?m)^RestartApplications=no\s*$') 'installer may restart applications'
        Assert-True ($iss -match '(?m)^#include "SetupTheme\.iss"\s*$') 'installer does not use the shared setup theme'
        # The presentation layer only draws: no process, file or uninstall policy of its own.
        $theme = Get-Content -LiteralPath (Join-Path $repo 'installer\SetupTheme.iss') -Raw
        foreach ($forbidden in @('(?m)^\s*\[(UninstallDelete|InstallDelete|Run|UninstallRun|Icons|Tasks|Dirs|Registry)\]', '(?m)^\s*(CloseApplications|RestartApplications|PrivilegesRequired|AppId|DefaultDirName|SetupMutex)\s*=',
                '(?i)\b(Exec|ExecAsOriginalUser|ShellExec|TerminateProcess|OpenProcess|DeleteFile|DelTree|RemoveDir|RenameFile|FileCopy|RegWriteStringValue|RegWriteDWordValue|RegDeleteValue|RegDeleteKeyIncludingSubkeys)\s*\(',
                '(?i)taskkill', '(?im)^\s*(function|procedure)\s+(PrepareToInstall|InitializeSetup|InitializeUninstall|CurStepChanged|CurUninstallStepChanged|NextButtonClick)\b')) {
            Assert-True ($theme -notmatch $forbidden) "SetupTheme.iss reaches beyond presentation ($forbidden)"
        }
        $events = @([regex]::Matches($theme, "<event\('([A-Za-z]+)'\)>") | ForEach-Object { $_.Groups[1].Value } | Sort-Object)
        Assert-Equal 'CancelButtonClick|CurInstallProgressChanged|CurPageChanged|DeinitializeSetup|InitializeWizard|ShouldSkipPage' ($events -join '|') 'SetupTheme.iss Inno events'
        $art = @(& (Get-Command pwsh).Source -NoLogo -NoProfile -File (Join-Path $repo 'scripts\generate-setup-art.ps1') -Check 2>&1 | ForEach-Object { [string]$_ })
        Assert-Equal 0 $LASTEXITCODE "installer art is not what scripts\generate-setup-art.ps1 renders:`n$($art -join "`n")"
        Assert-True ($iss -match '#define AppUserModelID "' + [regex]::Escape($script:appUserModelId) + '"') 'installer and app AppUserModelID differ'
        Assert-True ($iss -match '#define ProductionAppId "' + $productionAppId + '"') 'production AppId changed'
    }

    $payload1 = Join-Path $fixtureRoot 'payload-1'
    $payload2 = Join-Path $fixtureRoot 'payload-2'
    New-Payload $payload1 'build 1' @('only-in-1.txt')
    New-Payload $payload2 'build 2'
    $setup1 = Compile-Fixture '0.2.0' $payload1 'v1'
    $setup2 = Compile-Fixture '0.2.1' $payload2 'v2'
    $setupDown = Compile-Fixture '0.1.9' $payload1 'down'

    Invoke-Case 'install refuses a linked install folder without touching its target' {
        $target = Join-Path $fixtureRoot 'junction-target'
        New-Item -ItemType Directory -Path $target, (Split-Path -Parent $script:installDir) -Force | Out-Null
        New-Item -ItemType Junction -Path $script:installDir -Target $target | Out-Null
        try {
            $run = Invoke-Setup $setup1 'junction'
            Assert-Equal $innoBlocked $run.ExitCode 'junction install exit'
            Assert-Equal 0 @(Get-ChildItem -LiteralPath $target -Force).Count 'junction target was written'
            Assert-True (-not (Test-Path -LiteralPath $script:uninstallKey)) 'junction install registered'
        } finally {
            (Get-Item -LiteralPath $script:installDir -Force).Delete()
        }
    }

    Invoke-Case 'clean install: files, Start Menu, desktop option, Installed Apps' {
        $run = Invoke-Setup $setup1 'install-1' @('/TASKS=desktopicon')
        Assert-Equal 0 $run.ExitCode "clean install exit (log $($run.Log))"
        Assert-Installed $payload1 '0.2.0' 'clean install'
        Assert-True (Test-Path -LiteralPath (Join-Path $script:desktop 'SIDETALK.lnk')) 'desktop option did not create the shortcut'
        Assert-Equal (Join-Path $script:installDir 'Sidetalk.exe') (Get-ShortcutInfo (Join-Path $script:desktop 'SIDETALK.lnk')).Target 'desktop shortcut target'
        Assert-DataSentinel 'clean install'
    }

    Invoke-Case 'same-version reinstall and update replace only program files' {
        $run = Invoke-Setup $setup1 'reinstall-1'
        Assert-Equal 0 $run.ExitCode 'same-version reinstall exit'
        Assert-Installed $payload1 '0.2.0' 'same-version reinstall'
        $run = Invoke-Setup $setup2 'update-2'
        Assert-Equal 0 $run.ExitCode 'update exit'
        Assert-Installed $payload2 '0.2.1' 'update'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $script:installDir 'docs\only-in-1.txt'))) 'update left a stale document'
        Assert-True (Test-Path -LiteralPath (Join-Path $script:desktop 'SIDETALK.lnk')) 'update removed the chosen desktop shortcut'
        Assert-DataSentinel 'update'
    }

    Invoke-Case 'downgrade, other locations and a running SIDETALK are refused before copying' {
        $run = Invoke-Setup $setupDown 'downgrade'
        Assert-Equal $innoBlocked $run.ExitCode 'downgrade exit'
        Assert-Installed $payload2 '0.2.1' 'refused downgrade'
        $elsewhere = Join-Path $fixtureRoot 'elsewhere'
        $run = Invoke-Setup $setup2 'other-dir' @("/DIR=`"$elsewhere`"")
        Assert-Equal $innoBlocked $run.ExitCode '/DIR override exit'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $elsewhere 'Sidetalk.exe'))) '/DIR override installed files'
        # A running image cannot be opened for writing; hold the same kind of lock.
        $held = [IO.File]::Open((Join-Path $script:installDir 'Sidetalk.exe'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $run = Invoke-Setup $setup2 'running'
            Assert-Equal $innoBlocked $run.ExitCode 'running install exit'
            Assert-True ((Get-Content -LiteralPath $run.Log -Raw) -match 'SIDETALK install blocked') 'running install reason not logged'
            $run = Invoke-Uninstall 'running-uninstall'
            Assert-True ($run.ExitCode -ne 0) 'uninstall proceeded while SIDETALK was running'
            Assert-True (Test-Path -LiteralPath $script:uninstallKey) 'blocked uninstall removed the registration'
        } finally { $held.Dispose() }
        Assert-Installed $payload2 '0.2.1' 'refused while running'
        Assert-DataSentinel 'refusals'
    }

    Invoke-Case 'uninstall runs owned-shortcut cleanup first, removes program files and keeps data' {
        $cleanupLog = Join-Path $fixtureRoot 'cleanup.log'
        $env:SIDETALK_FIXTURE_CLEANUP_LOG = $cleanupLog
        $env:SIDETALK_FIXTURE_CLEANUP_EXIT = '0'
        try {
            $run = Invoke-Uninstall 'uninstall'
            Assert-Equal 0 $run.ExitCode 'uninstall exit'
            Assert-True (-not (Test-Path -LiteralPath $script:uninstallKey)) 'uninstall left the registration'
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $script:installDir 'Sidetalk.exe'))) 'uninstall left Sidetalk.exe'
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $script:programs 'SIDETALK.lnk'))) 'uninstall left the Start Menu shortcut'
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $script:desktop 'SIDETALK.lnk'))) 'uninstall left the desktop shortcut'
            $calls = @(Get-Content -LiteralPath $cleanupLog)
            Assert-Equal 1 $calls.Count 'cleanup call count'
            $parts = $calls[0].Split('|')
            Assert-Equal (Join-Path $script:installDir 'fixture-cleanup.exe') $parts[0] 'cleanup ran from the installation'
            Assert-Equal '--uninstall-owned-integration' $parts[1] 'cleanup arguments'
            Assert-Equal 'True' $parts[2] 'cleanup must run before program files are removed'
            Assert-DataSentinel 'uninstall'

            # Cleanup failures never block or widen uninstall.
            foreach ($code in @('1', '3')) {
                $env:SIDETALK_FIXTURE_CLEANUP_EXIT = $code
                Assert-Equal 0 (Invoke-Setup $setup2 "reinstall-$code").ExitCode "reinstall before cleanup exit $code"
                $run = Invoke-Uninstall "uninstall-$code"
                Assert-Equal 0 $run.ExitCode "uninstall with cleanup exit $code"
                Assert-True (-not (Test-Path -LiteralPath $script:uninstallKey)) "uninstall with cleanup exit $code left the registration"
                Assert-True ((Get-Content -LiteralPath $run.Log -Raw) -match "shortcut cleanup exit code: $code") "cleanup exit $code not logged"
            }
            Assert-DataSentinel 'uninstall with cleanup failures'
        } finally {
            Remove-Item Env:SIDETALK_FIXTURE_CLEANUP_LOG, Env:SIDETALK_FIXTURE_CLEANUP_EXIT -ErrorAction SilentlyContinue
        }
    }

    Invoke-Case 'build-installer stages, hashes and replaces SIDETALK-Setup.exe' {
        $root = New-BuilderRepo 'success'
        $run = Invoke-Builder $root
        Assert-Equal 0 $run.ExitCode "build-installer success`n$($run.Output)"
        $setup = Join-Path $root 'artifacts\release\SIDETALK-Setup.exe'
        $line = (Get-Content -LiteralPath ($setup + '.sha256') -Raw).Trim()
        Assert-Equal ((Get-Hash $setup).ToLowerInvariant() + '  SIDETALK-Setup.exe') $line 'SHA-256 file'
        Assert-True ((Get-Content -LiteralPath ($setup + '.unsigned.txt') -Raw) -match 'unsigned') 'unsigned marker'
        Assert-Equal 'NotSigned' ([string](Get-AuthenticodeSignature -LiteralPath $setup).Status) 'unsigned Setup signature status'
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($setup)
        [xml]$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
        $version = [string]($props.Project.PropertyGroup | ForEach-Object { $_.SidetalkVersion } | Where-Object { $_ } | Select-Object -First 1)
        Assert-Equal $version ([string]$info.ProductVersion).Trim() 'Setup ProductVersion'
        Assert-Equal 0 @(Get-ChildItem -LiteralPath (Join-Path $root 'artifacts') -Directory -Force -Filter '.installer-*').Count 'installer staging left'
    }

    Invoke-Case 'build-installer failures keep the previous Setup artifacts' {
        $root = New-BuilderRepo 'missing'
        Remove-Item -LiteralPath (Join-Path $root 'artifacts\publish\win-x64\tools\IsolationProbe\Sidetalk.IsolationProbe.exe')
        Assert-BuilderRefused $root 50 'missing tools\IsolationProbe\Sidetalk.IsolationProbe.exe' 'missing source file'

        $root = New-BuilderRepo 'version'
        $propsPath = Join-Path $root 'Directory.Build.props'
        (Get-Content -LiteralPath $propsPath -Raw) -replace '<SidetalkVersion>[^<]+</SidetalkVersion>', '<SidetalkVersion>9.9.9</SidetalkVersion>' | Set-Content -LiteralPath $propsPath -NoNewline
        Assert-BuilderRefused $root 50 'Directory.Build.props is 9.9.9' 'stale version'

        $root = New-BuilderRepo 'foreign'
        Set-Content -LiteralPath (Join-Path $root 'artifacts\publish\win-x64\KakaoTalk.exe') -Value 'not redistributable'
        Assert-BuilderRefused $root 50 'must not redistribute' 'KakaoTalk file in source'

        $root = New-BuilderRepo 'junction'
        $publish = Join-Path $root 'artifacts\publish\win-x64'
        $real = Join-Path $root 'real-publish'
        Move-Item -LiteralPath $publish -Destination $real
        New-Item -ItemType Junction -Path $publish -Target $real | Out-Null
        try { Assert-BuilderRefused $root 50 'linked' 'linked source' }
        finally { (Get-Item -LiteralPath $publish -Force).Delete() }

        $root = New-BuilderRepo 'compiler'
        $copy = Join-Path $root 'not-iscc.exe'
        Copy-Item -LiteralPath (Join-Path $env:WINDIR 'System32\cmd.exe') -Destination $copy
        $before = Get-ReleaseSnapshot $root
        $run = Invoke-Builder $root $copy
        Assert-Equal 50 $run.ExitCode 'unpinned compiler exit'
        Assert-True $run.Output.Contains('not the pinned Inno Setup') 'unpinned compiler message'
        Assert-Equal $before (Get-ReleaseSnapshot $root) 'unpinned compiler changed artifacts'

        $root = New-BuilderRepo 'iscc-failure'
        Add-Content -LiteralPath (Join-Path $root 'installer\SIDETALK.iss') -Value "`r`n[Setup]`r`nNotARealDirective=1"
        Assert-BuilderRefused $root 51 'ISCC.exe failed' 'compiler failure'
    }
} finally {
    $realAfter = if ($realBefore) { Get-RealStateSnapshot } else { $null }
    if ($realBefore -and $realAfter -ne $realBefore) {
        $failures.Add("The real SIDETALK installation, shortcuts or user data changed:`n$realBefore`n$realAfter")
    }
    # Never leave a fixture registration behind, even after a failed case.
    if ($script:uninstallKey -and (Test-Path -LiteralPath $script:uninstallKey)) {
        $uninstaller = Join-Path $script:installDir 'unins000.exe'
        if (Test-Path -LiteralPath $uninstaller) { Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait | Out-Null; Start-Sleep -Seconds 3 }
        if (Test-Path -LiteralPath $script:uninstallKey) { Remove-Item -LiteralPath $script:uninstallKey -Recurse -Force }
    }
    if ($failures.Count -or $KeepFixtureOnFailure) {
        Write-Host "Installer fixture preserved at $fixtureRoot"
    } elseif (Test-Path -LiteralPath $fixtureRoot) {
        if (Get-ChildItem -LiteralPath $fixtureRoot -Recurse -Force -Attributes ReparsePoint) { Write-Warning "Fixture contains a link; preserved at $fixtureRoot" }
        else { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
    }
}
if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
Write-Host ("All installer checks passed ({0:F2}s)." -f $timer.Elapsed.TotalSeconds)
