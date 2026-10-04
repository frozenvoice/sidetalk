param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'net8-persistence.json'),
    [string]$SdkVersion = '8.0.424'
)

$ErrorActionPreference = 'Stop'
$revision = '81477439bd735bd440446bc2b3fcab163159f503'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$generationRoot = Join-Path ([IO.Path]::GetTempPath()) ('sidetalk-net8-fixtures-' + [Guid]::NewGuid().ToString('N'))
$coreRoot = Join-Path $generationRoot 'Core'
[IO.Directory]::CreateDirectory($coreRoot) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)

# Compile the pre-migration Core in disposable directories. No app, profile store,
# Sandboxie API, journal operation, installation or real user data is accessed.
$sourcePaths = & git -C $repository ls-tree -r --name-only $revision -- src/Sidetalk.Core
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the pre-migration source revision.' }
foreach ($sourcePath in $sourcePaths) {
    if ([IO.Path]::GetExtension($sourcePath) -notin '.cs', '.json') { continue }
    $source = & git -C $repository show ($revision + ':' + $sourcePath)
    if ($LASTEXITCODE -ne 0) { throw "Cannot read $sourcePath from $revision." }
    [IO.File]::WriteAllLines((Join-Path $coreRoot ([IO.Path]::GetFileName($sourcePath))), [string[]]$source, $utf8)
}
[IO.File]::WriteAllText((Join-Path $generationRoot 'global.json'), '{"sdk":{"version":"' + $SdkVersion + '","rollForward":"disable"}}', $utf8)
[IO.File]::WriteAllText((Join-Path $coreRoot 'Core.csproj'), @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><EmbeddedResource Include="*.json" /></ItemGroup>
</Project>
'@, $utf8)
[IO.File]::WriteAllText((Join-Path $generationRoot 'Generator.csproj'), @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><Compile Remove="Core/**/*.cs" /><ProjectReference Include="Core/Core.csproj" /></ItemGroup>
</Project>
'@, $utf8)
[IO.File]::WriteAllText((Join-Path $generationRoot 'Program.cs'), @'
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sidetalk.Core;

const string revision = "81477439bd735bd440446bc2b3fcab163159f503";
const string operation = "11111111111111111111111111111111";
const string root = @"C:\__SIDETALK_FIXTURE_ROOT__";
const string start = root + @"\Sandboxie\Start.exe";
var profile = new BoxProfile(1, "SideTalk_aaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "__SIDETALK_FIXTURE_SID__", root + @"\Boxes\SideTalk_aaaaaaaaaaaaaaaaaaaaaaa");
var date = new DateTimeOffset(2026, 9, 1, 2, 3, 4, TimeSpan.Zero);
var identity = new CommandProcessIdentity(1234, date.UtcTicks, start);
var context = new CommandRecoveryContext(profile.UserSid, 1, null);
var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() }, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
var journalType = typeof(CommandJournal).GetNestedType("JournalEntry", BindingFlags.NonPublic)!;
var recoveryType = typeof(CommandJournal).Assembly.GetType("Sidetalk.Core.SetupRecovery")!.GetNestedType("RecoveryEvent", BindingFlags.NonPublic)!;
var commandFixtures = new Dictionary<string, JsonElement>();
foreach (var schema in new[] { 1, 2 })
foreach (var phase in new[] { "intent", "started", "failed", "completed" })
{
    if (schema == 1 && phase == "failed") continue;
    var failure = phase == "failed" ? new CommandFailure("process-query", "System.IO.IOException", "합성 조회 실패", "IOException: temporary fixture", false) : null;
    var record = Activator.CreateInstance(journalType, new object?[] { schema, phase, operation, SidetalkCommandAction.Launch, profile, start, date, phase == "intent" ? null : identity, schema == 1 ? null : context, failure })!;
    commandFixtures.Add($"schema{schema}.{phase}", JsonSerializer.SerializeToElement(record, journalType, options));
}
var recoveryFixtures = new Dictionary<string, JsonElement>();
foreach (var (sequence, kind) in new[] { (0, "Started"), (1, "BackupCreated"), (2, "Completed"), (999999, "Failed") })
{
    var record = Activator.CreateInstance(recoveryType, new object?[] { operation, sequence, kind, "22222222222222222222222222222222", "synthetic-legacy-scope", "합성 복구 기록", date, null })!;
    recoveryFixtures.Add(kind, JsonSerializer.SerializeToElement(record, recoveryType, options));
}
var manifest = new { SourceRevision = revision, SdkVersion = args[1], Runtime = RuntimeInformation.FrameworkDescription, Profile = profile, Command = commandFixtures, Recovery = recoveryFixtures };
File.WriteAllText(args[0], JsonSerializer.Serialize(manifest, options), new UTF8Encoding(false));
'@, $utf8)

Push-Location $generationRoot
try {
    & dotnet run --project Generator.csproj --configuration Release -- $OutputPath $SdkVersion
    if ($LASTEXITCODE -ne 0) { throw "Fixture generator failed with exit code $LASTEXITCODE. Source retained at $generationRoot." }
}
finally { Pop-Location }
Write-Output "Generated $OutputPath using isolated .NET 8 source at $generationRoot."
