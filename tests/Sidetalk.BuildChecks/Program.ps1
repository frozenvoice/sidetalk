[CmdletBinding()]
param(
    [switch]$KeepFixtureOnFailure
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('sidetalk-build-checks-' + [Guid]::NewGuid().ToString('N'))
$failures = [System.Collections.Generic.List[string]]::new()
$script:fixtureNumber = 0
$script:hadFailure = $false
$timer = [Diagnostics.Stopwatch]::StartNew()

# Resolve and check every recursive operation before touching fixture files.
function Assert-FixturePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $base = [IO.Path]::GetFullPath($fixtureRoot)
    if ($full -ne $base -and -not $full.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fixture operation escaped its temporary root.'
    }
    for ($ancestor = $full; $ancestor; $ancestor = Split-Path -Parent $ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Fixture operation encountered a linked ancestor.'
        }
    }
}

function Remove-FixtureTree([string]$Path) {
    Assert-FixturePath $Path
    if (Get-ChildItem -LiteralPath $Path -Recurse -Force -Attributes ReparsePoint) {
        throw 'Refusing recursive cleanup of a fixture containing links.'
    }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) {
        throw "$Message (expected '$Expected', got '$Actual')"
    }
}

function Assert-Contains([string]$Text, [string]$Needle, [string]$Message) {
    if (-not $Text.Contains($Needle, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Message (missing '$Needle')"
    }
}

function Get-FileSnapshot([string]$Root) {
    Assert-FixturePath $Root
    $snapshot = [ordered]@{}
    foreach ($file in @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($Root.Length).TrimStart('\', '/')
        $snapshot[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return $snapshot
}

function Assert-SnapshotEqual($Before, $After, [string]$Message) {
    $beforeJson = $Before | ConvertTo-Json -Compress
    $afterJson = $After | ConvertTo-Json -Compress
    if ($beforeJson -ne $afterJson) { throw "$Message`nBefore: $beforeJson`nAfter:  $afterJson" }
}

function Write-ChildRunner([string]$Root) {
    $runner = @'
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Entry,
    [switch]$UiChecks,

    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$root = $env:SIDETALK_BUILD_CHECK_ROOT
$logPath = Join-Path $root 'harness.log'
$failStage = $env:SIDETALK_BUILD_CHECK_FAIL

function Write-HarnessLog([string]$Kind, [hashtable]$Data = @{}) {
    $row = [ordered]@{ Kind = $Kind }
    foreach ($key in $Data.Keys) { $row[$key] = $Data[$key] }
    [pscustomobject]$row | ConvertTo-Json -Compress | Add-Content -LiteralPath $logPath -Encoding utf8
}

function Get-TokenText([object[]]$Tokens) {
    return @($Tokens | ForEach-Object { [string]$_ })
}

function dotnet {
    param([Parameter(ValueFromRemainingArguments = $true)][object[]]$Arguments)
    $tokens = Get-TokenText $Arguments
    $verb = if ($tokens.Count) { $tokens[0] } else { '' }
    $joined = $tokens -join ([char]31)
    Write-HarnessLog 'dotnet' @{ Arguments = $joined }
    $isApp = ($tokens -contains 'src/Sidetalk.App/Sidetalk.App.csproj')
    $isProbe = ($tokens -contains 'src/Sidetalk.IsolationProbe/Sidetalk.IsolationProbe.csproj')
    $isTests = ($tokens -contains 'tests/Sidetalk.Tests/Sidetalk.Tests.csproj')
    $isUi = ($tokens -contains 'tests/Sidetalk.UiChecks/Sidetalk.UiChecks.csproj')

    if (($verb -eq 'build' -and $isApp -and $failStage -eq 'app-build') -or
        ($verb -eq 'run' -and $isTests -and $failStage -eq 'tests') -or
        ($verb -eq 'build' -and $isProbe -and $failStage -eq 'probe-build') -or
        ($verb -eq 'run' -and $isUi -and $failStage -eq 'ui')) {
        [Console]::Error.WriteLine('synthetic compiler/test failure; output diagnostics retained')
        & $env:ComSpec /d /c 'exit 37'
        $global:LASTEXITCODE = $LASTEXITCODE
        return
    }

    if ($verb -eq 'publish') {
        $isProbePublish = $isProbe
        if (($isApp -and $failStage -eq 'publish-app') -or ($isProbePublish -and $failStage -eq 'publish-probe')) {
            $global:LASTEXITCODE = 1
            return
        }
        $outputIndex = [Array]::IndexOf($tokens, '-o')
        if ($outputIndex -lt 0 -or $outputIndex + 1 -ge $tokens.Count) { throw 'mock publish did not receive -o' }
        $publishOutput = [IO.Path]::GetFullPath($tokens[$outputIndex + 1])
        Microsoft.PowerShell.Management\New-Item -ItemType Directory -Path $publishOutput -Force | Out-Null
        $versionSource = Join-Path $root '.fixture-version.dll'
        if ($isApp) {
            Microsoft.PowerShell.Management\Copy-Item -LiteralPath $versionSource -Destination (Join-Path $publishOutput 'Sidetalk.dll') -Force
            Microsoft.PowerShell.Management\Copy-Item -LiteralPath $versionSource -Destination (Join-Path $publishOutput 'Sidetalk.exe') -Force
        } elseif ($isProbePublish) {
            Microsoft.PowerShell.Management\Copy-Item -LiteralPath $versionSource -Destination (Join-Path $publishOutput 'Sidetalk.IsolationProbe.dll') -Force
            Microsoft.PowerShell.Management\Copy-Item -LiteralPath $versionSource -Destination (Join-Path $publishOutput 'Sidetalk.IsolationProbe.exe') -Force
        }
    }
    $global:LASTEXITCODE = 0
}

function Get-Process {
    [CmdletBinding()]
    param([string[]]$Name)
    Write-HarnessLog 'get-process' @{ Names = ($Name -join ',') }
    if ($env:SIDETALK_BUILD_CHECK_RUNNING -eq '1') {
        [pscustomobject]@{ Path = (Join-Path $root 'artifacts\publish\win-x64\Sidetalk.exe') }
    }
}

function Move-Item {
    [CmdletBinding()]
    param([string]$LiteralPath, [string]$Destination, [switch]$Force)
    Write-HarnessLog 'move' @{ Source = $LiteralPath; Destination = $Destination }
    if ($env:SIDETALK_BUILD_CHECK_FAIL -in @('promotion', 'rollback') -and
        ([IO.Path]::GetFileName($LiteralPath) -eq 'release.zip') -and
        ([IO.Path]::GetFileName($Destination) -like 'SIDETALK-*.zip')) {
        throw 'synthetic promotion move failure'
    }
    if ($env:SIDETALK_BUILD_CHECK_FAIL -eq 'rollback' -and
        ([IO.Path]::GetFileName($LiteralPath) -eq 'win-x64') -and
        ([IO.Path]::GetFileName($Destination) -eq 'win-x64') -and
        ($Destination -match '\\.publish-[^\\]+\\win-x64$')) {
        throw 'synthetic rollback move failure'
    }
    Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
}

function Copy-Item {
    [CmdletBinding()]
    param([string]$LiteralPath, [string]$Destination, [switch]$Recurse, [switch]$Force)
    if ($env:SIDETALK_BUILD_CHECK_FAIL -eq 'docs' -and
        (($LiteralPath -like '*\README.md') -or ($LiteralPath -like '*\docs'))) {
        throw 'synthetic documentation copy failure'
    }
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Recurse:$Recurse -Force:$Force
}

function Compress-Archive {
    [CmdletBinding()]
    param([string[]]$Path, [string]$DestinationPath)
    if ($env:SIDETALK_BUILD_CHECK_FAIL -eq 'archive') { throw 'synthetic archive failure' }
    Microsoft.PowerShell.Archive\Compress-Archive -Path $Path -DestinationPath $DestinationPath
    Write-HarnessLog 'archive' @{ Destination = $DestinationPath }
}

$entryPath = Join-Path $root ('scripts\' + $Entry)
$invokeArgs = @{}
if ($Entry -eq 'verify.ps1') {
    $invokeArgs = @{ UiChecks = $UiChecks }
} elseif ($Entry -eq 'build.ps1') {
    $invokeArgs = @{ SelfContained = $SelfContained }
}
# Capture the returned hash object without console-table width truncation.
& $entryPath @invokeArgs | ForEach-Object {
    if ($_ -is [pscustomobject] -and $_.Algorithm -eq 'SHA256') { $_ | ConvertTo-Json -Compress }
    else { $_ }
}
exit $LASTEXITCODE
'@
    Set-Content -LiteralPath (Join-Path $Root '.harness-child.ps1') -Value $runner -Encoding utf8
}

function New-Fixture {
    $script:fixtureNumber++
    $root = Join-Path $fixtureRoot ('case ' + $script:fixtureNumber.ToString('00') + ' fixture')
    Assert-FixturePath $root
    New-Item -ItemType Directory -Path (Join-Path $root 'artifacts\publish\win-x64') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'docs') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'scripts') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'profile') -Force | Out-Null
    foreach ($name in @('build.ps1', 'build-common.ps1', 'verify.ps1', 'ensure-dotnet.ps1', 'install-local.ps1', 'build-installer.ps1', 'prepare-inno.ps1')) {
        Copy-Item -LiteralPath (Join-Path $repo ('scripts\' + $name)) -Destination (Join-Path $root ('scripts\' + $name))
    }
    Copy-Item -LiteralPath (Join-Path $repo 'build-local.cmd') -Destination (Join-Path $root 'build-local.cmd')
    Copy-Item -LiteralPath (Join-Path $repo 'run-local.cmd') -Destination (Join-Path $root 'run-local.cmd')
    # The CMD entry point must resolve the repository's SDK, not the host default.
    Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination (Join-Path $root 'global.json')
    Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination (Join-Path $root 'README.md')
    Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $root -Recurse -Force
    Set-Content -LiteralPath (Join-Path $root 'docs\fixture.md') -Value 'synthetic documentation' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $root 'profile\synthetic-profile.json') -Value '{"fixture":true}' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $root 'artifacts\publish\win-x64\Sidetalk.exe') -Value 'old deployed app' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $root 'artifacts\publish\win-x64\old.marker') -Value 'old output' -Encoding utf8

    # Read the copied image's version; System32 may resolve different MUI version metadata.
    $versionSource = Join-Path $root '.fixture-version.dll'
    Copy-Item -LiteralPath (Join-Path $env:WINDIR 'System32/cmd.exe') -Destination $versionSource
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($versionSource).ProductVersion.Split('+')[0]
    foreach ($flavor in @('desktop-runtime', 'self-contained')) {
        Set-Content -LiteralPath (Join-Path $root "artifacts\SIDETALK-$version-win-x64-$flavor.zip") -Value "old $flavor archive" -Encoding utf8
        Set-Content -LiteralPath (Join-Path $root "artifacts\SIDETALK-0.1.6-win-x64-$flavor.zip") -Value "old 0.1.6 $flavor archive" -Encoding utf8
    }
    Set-Content -LiteralPath (Join-Path $root 'artifacts\.publish.lock') -Value 'synthetic lock' -Encoding utf8
    Write-ChildRunner $root
    return $root
}

function Invoke-Fixture([string]$Root, [string]$Entry, [string]$Failure = '', [switch]$Running, [switch]$UiChecks, [switch]$SelfContained) {
    Remove-Item -LiteralPath (Join-Path $Root 'harness.log') -Force -ErrorAction SilentlyContinue
    $oldRoot = $env:SIDETALK_BUILD_CHECK_ROOT
    $oldFailure = $env:SIDETALK_BUILD_CHECK_FAIL
    $oldRunning = $env:SIDETALK_BUILD_CHECK_RUNNING
    try {
        $env:SIDETALK_BUILD_CHECK_ROOT = $Root
        $env:SIDETALK_BUILD_CHECK_FAIL = $Failure
        $env:SIDETALK_BUILD_CHECK_RUNNING = if ($Running) { '1' } else { '0' }
        $pwsh = (Get-Command pwsh -ErrorAction Stop).Source
        $args = @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'RemoteSigned', '-File', (Join-Path $Root '.harness-child.ps1'), '-Entry', $Entry)
        if ($UiChecks) { $args += '-UiChecks' }
        if ($SelfContained) { $args += '-SelfContained' }
        $output = @(& $pwsh @args 2>&1 | ForEach-Object { [string]$_ })
        $code = $LASTEXITCODE
        $log = @()
        $logFile = Join-Path $Root 'harness.log'
        if (Test-Path -LiteralPath $logFile) { $log = @(Get-Content -LiteralPath $logFile | ForEach-Object { $_ | ConvertFrom-Json }) }
        return [pscustomobject]@{ ExitCode = $code; Output = ($output -join "`n"); Log = $log }
    } finally {
        $env:SIDETALK_BUILD_CHECK_ROOT = $oldRoot
        $env:SIDETALK_BUILD_CHECK_FAIL = $oldFailure
        $env:SIDETALK_BUILD_CHECK_RUNNING = $oldRunning
    }
}

