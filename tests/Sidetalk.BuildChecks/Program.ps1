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
    foreach ($name in @('build.ps1', 'build-common.ps1', 'verify.ps1', 'ensure-dotnet.ps1')) {
        Copy-Item -LiteralPath (Join-Path $repo ('scripts\' + $name)) -Destination (Join-Path $root ('scripts\' + $name))
    }
    Copy-Item -LiteralPath (Join-Path $repo 'build-local.cmd') -Destination (Join-Path $root 'build-local.cmd')
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
function Test-BuildLocalExitCodes {
    $fixture = New-Fixture
    $fakeTools = Join-Path $fixture 'fake-dotnet'
    New-Item -ItemType Directory -Path $fakeTools -Force | Out-Null
    $fakeDotnet = Join-Path $fakeTools 'dotnet.cmd'
    Set-Content -LiteralPath $fakeDotnet -Value '@echo 10.0.401' -Encoding ascii

    $ensureStub = @'
param([Parameter(Mandatory=$true)][string]$ResultPath)
[IO.File]::WriteAllText($ResultPath, $env:SIDETALK_BUILD_CHECK_DOTNET, [Text.UTF8Encoding]::new($false))
exit 0
'@
    Set-Content -LiteralPath (Join-Path $fixture 'scripts\ensure-dotnet.ps1') -Value $ensureStub -Encoding utf8

    $oldCi = $env:CI
    $oldDotnet = $env:SIDETALK_BUILD_CHECK_DOTNET
    try {
        $env:CI = '1'
        $env:SIDETALK_BUILD_CHECK_DOTNET = $fakeDotnet

        $stub = @'
param([switch]$SelfContained)
exit 11
'@
        Set-Content -LiteralPath (Join-Path $fixture 'scripts\build.ps1') -Value $stub -Encoding utf8
        $output = @(& cmd.exe /d /c ('"{0}" -NoLaunch -NoPause' -f (Join-Path $fixture 'build-local.cmd')) 2>&1 | ForEach-Object { [string]$_ })
        Assert-Equal 11 $LASTEXITCODE 'build-local failed build exit code'

        $stub = @'
param([switch]$SelfContained)
$root = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Path (Join-Path $root 'artifacts\publish\win-x64') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $root 'artifacts\publish\win-x64\Sidetalk.exe') -Value 'synthetic app'
'@
        Set-Content -LiteralPath (Join-Path $fixture 'scripts\build.ps1') -Value $stub -Encoding utf8
        $output = @(& cmd.exe /d /c ('"{0}" -NoLaunch -NoPause' -f (Join-Path $fixture 'build-local.cmd')) 2>&1 | ForEach-Object { [string]$_ })
        Assert-Equal 0 $LASTEXITCODE 'build-local success exit code'

        $marker = Join-Path $fixture 'build-should-not-run.marker'
        $stub = @"
param([switch]\$SelfContained)
Set-Content -LiteralPath '$($marker.Replace("'", "''"))' -Value 'unexpected'
"@
        Set-Content -LiteralPath (Join-Path $fixture 'scripts\build.ps1') -Value $stub -Encoding utf8
        $ensureFailure = @'
param([Parameter(Mandatory=$true)][string]$ResultPath)
exit -1
'@
        Set-Content -LiteralPath (Join-Path $fixture 'scripts\ensure-dotnet.ps1') -Value $ensureFailure -Encoding utf8
        $output = @(& cmd.exe /d /c ('"{0}" -NoLaunch -NoPause' -f (Join-Path $fixture 'build-local.cmd')) 2>&1 | ForEach-Object { [string]$_ })
        Assert-True ($LASTEXITCODE -ne 0) 'build-local accepted a negative SDK bootstrap failure code'
        Assert-True (-not (Test-Path -LiteralPath $marker)) 'build-local continued after SDK bootstrap failure'
        Assert-Contains ($output -join [Environment]::NewLine) 'could not prepare the required .NET SDK automatically' 'bootstrap failure explanation missing'

        $tokens = $null
        $parseErrors = $null
        [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $repo 'scripts\ensure-dotnet.ps1'), [ref]$tokens, [ref]$parseErrors) | Out-Null
        Assert-Equal 0 $parseErrors.Count 'ensure-dotnet.ps1 PowerShell parse errors'
    } finally {
        $env:CI = $oldCi
        $env:SIDETALK_BUILD_CHECK_DOTNET = $oldDotnet
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
