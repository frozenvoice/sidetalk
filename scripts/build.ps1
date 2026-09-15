param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repo
try {
    dotnet build src/Sidetalk.App/Sidetalk.App.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project tests/Sidetalk.Tests/Sidetalk.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Safety tests failed.' }
    $flavor = if ($SelfContained) { 'self-contained' } else { 'desktop-runtime' }
    $output = Join-Path $repo ('artifacts/publish/win-x64-' + $flavor + '-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
    $bundled = if ($SelfContained) { 'true' } else { 'false' }
    dotnet publish src/Sidetalk.App/Sidetalk.App.csproj -c Release -r win-x64 --self-contained $bundled -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $output
    Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $output -Recurse -Force
    $zip = Join-Path $repo ('artifacts/SIDETALK-0.1.1-win-x64-' + $(if ($SelfContained) { 'self-contained' } else { 'desktop-runtime' }) + '.zip')
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -Force
    Get-FileHash -LiteralPath $zip -Algorithm SHA256
    Write-Output ('App: ' + (Join-Path $output 'Sidetalk.exe'))
} finally { Pop-Location }