function Invoke-Case([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        Write-Host "PASS $Name"
    } catch {
        $script:hadFailure = $true
        $failures.Add("$Name`: $($_.Exception.Message)")
        Write-Warning "FAIL $Name`: $($_.Exception.Message)"
    }
}

function Assert-VerificationCalls($Log, [switch]$Ui) {
    $dotnet = @($Log | Where-Object Kind -eq 'dotnet')
    $buildApp = @($dotnet | Where-Object { $_.Arguments -match '(^|\x1F)build(\x1F|$)' -and $_.Arguments -match 'Sidetalk\.App\.csproj' })
    $tests = @($dotnet | Where-Object { $_.Arguments -match '(^|\x1F)run(\x1F|$)' -and $_.Arguments -match 'Sidetalk\.Tests\.csproj' })
    $buildProbe = @($dotnet | Where-Object { $_.Arguments -match '(^|\x1F)build(\x1F|$)' -and $_.Arguments -match 'IsolationProbe\.csproj' })
    Assert-Equal 1 $buildApp.Count 'app build count'
    Assert-Equal 1 $tests.Count 'safety test count'
    Assert-Equal 1 $buildProbe.Count 'probe build count'
    Assert-True ($dotnet[0].Arguments -match '^build\x1Fsrc/Sidetalk.App/') 'app must build first'
    Assert-True ($dotnet[1].Arguments -match '^run\x1F--project\x1Ftests/Sidetalk.Tests/') 'console runner must run second'
    Assert-True ($dotnet[2].Arguments -match '^build\x1Fsrc/Sidetalk.IsolationProbe/') 'probe must build third'
    foreach ($row in $dotnet) {
        Assert-True ($row.Arguments -match '\x1F-c\x1FRelease(\x1F|$)') 'all checks must use Release'
        Assert-True ($row.Arguments -notmatch '--no-build|--no-restore|^test\x1F') 'checks must build/restore and use the console runner'
    }
    if ($Ui) {
        Assert-Equal 1 @($dotnet | Where-Object { $_.Arguments -match 'UiChecks\.csproj' }).Count 'UI check count'
    } else {
        Assert-Equal 0 @($dotnet | Where-Object { $_.Arguments -match 'UiChecks\.csproj' }).Count 'unexpected UI check count'
    }
}

function Assert-PublishArguments($Log, [bool]$SelfContained) {
    $publish = @($Log | Where-Object { $_.Kind -eq 'dotnet' -and $_.Arguments -match '(^|\x1F)publish(\x1F|$)' })
    Assert-Equal 2 $publish.Count 'publish count'
    $expected = if ($SelfContained) { 'true' } else { 'false' }
    foreach ($row in $publish) {
        $args = $row.Arguments -split ([char]31)
        Assert-True (-not (($args -contains '--no-build') -or ($args -contains '--no-restore'))) 'RID publish must restore and build'
        Assert-Equal 'Release' $args[[Array]::IndexOf($args, '-c') + 1] 'publish configuration'
        Assert-True (($args -contains '-r') -and ($args[[Array]::IndexOf($args, '-r') + 1] -eq 'win-x64')) 'publish RID must be win-x64'
        Assert-True (($args -contains '--self-contained') -and ($args[[Array]::IndexOf($args, '--self-contained') + 1] -eq $expected)) "publish self-contained must be $expected"
    }
}

