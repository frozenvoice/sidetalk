[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $SourcePath,

    [string] $ExpectedSha256 = '9194D31B8986D17B8698C1394C0722DACE698838DADDF41612F1083A8A4E52D7',

    [string] $RepoRoot = (Join-Path $PSScriptRoot '..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-RepositoryPath {
    param(
        [Parameter(Mandatory = $true)] [string] $Root,
        [Parameter(Mandatory = $true)] [string] $RelativePath
    )

    $rootFull = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Root).Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $target = [IO.Path]::GetFullPath((Join-Path $rootFull $RelativePath))
    $prefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "출력 경로가 저장소 밖에 있습니다: $target"
    }

    return $target
}

function Read-Sections {
    param([Parameter(Mandatory = $true)] [string] $Text)

    $lines = $Text -split "`n"
    $sections = [ordered]@{}
    $current = $null
    foreach ($lineWithCr in $lines) {
        $line = $lineWithCr.TrimEnd("`r")
        $match = [Regex]::Match($line, '^\[([^\]]+)\]$')
        if ($match.Success) {
            $current = $match.Groups[1].Value
            # Older Sandboxie sources contain an empty deprecated duplicate for
            # one template. Keep the first definition, which is the active one.
            if ($sections.Contains($current)) {
                $current = $null
                continue
            }

            $sections[$current] = [Collections.Generic.List[string]]::new()
            [void] $sections[$current].Add($line)
            continue
        }

        if ($null -ne $current) {
            [void] $sections[$current].Add($line)
        }
    }

    return $sections
}

function Get-Configuration {
    param(
        [Parameter(Mandatory = $true)] [Collections.Generic.IEnumerable[string]] $Lines
    )

    $result = [ordered]@{}
    foreach ($line in $Lines) {
        # Sandboxie ignores blank and comment lines. Keep only active key/value
        # records here; duplicate keys remain ordered in the value array.
        $match = [Regex]::Match($line, '^\s*([^#;\s][^=]*?)\s*=\s?(.*)$')
        if (-not $match.Success) {
            continue
        }

        $key = $match.Groups[1].Value.Trim()
        $value = $match.Groups[2].Value
        if (-not $result.Contains($key)) {
            $result[$key] = [Collections.Generic.List[string]]::new()
        }

        [void] $result[$key].Add($value)
    }

    return $result
}

function Convert-ToJsonObject {
    param([Parameter(Mandatory = $true)] [Collections.IDictionary] $Sections)

    $output = [ordered]@{}
    foreach ($sectionName in $Sections.Keys) {
        $configuration = Get-Configuration -Lines $Sections[$sectionName]
        $sectionOutput = [ordered]@{}
        foreach ($key in $configuration.Keys) {
            $sectionOutput[$key] = @($configuration[$key])
        }

        $output[$sectionName] = $sectionOutput
    }

    return $output
}

$rootFull = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $RepoRoot).Path)
$sourceFull = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $SourcePath).Path)
$actualSha256 = (Get-FileHash -LiteralPath $sourceFull -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualSha256 -ne $ExpectedSha256.ToUpperInvariant()) {
    throw "Sandboxie 원문 SHA-256이 일치하지 않습니다. expected=$ExpectedSha256 actual=$actualSha256"
}

$defaultTemplateNames = @(
    'RpcPortBindings', 'SpecialImages', 'COM', 'WindowsExplorer',
    'ThirdPartyIsolation', 'BlockSoftwareUpdaters', 'BlockWinRM',
    'OpenWinInetCache', 'CredentialUIBroker', 'MSI_Lite'
)
$specialSectionNames = @(
    'TemplateDefaultPaths', 'TemplateNetworkPaths', 'TemplateSModPaths',
    'TemplatePModPaths', 'TemplateDefaultFolders', 'TemplateAppCPaths',
    'TemplateSettings'
)
$selectedNames = @('DefaultTemplates') + ($defaultTemplateNames | ForEach-Object { "Template_$_" }) + $specialSectionNames
$allSections = Read-Sections -Text ([IO.File]::ReadAllText($sourceFull, [Text.Encoding]::UTF8))

$selected = [ordered]@{}
foreach ($sectionName in $selectedNames) {
    if (-not $allSections.Contains($sectionName)) {
        throw "공식 원문에 필요한 섹션이 없습니다: [$sectionName]"
    }

    $selected[$sectionName] = $allSections[$sectionName]
}

$jsonPath = Resolve-RepositoryPath -Root $rootFull -RelativePath 'src/Sidetalk.Core/SandboxieDefaults.json'
$fixturePath = Resolve-RepositoryPath -Root $rootFull -RelativePath 'tests/Sidetalk.Tests/Fixtures/sandboxie-1.18.4.ini'
$fixtureDirectory = Split-Path -Parent $fixturePath
New-Item -ItemType Directory -Path $fixtureDirectory -Force | Out-Null

$json = (Convert-ToJsonObject -Sections $selected | ConvertTo-Json -Depth 20)
[IO.File]::WriteAllText($jsonPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

$header = @(
    '# SIDETALK test fixture: selected sections from Sandboxie Plus v1.18.4 Templates.ini',
    "# Source SHA-256: $actualSha256",
    '# Source: https://raw.githubusercontent.com/sandboxie-plus/Sandboxie/v1.18.4/Sandboxie/install/Templates.ini',
    '# Upstream: Sandboxie Plus (GPL-3.0-or-later); source attribution retained for this derived fixture.',
    '# Generated by scripts/update-sandboxie-baseline.ps1. This file is data only; it is never installed or applied.',
    ''
)
$fixtureLines = [Collections.Generic.List[string]]::new()
$header | ForEach-Object { $fixtureLines.Add($_) }
foreach ($sectionName in $selectedNames) {
    foreach ($line in $selected[$sectionName]) {
        $fixtureLines.Add($line)
    }
}
[IO.File]::WriteAllText($fixturePath, ($fixtureLines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

Write-Output "Generated $jsonPath"
Write-Output "Generated $fixturePath"
Write-Output "Source SHA-256: $actualSha256"

# Recovery selectors are never policy allow-list entries. Their exact upstream
# definitions are pinned solely to recognize a reviewed removable conflict.
$recoveryNames = @('WindowsRasMan', 'OnScreenKeyboard', 'WindowsLive', 'Edge_Fix', 'OfficeLicensing', 'OfficeClickToRun')
$recoverySections = [ordered]@{}
foreach ($name in $recoveryNames) {
    $sectionName = "Template_$name"
    if (-not $allSections.Contains($sectionName)) { throw "Missing recovery template: $sectionName" }
    $configuration = Get-Configuration -Lines $allSections[$sectionName]
    if ($configuration.Contains('Template')) { throw "Nested recovery template requires review: $sectionName" }
    $recoverySections[$sectionName] = $allSections[$sectionName]
}
$recoveryPath = Resolve-RepositoryPath -Root $rootFull -RelativePath 'src/Sidetalk.Core/RecoveryTemplates.json'
$recoveryJson = Convert-ToJsonObject -Sections $recoverySections | ConvertTo-Json -Depth 20
[IO.File]::WriteAllText($recoveryPath, $recoveryJson + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "Generated $recoveryPath"