function Test-VerifyLeavesDeployment {
    $fixture = New-Fixture
    $artifactBefore = Get-FileSnapshot (Join-Path $fixture 'artifacts')
    $profileBefore = Get-FileSnapshot (Join-Path $fixture 'profile')
    $lock = [IO.File]::Open((Join-Path $fixture 'artifacts/.publish.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { $result = Invoke-Fixture $fixture 'verify.ps1' -Running } finally { $lock.Dispose() }
    Assert-Equal 0 @($result.Log | Where-Object Kind -eq 'get-process').Count 'verify inspected deployed processes'
    Assert-Equal 3 @($result.Log | Where-Object Kind -eq 'dotnet').Count 'verify must run only three current-source checks'
    Assert-Equal 0 $result.ExitCode 'verify exit code'
    Assert-VerificationCalls $result.Log
    Assert-Equal 0 @($result.Log | Where-Object { $_.Kind -in @('move', 'archive') }).Count 'verify must not promote or archive'
    Assert-SnapshotEqual $artifactBefore (Get-FileSnapshot (Join-Path $fixture 'artifacts')) 'verify changed deployment artifacts'
    Assert-SnapshotEqual $profileBefore (Get-FileSnapshot (Join-Path $fixture 'profile')) 'verify changed synthetic profile'
}

function Test-VerifyFailureCodes {
    $fixture = New-Fixture
    $result = Invoke-Fixture $fixture 'verify.ps1' -Failure tests
    Assert-Equal 11 $result.ExitCode 'safety test failure exit code'
    Assert-Contains $result.Output 'exited with code 37' 'native failure code was lost'
    Assert-Contains $result.Output 'output diagnostics retained' 'native error output was lost'
    Assert-True (-not (@($result.Log | Where-Object { $_.Kind -eq 'dotnet' -and $_.Arguments -match 'IsolationProbe\.csproj' }).Count)) 'verification continued after failed tests'

    $result = Invoke-Fixture $fixture 'verify.ps1' -Failure ui -UiChecks
    Assert-Equal 13 $result.ExitCode 'UI fixture failure exit code'
    $paths = @()
    foreach ($iteration in 1..2) {
        $result = Invoke-Fixture $fixture 'verify.ps1' -UiChecks
        Assert-Equal 0 $result.ExitCode 'optional UI success exit code'
        Assert-VerificationCalls $result.Log -Ui
        $uiCall = @($result.Log | Where-Object { $_.Kind -eq 'dotnet' -and $_.Arguments -match 'UiChecks\.csproj' })[0]
        $uiArgs = $uiCall.Arguments -split ([char]31)
        $uiPath = $uiArgs[[Array]::IndexOf($uiArgs, '--') + 1]
        Assert-True ($uiPath.StartsWith((Join-Path ([IO.Path]::GetTempPath()) 'sidetalk-ui-checks-'))) 'UI path must use an isolated TEMP directory'
        Assert-True (-not (Test-Path -LiteralPath $uiPath)) 'mock UI must receive a new path'
        $paths += $uiPath
    }
    Assert-True ($paths[0] -ne $paths[1]) 'UI calls reused a directory'
}

function Test-PackagingSuccessAndArguments {
    foreach ($selfContained in @($false, $true)) {
        $fixture = New-Fixture
        $before = Get-FileSnapshot (Join-Path $fixture 'artifacts\publish')
        $result = Invoke-Fixture $fixture 'build.ps1' -SelfContained:$selfContained
        Assert-Equal 0 $result.ExitCode "packaging exit code ($selfContained)"
        Assert-VerificationCalls $result.Log
        Assert-PublishArguments $result.Log $selfContained
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $fixture '.fixture-version.dll')).ProductVersion.Split('+')[0]
        $flavor = if ($selfContained) { 'self-contained' } else { 'desktop-runtime' }
        $zipPath = Join-Path $fixture "artifacts/SIDETALK-$version-win-x64-$flavor.zip"
        $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
            Assert-True ($entries -contains 'README.md') 'release archive omitted README.md'
            Assert-True ($entries -contains 'docs/fixture.md') 'release archive omitted docs'
            Assert-True ($entries -contains 'tools/IsolationProbe/Sidetalk.IsolationProbe.exe') 'release archive omitted probe'
        } finally { $archive.Dispose() }
        Assert-Contains $result.Output (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash 'printed archive hash did not match ZIP'
        Assert-True (-not (Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts') -Directory -Filter '.publish-*')) 'successful packaging left staging'
        Assert-True ((Get-Content -LiteralPath (Join-Path $fixture 'artifacts\publish\win-x64\Sidetalk.exe') -Raw).Length -gt 0) 'new app output missing'
    }
}

function Test-PackagingFailuresPreserveOutput {
    foreach ($failure in @('app-build', 'tests', 'probe-build', 'publish-app', 'publish-probe', 'archive', 'docs')) {
        $fixture = New-Fixture
        $before = Get-FileSnapshot (Join-Path $fixture 'artifacts')
        $result = Invoke-Fixture $fixture 'build.ps1' -Failure $failure
        $expected = switch ($failure) {
            'app-build' { 10 }
            'tests' { 11 }
            'probe-build' { 12 }
            'publish-app' { 21 }
            'publish-probe' { 22 }
            'docs' { 23 }
            'archive' { 30 }
        }
        Assert-Equal $expected $result.ExitCode "$failure exit code"
        Assert-SnapshotEqual $before (Get-FileSnapshot (Join-Path $fixture 'artifacts')) "$failure changed existing artifacts"
    }
}

function Test-PreflightGuards {
    $fixture = New-Fixture
    $before = Get-FileSnapshot (Join-Path $fixture 'artifacts')
    $result = Invoke-Fixture $fixture 'build.ps1' -Running
    Assert-Equal 20 $result.ExitCode 'running output guard exit code'
    Assert-SnapshotEqual $before (Get-FileSnapshot (Join-Path $fixture 'artifacts')) 'running guard changed artifacts'

    $lockFixture = New-Fixture
    $lockPath = Join-Path $lockFixture 'artifacts\.publish.lock'
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $result = Invoke-Fixture $lockFixture 'build.ps1'
        Assert-Equal 20 $result.ExitCode 'publish lock exit code'
    } finally { $lock.Dispose() }

    $junctionFixture = New-Fixture
    $outside = Join-Path $fixtureRoot 'outside-target'
    New-Item -ItemType Directory -Path $outside -Force | Out-Null
    Remove-FixtureTree (Join-Path $junctionFixture 'artifacts\publish')
    New-Item -ItemType Junction -Path (Join-Path $junctionFixture 'artifacts\publish') -Target $outside | Out-Null
    $result = Invoke-Fixture $junctionFixture 'build.ps1'
    try {
        Assert-Equal 20 $result.ExitCode 'junction path guard exit code'
        Assert-Equal 0 @(Get-ChildItem -LiteralPath $outside -Force).Count 'linked target was modified'
    } finally {
        # Delete only the known junction itself, without recursive traversal.
        $link = Get-Item -LiteralPath (Join-Path $junctionFixture 'artifacts/publish') -Force
        Assert-True (($link.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) 'expected a fixture junction'
        $link.Delete()
    }
}

function Test-PromotionRollback {
    $fixture = New-Fixture
    $before = Get-FileSnapshot (Join-Path $fixture 'artifacts')
    $result = Invoke-Fixture $fixture 'build.ps1' -Failure promotion
    Assert-Equal 40 $result.ExitCode 'promotion failure exit code'
    Assert-SnapshotEqual $before (Get-FileSnapshot (Join-Path $fixture 'artifacts')) 'successful rollback did not restore artifacts'
    Assert-True (-not (Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts') -Directory -Filter '.publish-*')) 'successful rollback left staging'

    $fixture = New-Fixture
    $result = Invoke-Fixture $fixture 'build.ps1' -Failure rollback
    Assert-Equal 41 $result.ExitCode 'rollback failure exit code'
    Assert-Contains $result.Output 'Preserved files:' 'rollback failure omitted preserved staging path'
    Assert-Contains $result.Output 'Replacement:' 'rollback failure omitted original replacement error'
    Assert-Contains $result.Output 'Rollback:' 'rollback failure omitted rollback error'
    Assert-Contains $result.Output 'synthetic promotion move failure' 'original replacement reason was lost'
    Assert-Contains $result.Output 'synthetic rollback move failure' 'rollback reason was lost'
    $staging = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts') -Directory -Filter '.publish-*')[0].FullName
    Assert-Equal 'old output' (Get-Content -LiteralPath (Join-Path $staging 'previous-win-x64/old.marker')).Trim() 'previous deployment was not preserved'
    Assert-True (Test-Path -LiteralPath (Join-Path $staging 'previous.zip')) 'previous archive was not preserved'
    Assert-True ((Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts') -Directory -Filter '.publish-*').Count -gt 0) 'rollback failure did not preserve staging'
}

function Test-EmptyArtifacts {
    $fixture = New-Fixture
    Remove-FixtureTree (Join-Path $fixture 'artifacts')
    $result = Invoke-Fixture $fixture 'verify.ps1'
    Assert-Equal 0 $result.ExitCode 'verify without existing artifacts'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $fixture 'artifacts'))) 'verify unexpectedly created packaging artifacts'
    $result = Invoke-Fixture $fixture 'build.ps1' -SelfContained
    Assert-Equal 0 $result.ExitCode 'packaging without existing artifacts'
    Assert-VerificationCalls $result.Log
    Assert-PublishArguments $result.Log $true
}

function Test-ArtifactBoundaries {
    $fixture = New-Fixture
    $artifacts = Join-Path $fixture 'artifacts'
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $fixture 'scripts/build.ps1'), [ref]$null, [ref]$null)
    foreach ($name in @('Assert-ArtifactPath', 'Assert-ArtifactTree')) {
        $functionAst = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $false)
        . ([scriptblock]::Create($functionAst.Extent.Text))
    }
    $blocked = $false
    try { Assert-ArtifactPath (Join-Path $artifacts '../outside') } catch { $blocked = $_.Exception.Message.Contains('outside the artifacts directory') }
    Assert-True $blocked 'artifact path escape was allowed'
    $target = Join-Path $fixture 'linked-target'
    New-Item -ItemType Directory -Path $target | Out-Null
    $linkPath = Join-Path $artifacts 'publish/win-x64/nested-link'
    New-Item -ItemType Junction -Path $linkPath -Target $target | Out-Null
    try {
        $blocked = $false
        try { Assert-ArtifactTree (Join-Path $artifacts 'publish/win-x64') } catch { $blocked = $_.Exception.Message.Contains('Linked artifact files') }
        Assert-True $blocked 'nested artifact reparse point was allowed'
        Assert-Equal 0 @(Get-ChildItem -LiteralPath $target -Force).Count 'linked target was modified'
    } finally { (Get-Item -LiteralPath $linkPath -Force).Delete() }
}
# Stands in for scripts\install-local.ps1: records the forwarded switches and
# returns SIDETALK_BUILD_CHECK_INSTALL_EXIT, never touching Windows installation.
function Set-InstallStub([string]$Fixture) {
    Set-Content -LiteralPath (Join-Path $Fixture 'scripts\install-local.ps1') -Encoding utf8 -Value @'
param([switch]$NoInstall, [switch]$NoLaunch)
Add-Content -LiteralPath $env:SIDETALK_BUILD_CHECK_INSTALL_RECORD -Value ('{0},{1}' -f [bool]$NoInstall, [bool]$NoLaunch)
exit ([int]$env:SIDETALK_BUILD_CHECK_INSTALL_EXIT)
'@
}

function Invoke-BuildLocal([string]$Fixture, [string]$Options = '-NoPause') {
    $output = @(& cmd.exe /d /c ('"{0}" {1}' -f (Join-Path $Fixture 'build-local.cmd'), $Options) 2>&1 | ForEach-Object { [string]$_ })
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join [Environment]::NewLine) }
}

function Test-BuildLocalExitCodes {
    $fixture = New-Fixture
    $fakeTools = Join-Path $fixture 'fake-dotnet'
    # A real executable: a dotnet.cmd stand-in would never return to build-local.cmd.
    Set-FakeDotnet $fakeTools '10.0.401'
    $fakeDotnet = Join-Path $fakeTools 'dotnet.exe'

    $ensureStub = @'
param([Parameter(Mandatory=$true)][string]$ResultPath)
[IO.File]::WriteAllText($ResultPath, $env:SIDETALK_BUILD_CHECK_DOTNET, [Text.UTF8Encoding]::new($false))
exit 0
'@
    Set-Content -LiteralPath (Join-Path $fixture 'scripts\ensure-dotnet.ps1') -Value $ensureStub -Encoding utf8

    Set-InstallStub $fixture
    $installRecord = Join-Path $fixture 'install-calls.txt'
    $oldCi = $env:CI
    $oldDotnet = $env:SIDETALK_BUILD_CHECK_DOTNET
    $oldRecord = $env:SIDETALK_BUILD_CHECK_INSTALL_RECORD
    $oldInstallExit = $env:SIDETALK_BUILD_CHECK_INSTALL_EXIT
    try {
        $env:CI = '1'
        $env:SIDETALK_BUILD_CHECK_DOTNET = $fakeDotnet
        $env:SIDETALK_BUILD_CHECK_INSTALL_RECORD = $installRecord
        $env:SIDETALK_BUILD_CHECK_INSTALL_EXIT = '0'

        $stub = @'
param([switch]$SelfContained)
exit 11
'@
        Set-Content -LiteralPath (Join-Path $fixture 'scripts\build.ps1') -Value $stub -Encoding utf8
        $run = Invoke-BuildLocal $fixture
        Assert-Equal 11 $run.ExitCode 'build-local failed build exit code'
        Assert-True (-not (Test-Path -LiteralPath $installRecord)) 'Setup stage ran after a failed build'
        Assert-Contains $run.Output 'Setup was not built or started' 'failed build guidance'

        $stub = @'
param([switch]$SelfContained)
$root = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Path (Join-Path $root 'artifacts\publish\win-x64') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $root 'artifacts\publish\win-x64\Sidetalk.exe') -Value 'synthetic app'
'@
        Set-Content -LiteralPath (Join-Path $fixture 'scripts\build.ps1') -Value $stub -Encoding utf8
        # Every option combination reaches install-local.ps1 with the documented meaning.
        foreach ($case in @(@('-NoPause', 'False,False'), @('-NoLaunch -NoPause', 'False,True'),
                @('-NoInstall -NoPause', 'True,False'), @('-NoInstall -NoLaunch -NoPause', 'True,False'))) {
            $run = Invoke-BuildLocal $fixture $case[0]
            Assert-Equal 0 $run.ExitCode "build-local success exit code ($($case[0]))"
            Assert-Equal $case[1] @(Get-Content -LiteralPath $installRecord)[-1] "install-local switches for $($case[0])"
        }

        # Setup cancelled (62), refused (64) or failed, including native negative codes.
        foreach ($code in @(62, 64, 61, -1, $script:hostNativeFailure)) {
            $env:SIDETALK_BUILD_CHECK_INSTALL_EXIT = [string]$code
            $run = Invoke-BuildLocal $fixture
            Assert-Equal $code $run.ExitCode "build-local Setup result $code"
            Assert-Contains $run.Output 'SIDETALK was not installed by this run' "Setup result $code explanation"
        }
        $env:SIDETALK_BUILD_CHECK_INSTALL_EXIT = '0'
        Remove-Item -LiteralPath $installRecord -Force

        $marker = Join-Path $fixture 'build-should-not-run.marker'
        $oldMarker = $env:SIDETALK_BUILD_CHECK_MARKER
        $env:SIDETALK_BUILD_CHECK_MARKER = $marker
        $stub = @'
param([switch]$SelfContained)
Set-Content -LiteralPath $env:SIDETALK_BUILD_CHECK_MARKER -Value 'unexpected'
'@
        Set-Content -LiteralPath (Join-Path $fixture 'scripts\build.ps1') -Value $stub -Encoding utf8
        # Native-style negative codes from SDK setup or `dotnet --version` must stop
        # before packaging, so no build, launch, or KakaoTalk/Sandboxie action runs.
        foreach ($code in @(-1, $script:hostNativeFailure)) {
            $ensureFailure = "param([Parameter(Mandatory=`$true)][string]`$ResultPath)`nexit $code"
            Set-Content -LiteralPath (Join-Path $fixture 'scripts\ensure-dotnet.ps1') -Value $ensureFailure -Encoding utf8
            $run = Invoke-BuildLocal $fixture
            Assert-Equal $code $run.ExitCode "build-local SDK bootstrap failure code $code"
            Assert-True (-not (Test-Path -LiteralPath $marker)) "build-local continued after SDK bootstrap failure $code"
            Assert-True (-not (Test-Path -LiteralPath $installRecord)) "Setup stage ran after SDK bootstrap failure $code"
            Assert-Contains $run.Output 'could not prepare the required .NET SDK automatically' 'bootstrap failure explanation missing'
        }

        Set-Content -LiteralPath (Join-Path $fixture 'scripts\ensure-dotnet.ps1') -Value $ensureStub -Encoding utf8
        Set-FakeDotnet $fakeTools '10.0.401' -ExitCode $script:hostNativeFailure
        $run = Invoke-BuildLocal $fixture
        Assert-Equal $script:hostNativeFailure $run.ExitCode 'build-local dotnet --version failure code'
        Assert-True (-not (Test-Path -LiteralPath $marker)) 'build-local continued after dotnet --version failure'
        Assert-True (-not (Test-Path -LiteralPath $installRecord)) 'Setup stage ran after dotnet --version failure'
    } finally {
        $env:CI = $oldCi
        $env:SIDETALK_BUILD_CHECK_DOTNET = $oldDotnet
        $env:SIDETALK_BUILD_CHECK_MARKER = $oldMarker
        $env:SIDETALK_BUILD_CHECK_INSTALL_RECORD = $oldRecord
        $env:SIDETALK_BUILD_CHECK_INSTALL_EXIT = $oldInstallExit
    }
}

function Test-BuildLocalWithRealBootstrap {
    $fixture = New-Fixture
    # build-local.cmd reads the SDK path back with `set /p` (console code page),
    # so exercise a non-ASCII folder whenever that code page can carry it.
    $name = 'SDK 도구'
    $encoding = [Console]::OutputEncoding
    if ($encoding.GetString($encoding.GetBytes($name)) -ne $name) { $name = 'SDK tools' }
    $systemDir = Join-Path $fixture $name
    Set-FakeDotnet $systemDir '10.0.401'
    $record = Join-Path $fixture 'build-environment.json'
    Set-InstallStub $fixture
    Set-Content -LiteralPath (Join-Path $fixture 'scripts\build.ps1') -Encoding utf8 -Value @'
param([switch]$SelfContained)
$root = Split-Path -Parent $PSScriptRoot
[pscustomobject]@{ DotnetRoot = $env:DOTNET_ROOT; Dotnet = (Get-Command dotnet.exe).Source; SelfContained = [bool]$SelfContained } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'build-environment.json') -Encoding utf8
New-Item -ItemType Directory -Path (Join-Path $root 'artifacts\publish\win-x64') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $root 'artifacts\publish\win-x64\Sidetalk.exe') -Value 'synthetic app'
'@
    $oldCi = $env:CI
    $oldPath = $env:PATH
    $oldRecord = $env:SIDETALK_BUILD_CHECK_INSTALL_RECORD
    $oldInstallExit = $env:SIDETALK_BUILD_CHECK_INSTALL_EXIT
    try {
        $env:CI = '1'
        $env:SIDETALK_BUILD_CHECK_INSTALL_RECORD = Join-Path $fixture 'install-calls.txt'
        $env:SIDETALK_BUILD_CHECK_INSTALL_EXIT = '0'
        $pwshDir = Split-Path -Parent (Get-Command pwsh -ErrorAction Stop).Source
        $env:PATH = "$systemDir;$pwshDir;$env:SystemRoot\System32"
        $output = @(& cmd.exe /d /c ('"{0}" -NoLaunch -NoPause' -f (Join-Path $fixture 'build-local.cmd')) 2>&1 | ForEach-Object { [string]$_ })
        Assert-Equal 0 $LASTEXITCODE "build-local with real SDK bootstrap`n$($output -join [Environment]::NewLine)"
    } finally {
        $env:CI = $oldCi
        $env:PATH = $oldPath
        $env:SIDETALK_BUILD_CHECK_INSTALL_RECORD = $oldRecord
        $env:SIDETALK_BUILD_CHECK_INSTALL_EXIT = $oldInstallExit
    }
    Assert-Equal 'False,True' @(Get-Content -LiteralPath (Join-Path $fixture 'install-calls.txt'))[-1] 'Setup stage after the bootstrapped build'
    $seen = Get-Content -LiteralPath $record -Raw -Encoding utf8 | ConvertFrom-Json
    Assert-Equal ($systemDir + '\') $seen.DotnetRoot 'DOTNET_ROOT given to build.ps1'
    Assert-Equal (Join-Path $systemDir 'dotnet.exe') $seen.Dotnet 'dotnet on PATH for build.ps1'
    Assert-True $seen.SelfContained 'build-local must package self-contained'
}

function Test-RunLocal {
    $fixture = New-Fixture
    $localAppData = Join-Path $fixture 'LocalAppData'
    New-Item -ItemType Directory -Path $localAppData -Force | Out-Null
    # The fixture's artifacts build must never be the fallback; its bytes stay put.
    $artifactsExe = Join-Path $fixture 'artifacts\publish\win-x64\Sidetalk.exe'
    $artifactsBefore = (Get-FileHash -LiteralPath $artifactsExe).Hash
    Set-Content -LiteralPath (Join-Path $fixture 'build-local.cmd') -Encoding ascii -Value "@echo build-local stub %*`r`n@exit /b 9"
    $oldCi = $env:CI
    $oldLocal = $env:LOCALAPPDATA
    try {
        $env:CI = '1'
        $env:LOCALAPPDATA = $localAppData
        $output = @(& cmd.exe /d /c ('"{0}" -NoPause' -f (Join-Path $fixture 'run-local.cmd')) 2>&1 | ForEach-Object { [string]$_ }) -join "`n"
        Assert-Equal 9 $LASTEXITCODE 'run-local must hand a missing installation to build-local'
        Assert-Contains $output 'SIDETALK is not installed. Building and opening Setup...' 'run-local missing-install message'
        Assert-Contains $output 'build-local stub -NoPause' 'run-local forwards -NoPause to build-local'
        Assert-Equal $artifactsBefore (Get-FileHash -LiteralPath $artifactsExe).Hash 'artifacts build changed'

        # Installed: the real installed path starts, without building.
        $installed = Join-Path $localAppData 'Programs\SIDETALK'
        Set-FakeDotnet $installed '0.0.0'
        Move-Item -LiteralPath (Join-Path $installed 'dotnet.exe') -Destination (Join-Path $installed 'Sidetalk.exe')
        Set-Content -LiteralPath (Join-Path $installed 'Sidetalk.dll') -Value 'synthetic'
        Set-Content -LiteralPath (Join-Path $installed 'unins000.exe') -Value 'synthetic'
        $output = @(& cmd.exe /d /c ('"{0}" -NoPause' -f (Join-Path $fixture 'run-local.cmd')) 2>&1 | ForEach-Object { [string]$_ }) -join "`n"
        Assert-Equal 0 $LASTEXITCODE "run-local installed exit code`n$output"
        Assert-True (-not $output.Contains('build-local stub')) 'run-local rebuilt an installed SIDETALK'
        $log = Join-Path $installed 'fake-dotnet.log'
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (-not (Test-Path -LiteralPath $log) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        Assert-True (Test-Path -LiteralPath $log) 'run-local did not start the installed Sidetalk.exe'
    } finally {
        $env:CI = $oldCi
        $env:LOCALAPPDATA = $oldLocal
    }
}

function Test-InstallLocalStages {
    $fixture = New-Fixture
    . (Join-Path $fixture 'scripts\install-local.ps1') -LoadOnly
    foreach ($case in @(@(0, 'Installed'), @(2, 'Cancelled'), @(5, 'Cancelled'), @(7, 'Blocked'), @(1, 'Failed'), @(3, 'Failed'), @(-1, 'Failed'))) {
        Assert-Equal $case[1] (Resolve-SetupOutcome $case[0]) "Setup exit $($case[0])"
    }

    $installRoot = Join-Path $fixture 'LocalAppData\Programs\SIDETALK'
    $programs = Join-Path $fixture 'StartMenu\Programs'
    $dataRoot = Join-Path $fixture 'LocalAppData\SIDETALK'
    New-Item -ItemType Directory -Path (Join-Path $dataRoot 'Boxes\synthetic') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $dataRoot 'profile.json') -Value '{"synthetic":true}'
    $dataBefore = Get-UserDataFingerprint $dataRoot

    # Only processes running from the installation (or with an unreadable path) block.
    function Get-Process {
        param($Name, $ErrorAction)
        @([pscustomobject]@{ Id = 11; Path = (Join-Path $installRoot 'Sidetalk.exe') },
          [pscustomobject]@{ Id = 12; Path = 'C:\Elsewhere\Sidetalk.exe' },
          [pscustomobject]@{ Id = 13; Path = $null })
    }
    Assert-Equal '11,13' ((@(Get-OwnedRunningProcesses $installRoot) | ForEach-Object Id) -join ',') 'owned running processes'

    $release = Join-Path $fixture 'artifacts\release'
    New-Item -ItemType Directory -Path $release -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $env:WINDIR 'System32\cmd.exe') -Destination (Join-Path $fixture 'artifacts\publish\win-x64\Sidetalk.dll')
    $setupLog = Join-Path $release 'fake-dotnet.log'
    $installerExit = Join-Path $fixture 'build-installer-exit.txt'
    Set-Content -LiteralPath (Join-Path $fixture 'scripts\build-installer.ps1') -Encoding utf8 -Value @"
exit ([int](Get-Content -LiteralPath '$installerExit'))
"@
    function Invoke-Stage([int]$BuildInstaller, [int]$Setup, [switch]$NoInstall) {
        Set-Content -LiteralPath $installerExit -Value $BuildInstaller
        Set-FakeDotnet $release 'setup' -ExitCode $Setup
        Move-Item -LiteralPath (Join-Path $release 'dotnet.exe') -Destination (Join-Path $release 'SIDETALK-Setup.exe') -Force
        Remove-Item -LiteralPath $setupLog -Force -ErrorAction SilentlyContinue
        try {
            $code = Invoke-InstallLocal -NoInstall:$NoInstall -InstallRoot $installRoot -ProgramsDir $programs -DataRoot $dataRoot 6>$null
        } catch {
            $code = Write-SidetalkFailure $_ 2>$null
        }
        [pscustomobject]@{ Code = $code; SetupStarted = (Test-Path -LiteralPath $setupLog) }
    }

    $run = Invoke-Stage 51 0
    Assert-Equal 51 $run.Code 'installer build failure code'
    Assert-True (-not $run.SetupStarted) 'Setup started after the installer build failed'
    $run = Invoke-Stage 0 0 -NoInstall
    Assert-Equal 0 $run.Code '-NoInstall exit code'
    Assert-True (-not $run.SetupStarted) '-NoInstall started Setup'
    $run = Invoke-Stage 0 0
    Assert-Equal 60 $run.Code 'running installed SIDETALK must block before Setup'
    Assert-True (-not $run.SetupStarted) 'Setup started while the installed SIDETALK was running'

    function Get-Process { param($Name, $ErrorAction) @() }
    foreach ($case in @(@(2, 62), @(5, 62), @(7, 64), @(1, 61), @(-1, 61))) {
        $run = Invoke-Stage 0 $case[0]
        Assert-True $run.SetupStarted "Setup exit $($case[0]) was not started"
        Assert-Equal $case[1] $run.Code "Setup exit $($case[0]) build-local code"
    }
    Assert-Equal $dataBefore (Get-UserDataFingerprint $dataRoot) 'synthetic user data changed'
    Assert-True (-not (Test-Path -LiteralPath $installRoot)) 'stage tests created an installation'

    # Post-install verification compares bytes, the Start Menu target and registration.
    $publish = Join-Path $fixture 'publish copy'
    foreach ($relative in @('Sidetalk.exe', 'Sidetalk.dll', 'Sidetalk.Core.dll', 'tools\IsolationProbe\Sidetalk.IsolationProbe.exe')) {
        foreach ($root in @($publish, $installRoot)) {
            New-Item -ItemType Directory -Path (Split-Path -Parent (Join-Path $root $relative)) -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $root $relative) -Value "build $relative"
        }
    }
    New-Item -ItemType Directory -Path $programs -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut((Join-Path $programs 'SIDETALK.lnk'))
        $link.TargetPath = Join-Path $installRoot 'Sidetalk.exe'
        $link.Save()
    } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
    $key = 'HKCU:\Software\SIDETALK-BuildChecks\' + [Guid]::NewGuid().ToString('N')
    New-Item -Path $key -Force | Out-Null
    try {
        New-ItemProperty -LiteralPath $key -Name DisplayName -Value 'SIDETALK' | Out-Null
        New-ItemProperty -LiteralPath $key -Name DisplayVersion -Value '0.2.0' | Out-Null
        $verify = { Assert-InstalledBuild -PublishDir $publish -InstallRoot $installRoot -ProgramsDir $programs -Version '0.2.0' -UninstallKey $key }
        & $verify | Out-Null
        Set-Content -LiteralPath (Join-Path $installRoot 'Sidetalk.dll') -Value 'previous build left in place'
        $failed = $false
        try { & $verify } catch { $failed = $_.Exception.Message.Contains('is not this build') }
        Assert-True $failed 'a same-version install that kept old bytes was accepted'
        Set-Content -LiteralPath (Join-Path $installRoot 'Sidetalk.dll') -Value 'build Sidetalk.dll'
        Set-ItemProperty -LiteralPath $key -Name DisplayVersion -Value '0.1.6'
        $failed = $false
        try { & $verify } catch { $failed = $_.Exception.Message.Contains('expected 0.2.0') }
        Assert-True $failed 'a stale Installed Apps version was accepted'
        Set-ItemProperty -LiteralPath $key -Name DisplayVersion -Value '0.2.0'
        Remove-Item -LiteralPath (Join-Path $programs 'SIDETALK.lnk')
        $failed = $false
        try { & $verify } catch { $failed = $_.Exception.Message.Contains('Start Menu shortcut is missing') }
        Assert-True $failed 'a missing Start Menu shortcut was accepted'
    } finally {
        Remove-Item -LiteralPath $key -Recurse -Force
        if (-not @(Get-ChildItem -LiteralPath 'HKCU:\Software\SIDETALK-BuildChecks' -ErrorAction SilentlyContinue).Count) {
            Remove-Item -LiteralPath 'HKCU:\Software\SIDETALK-BuildChecks' -Force -ErrorAction SilentlyContinue
        }
    }
}

# ---- SDK bootstrap (scripts/ensure-dotnet.ps1) ----------------------------
# The real script runs in a child pwsh. Only the network download and the
# Authenticode lookup are replaced; dotnet.exe is a real native executable
# whose reported version and exit code come from fake-dotnet.txt beside it.
$script:hostNativeFailure = -2147450725  # 0x8000809B: host could not resolve the global.json SDK

function Get-FakeDotnetExe {
    if ($script:fakeDotnetExe) { return $script:fakeDotnetExe }
    $dir = Join-Path $fixtureRoot 'fake dotnet build'
    Assert-FixturePath $dir
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $source = Join-Path $dir 'FakeDotnet.cs'
    Set-Content -LiteralPath $source -Encoding ascii -Value @'
using System;
using System.IO;
static class FakeDotnet {
    static int Main(string[] args) {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        File.AppendAllText(Path.Combine(dir, "fake-dotnet.log"), string.Join(" ", args) + "\r\n");
        string config = Path.Combine(dir, "fake-dotnet.txt");
        if (!File.Exists(config)) { return unchecked((int)0x8000809B); }
        string[] lines = File.ReadAllLines(config);
        int failFirst = lines.Length > 2 ? int.Parse(lines[2]) : 0;
        if (failFirst > 0) {
            lines[2] = (failFirst - 1).ToString();
            File.WriteAllLines(config, lines);
            Console.Error.WriteLine("A compatible .NET SDK was not found.");
            return unchecked((int)0x8000809B);
        }
        Console.WriteLine(lines[0]);
        return int.Parse(lines[1]);
    }
}
'@
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $exe = Join-Path $dir 'dotnet.exe'
    $compilerOutput = & $csc /nologo /target:exe ("/out:$exe") $source 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $exe)) { throw "Could not compile the fake dotnet.exe: $compilerOutput" }
    $script:fakeDotnetExe = $exe
    return $exe
}

function Set-FakeDotnet([string]$Dir, [string]$Version, [int]$ExitCode = 0, [int]$FailFirst = 0) {
    New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    Copy-Item -LiteralPath (Get-FakeDotnetExe) -Destination (Join-Path $Dir 'dotnet.exe') -Force
    Set-Content -LiteralPath (Join-Path $Dir 'fake-dotnet.txt') -Value @($Version, [string]$ExitCode, [string]$FailFirst) -Encoding ascii
}

function New-SdkFixture([string]$SystemVersion = '8.0.424', [int]$SystemExit = $script:hostNativeFailure) {
    $script:fixtureNumber++
    $root = Join-Path $fixtureRoot ('case ' + $script:fixtureNumber.ToString('00') + ' sdk fixture')
    Assert-FixturePath $root
    New-Item -ItemType Directory -Path (Join-Path $root 'scripts') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'scripts\ensure-dotnet.ps1') -Destination (Join-Path $root 'scripts\ensure-dotnet.ps1')
    Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination (Join-Path $root 'global.json')
    $systemDir = Join-Path $root 'system dotnet'
    if ($SystemVersion) { Set-FakeDotnet $systemDir $SystemVersion $SystemExit }

    # Mirrors dotnet-install.ps1: success returns without `exit`.
    $installer = Join-Path $root 'fake-dotnet-install.ps1'
    Set-Content -LiteralPath $installer -Encoding utf8 -Value @'
param([string]$Version, [string]$InstallDir, [string]$Architecture, [switch]$NoPath)
Add-Content -LiteralPath $env:SIDETALK_ENSURE_CHECK_LOG -Value 'installer'
switch ($env:SIDETALK_ENSURE_CHECK_INSTALL) {
    'throw' { throw 'synthetic dotnet-install failure' }
    'exit' { exit 7 }
}
New-Item -ItemType Directory -Path (Join-Path $InstallDir "sdk\$Version") -Force | Out-Null
Copy-Item -LiteralPath $env:SIDETALK_ENSURE_CHECK_FAKE_EXE -Destination (Join-Path $InstallDir 'dotnet.exe')
$reported = if ($env:SIDETALK_ENSURE_CHECK_INSTALL -eq 'wrongversion') { '9.0.100' } else { $Version }
Set-Content -LiteralPath (Join-Path $InstallDir 'fake-dotnet.txt') -Value @($reported, '0', '0') -Encoding ascii
Write-Host 'dotnet-install: Installation finished'
'@

    Set-Content -LiteralPath (Join-Path $root 'ensure-runner.ps1') -Encoding utf8 -Value @'
param([string]$ResultPath, [string]$InstallRoot, [string]$InstallerSha256, [int]$LockTimeoutSeconds = 180)
$ErrorActionPreference = 'Stop'
function Invoke-WebRequest {
    param([string]$Uri, [string]$OutFile)
    Add-Content -LiteralPath $env:SIDETALK_ENSURE_CHECK_LOG -Value 'download'
    if ($env:SIDETALK_ENSURE_CHECK_DOWNLOAD -eq 'fail') { throw 'synthetic network failure' }
    Copy-Item -LiteralPath $env:SIDETALK_ENSURE_CHECK_INSTALLER -Destination $OutFile
}
function Get-AuthenticodeSignature {
    param([string]$LiteralPath)
    Add-Content -LiteralPath $env:SIDETALK_ENSURE_CHECK_LOG -Value 'signature'
    $valid = [System.Management.Automation.SignatureStatus]::Valid
    $microsoft = [pscustomobject]@{
        Subject = 'CN=.NET, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
        Issuer = 'CN=Microsoft Code Signing PCA 2024, O=Microsoft Corporation, C=US'
    }
    switch ($env:SIDETALK_ENSURE_CHECK_SIGNATURE) {
        'invalid' { return [pscustomobject]@{ Status = [System.Management.Automation.SignatureStatus]::HashMismatch; SignerCertificate = $microsoft } }
        'nocert' { return [pscustomobject]@{ Status = $valid; SignerCertificate = $null } }
        'other' { return [pscustomobject]@{ Status = $valid; SignerCertificate = [pscustomobject]@{ Subject = 'CN=Contoso, O=Contoso'; Issuer = 'CN=Contoso CA, O=Contoso' } } }
        default { return [pscustomobject]@{ Status = $valid; SignerCertificate = $microsoft } }
    }
}
& (Join-Path $PSScriptRoot 'scripts\ensure-dotnet.ps1') -ResultPath $ResultPath -InstallRoot $InstallRoot `
    -InstallerUri 'https://example.invalid/dotnet-install.ps1' -InstallerSha256 $InstallerSha256 -LockTimeoutSeconds $LockTimeoutSeconds
exit $LASTEXITCODE
'@
    return [pscustomobject]@{
        Root = $root
        SystemDir = $systemDir
        HasSystem = [bool]$SystemVersion
        InstallRoot = Join-Path $root 'private cache'
        CacheDir = Join-Path $root 'private cache\10.0.401'
        Installer = $installer
        InstallerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
        ResultPath = Join-Path $root 'result\dotnet-path.txt'
        Log = Join-Path $root 'ensure.log'
    }
}

function Invoke-SdkFixture($Fixture, [hashtable]$Mode = @{}, [string]$InstallerHash = '', [int]$LockTimeoutSeconds = 180) {
    Remove-Item -LiteralPath $Fixture.ResultPath -Force -ErrorAction SilentlyContinue
    Set-Content -LiteralPath $Fixture.Log -Value @() -Encoding utf8
    $names = @('PATH', 'SIDETALK_ENSURE_CHECK_LOG', 'SIDETALK_ENSURE_CHECK_INSTALLER', 'SIDETALK_ENSURE_CHECK_FAKE_EXE',
        'SIDETALK_ENSURE_CHECK_DOWNLOAD', 'SIDETALK_ENSURE_CHECK_INSTALL', 'SIDETALK_ENSURE_CHECK_SIGNATURE')
    $saved = @{}
    foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
    try {
        $pwsh = (Get-Command pwsh -ErrorAction Stop).Source
        # No real SDK on PATH: only the synthetic system dotnet, if any.
        $env:PATH = if ($Fixture.HasSystem) { "$($Fixture.SystemDir);$env:SystemRoot\System32" } else { "$env:SystemRoot\System32" }
        $env:SIDETALK_ENSURE_CHECK_LOG = $Fixture.Log
        $env:SIDETALK_ENSURE_CHECK_INSTALLER = $Fixture.Installer
        $env:SIDETALK_ENSURE_CHECK_FAKE_EXE = Get-FakeDotnetExe
        $env:SIDETALK_ENSURE_CHECK_DOWNLOAD = $Mode['Download']
        $env:SIDETALK_ENSURE_CHECK_INSTALL = $Mode['Install']
        $env:SIDETALK_ENSURE_CHECK_SIGNATURE = $Mode['Signature']
        $hash = if ($InstallerHash) { $InstallerHash } else { $Fixture.InstallerHash }
        $output = @(& $pwsh -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File (Join-Path $Fixture.Root 'ensure-runner.ps1') `
                -ResultPath $Fixture.ResultPath -InstallRoot $Fixture.InstallRoot -InstallerSha256 $hash -LockTimeoutSeconds $LockTimeoutSeconds 2>&1 |
            ForEach-Object { [string]$_ })
        $code = $LASTEXITCODE
    } finally {
        foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
    $result = $null
    if (Test-Path -LiteralPath $Fixture.ResultPath) {
        $result = [Console]::OutputEncoding.GetString([IO.File]::ReadAllBytes($Fixture.ResultPath))
    }
    $events = @(Get-Content -LiteralPath $Fixture.Log | Where-Object { $_ })
    return [pscustomobject]@{
        ExitCode = $code
        Output = ($output -join "`n")
        Result = $result
        Downloads = @($events | Where-Object { $_ -eq 'download' }).Count
        Installs = @($events | Where-Object { $_ -eq 'installer' }).Count
        Signatures = @($events | Where-Object { $_ -eq 'signature' }).Count
    }
}

# The fake dotnet logs each call beside itself; compare everything else.
function Get-SdkSnapshot([string]$Dir) {
    $snapshot = Get-FileSnapshot $Dir
    $snapshot.Remove('fake-dotnet.log')
    return $snapshot
}

function Assert-BootstrapCleanExit($Fixture, $Run) {
    foreach ($needle in @('null-valued expression', 'null 값 식')) {
        Assert-True (-not $Run.Output.Contains($needle)) "cleanup raised a null method call:`n$($Run.Output)"
    }
    if (Test-Path -LiteralPath $Fixture.InstallRoot) {
        Assert-Equal 0 @(Get-ChildItem -LiteralPath $Fixture.InstallRoot -Directory -Force -Filter '.staging-*').Count 'staging folder left behind'
        $lockPath = Join-Path $Fixture.InstallRoot '.install.lock'
        if (Test-Path -LiteralPath $lockPath) {
            $probe = [IO.File]::Open($lockPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $probe.Dispose()
        }
    }
}

function Test-SdkBootstrapUsesSystemSdk {
    $fixture = New-SdkFixture -SystemVersion '10.0.402' -SystemExit 0
    $run = Invoke-SdkFixture $fixture
    Assert-Equal 0 $run.ExitCode "satisfying system SDK exit code`n$($run.Output)"
    Assert-Equal (Join-Path $fixture.SystemDir 'dotnet.exe') $run.Result 'system SDK path'
    Assert-Equal 0 $run.Downloads 'system SDK must not download'
    Assert-True (-not (Test-Path -LiteralPath $fixture.InstallRoot)) 'system SDK must not create a private cache'
}

function Test-SdkBootstrapUsesPrivateCache {
    $fixture = New-SdkFixture
    Set-FakeDotnet $fixture.CacheDir '10.0.401'
    $systemBefore = Get-SdkSnapshot $fixture.SystemDir
    $run = Invoke-SdkFixture $fixture
    Assert-Equal 0 $run.ExitCode "private cache exit code`n$($run.Output)"
    Assert-Equal (Join-Path $fixture.CacheDir 'dotnet.exe') $run.Result 'private cache path'
    Assert-Equal 0 $run.Downloads 'cached SDK must not download'
    Assert-Equal 0 $run.Installs 'cached SDK must not install'
    Assert-Contains $run.Output 'Using the cached SIDETALK SDK 10.0.401' 'cache use message'
    Assert-SnapshotEqual $systemBefore (Get-SdkSnapshot $fixture.SystemDir) 'system SDK changed'

    # Without a system dotnet at all the cache is still selected.
    $fixture = New-SdkFixture -SystemVersion ''
    Set-FakeDotnet $fixture.CacheDir '10.0.401'
    $run = Invoke-SdkFixture $fixture
    Assert-Equal 0 $run.ExitCode "cache without system dotnet`n$($run.Output)"
    Assert-Equal 0 $run.Downloads 'cache without system dotnet must not download'
}

function Test-SdkBootstrapInstallsThenReuses {
    $fixture = New-SdkFixture
    # Leftovers from an interrupted run and an unrelated SDK version.
    $oldStaging = Join-Path $fixture.InstallRoot '.staging-0123456789abcdef'
    Set-FakeDotnet $oldStaging '10.0.401'
    $oldQuarantine = Join-Path $fixture.InstallRoot '10.0.401.invalid-0123456789abcdef'
    New-Item -ItemType Directory -Path $oldQuarantine -Force | Out-Null
    $otherVersion = Join-Path $fixture.InstallRoot '9.0.100'
    Set-FakeDotnet $otherVersion '9.0.100'
    $otherBefore = Get-SdkSnapshot $otherVersion

    $run = Invoke-SdkFixture $fixture
    Assert-Equal 0 $run.ExitCode "first install exit code`n$($run.Output)"
    Assert-BootstrapCleanExit $fixture $run
    Assert-Equal (Join-Path $fixture.CacheDir 'dotnet.exe') $run.Result 'installed SDK path'
    Assert-Equal 1 $run.Downloads 'first install download count'
    Assert-Equal 1 $run.Installs 'first install installer count'
    Assert-Equal 1 $run.Signatures 'first install signature checks'
    foreach ($stage in @('Download', 'Install', 'Verify staged SDK', 'Verify signature', 'Promote cache', 'Ready')) {
        Assert-Contains $run.Output "[dotnet/bootstrap] $stage" "missing stage $stage"
    }
    Assert-True (Test-Path -LiteralPath (Join-Path $fixture.CacheDir 'sdk\10.0.401')) 'promoted SDK layout'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $fixture.CacheDir 'dotnet-install.ps1'))) 'installer script left in the cache'
    Assert-True (-not (Test-Path -LiteralPath $oldStaging)) 'interrupted staging was not replaced'
    Assert-True (-not (Test-Path -LiteralPath $oldQuarantine)) 'old quarantine was not cleaned'
    Assert-SnapshotEqual $otherBefore (Get-SdkSnapshot $otherVersion) 'another SDK version cache changed'

    $run = Invoke-SdkFixture $fixture
    Assert-Equal 0 $run.ExitCode "second run exit code`n$($run.Output)"
    Assert-Equal 0 $run.Downloads 'second run must not download'
    Assert-Equal 0 $run.Installs 'second run must not install'
    Assert-Equal (Join-Path $fixture.CacheDir 'dotnet.exe') $run.Result 'second run SDK path'

    # A cache that resolves an older feature band does not satisfy global.json.
    $fixture = New-SdkFixture
    Set-FakeDotnet $fixture.CacheDir '10.0.300'
    $run = Invoke-SdkFixture $fixture
    Assert-Equal 0 $run.ExitCode "stale cache replacement`n$($run.Output)"
    Assert-Equal 1 $run.Installs 'stale cache must be replaced'
    Assert-Equal '10.0.401' (Get-Content -LiteralPath (Join-Path $fixture.CacheDir 'fake-dotnet.txt'))[0] 'replacement cache version'
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $fixture.InstallRoot -Directory -Force -Filter '10.0.401.invalid-*').Count 'quarantine left after replacement'
}

function Test-SdkBootstrapFailuresAreRecoverable {
    $cases = @(
        @{ Name = 'download'; Stage = 'Download'; Mode = @{ Download = 'fail' } },
        @{ Name = 'hash'; Stage = 'Download'; Hash = ('0' * 64) },
        @{ Name = 'installer throw'; Stage = 'Install'; Mode = @{ Install = 'throw' } },
        @{ Name = 'installer exit'; Stage = 'Install'; Mode = @{ Install = 'exit' } },
        @{ Name = 'sdk version'; Stage = 'Verify staged SDK'; Mode = @{ Install = 'wrongversion' } },
        @{ Name = 'signature status'; Stage = 'Verify signature'; Mode = @{ Signature = 'invalid' } },
        @{ Name = 'signature certificate'; Stage = 'Verify signature'; Mode = @{ Signature = 'nocert' } },
        @{ Name = 'signature publisher'; Stage = 'Verify signature'; Mode = @{ Signature = 'other' } }
    )
    foreach ($case in $cases) {
        $fixture = New-SdkFixture
        Set-FakeDotnet $fixture.CacheDir '10.0.300'
        $cacheBefore = Get-SdkSnapshot $fixture.CacheDir
        $systemBefore = Get-SdkSnapshot $fixture.SystemDir
        $mode = if ($case.Mode) { $case.Mode } else { @{} }
        $run = Invoke-SdkFixture $fixture -Mode $mode -InstallerHash $case.Hash
        Assert-Equal 1 $run.ExitCode "$($case.Name) failure exit code`n$($run.Output)"
        Assert-BootstrapCleanExit $fixture $run
        Assert-Contains $run.Output "[dotnet/bootstrap] Failed during: $($case.Stage)" "$($case.Name) failure stage"
        Assert-True ($null -eq $run.Result) "$($case.Name) failure produced an SDK path"
        Assert-True (-not $run.Output.Contains('[dotnet/bootstrap] Ready')) "$($case.Name) failure reported ready"
        Assert-SnapshotEqual $cacheBefore (Get-SdkSnapshot $fixture.CacheDir) "$($case.Name) failure changed the existing cache"
        Assert-SnapshotEqual $systemBefore (Get-SdkSnapshot $fixture.SystemDir) "$($case.Name) failure changed the system SDK"

        $run = Invoke-SdkFixture $fixture
        Assert-Equal 0 $run.ExitCode "$($case.Name) recovery exit code`n$($run.Output)"
        Assert-Equal (Join-Path $fixture.CacheDir 'dotnet.exe') $run.Result "$($case.Name) recovery SDK path"
    }
}

function Test-SdkBootstrapLockPaths {
    # Lock never acquired: finally runs with a null lock after an exception.
    $fixture = New-SdkFixture
    New-Item -ItemType Directory -Path $fixture.InstallRoot -Force | Out-Null
    $held = [IO.File]::Open((Join-Path $fixture.InstallRoot '.install.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $run = Invoke-SdkFixture $fixture -LockTimeoutSeconds 1
    } finally {
        $held.Dispose()
    }
    Assert-Equal 1 $run.ExitCode "held lock exit code`n$($run.Output)"
    Assert-BootstrapCleanExit $fixture $run
    Assert-Contains $run.Output '[dotnet/bootstrap] Failed during: Acquire setup lock' 'held lock stage'
    Assert-Equal 0 $run.Downloads 'held lock must not download'

    # Lock acquired, then a successful `exit 0` inside the locked block: another
    # build finished the cache while this one waited.
    $fixture = New-SdkFixture
    Set-FakeDotnet $fixture.CacheDir '10.0.401' -FailFirst 1
    $run = Invoke-SdkFixture $fixture
    Assert-Equal 0 $run.ExitCode "locked recheck exit code`n$($run.Output)"
    Assert-BootstrapCleanExit $fixture $run
    Assert-Contains $run.Output '[dotnet/bootstrap] Acquire setup lock' 'locked recheck must hold the lock'
    Assert-Equal 0 $run.Downloads 'locked recheck must not download'
    Assert-Equal (Join-Path $fixture.CacheDir 'dotnet.exe') $run.Result 'locked recheck SDK path'
}

function Test-ScriptsAvoidAmbiguousNullConditional {
    # `$name?.Member` parses as a variable literally named `name?`.
    $files = @(Get-ChildItem -LiteralPath (Join-Path $repo 'scripts') -Filter '*.ps1') + @(Get-Item -LiteralPath $PSCommandPath)
    foreach ($file in $files) {
        $tokens = $null
        $parseErrors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
        Assert-Equal 0 $parseErrors.Count "$($file.Name) PowerShell parse errors"
        $bad = @($tokens | Where-Object { $_.Kind -eq 'Variable' -and $_.Name.EndsWith('?') })
        Assert-Equal 0 $bad.Count "$($file.Name) uses a variable name ending in '?': $($bad.Text -join ', ')"
    }
}

try {
    New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
    Invoke-Case 'verify leaves deployment and profile untouched' { Test-VerifyLeavesDeployment }
    Invoke-Case 'verify failure and optional UI codes' { Test-VerifyFailureCodes }
    Invoke-Case 'packaging succeeds for both runtime flavors' { Test-PackagingSuccessAndArguments }
    Invoke-Case 'build, test, publish, docs, and archive failures preserve artifacts' { Test-PackagingFailuresPreserveOutput }
    Invoke-Case 'preflight running, lock, and junction guards' { Test-PreflightGuards }
    Invoke-Case 'promotion rollback and preserved failed rollback' { Test-PromotionRollback }
    Invoke-Case 'verification and packaging without existing artifacts' { Test-EmptyArtifacts }
    Invoke-Case 'artifact boundary and nested reparse point guards' { Test-ArtifactBoundaries }
    Invoke-Case 'build-local bootstraps SDK and forwards exit codes' { Test-BuildLocalExitCodes }
    Invoke-Case 'build-local passes the bootstrapped SDK to packaging' { Test-BuildLocalWithRealBootstrap }
    Invoke-Case 'run-local opens the installation or routes to Setup' { Test-RunLocal }
    Invoke-Case 'install-local stages, Setup outcomes and installed-build checks' { Test-InstallLocalStages }
    Invoke-Case 'SDK bootstrap uses a satisfying system SDK' { Test-SdkBootstrapUsesSystemSdk }
    Invoke-Case 'SDK bootstrap prefers the private cache over an old system SDK' { Test-SdkBootstrapUsesPrivateCache }
    Invoke-Case 'SDK bootstrap installs once, cleans up, and reuses the cache' { Test-SdkBootstrapInstallsThenReuses }
    Invoke-Case 'SDK bootstrap failures name the stage and stay recoverable' { Test-SdkBootstrapFailuresAreRecoverable }
    Invoke-Case 'SDK bootstrap lock cleanup on null and held locks' { Test-SdkBootstrapLockPaths }
    Invoke-Case 'scripts avoid ambiguous null-conditional variables' { Test-ScriptsAvoidAmbiguousNullConditional }
    if ($failures.Count) {
        throw ($failures -join [Environment]::NewLine)
    }
    Write-Host ("All synthetic build checks passed ({0:F2}s; {1} temporary repositories)." -f $timer.Elapsed.TotalSeconds, $script:fixtureNumber)
} finally {
    if ($script:hadFailure -or $KeepFixtureOnFailure) {
        Write-Host "Synthetic fixture preserved at $fixtureRoot"
    } elseif (Test-Path -LiteralPath $fixtureRoot) {
        Remove-FixtureTree $fixtureRoot
    }
}
