using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Sidetalk.Core;

namespace Sidetalk.IsolationProbe;

internal static class Program
{
    private const int Schema = 1;
    private const string RegistryValueName = "Sentinel";
    private const string SentinelFileName = "sentinel.txt";

    private static int Main(string[] args)
    {
        var phase = args.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "";
        var stage = "시작";
        var manifestPath = "";
        ProbeManifest? loadedManifest = null;
        try
        {
            stage = "인자 확인";
            if (!IsPhase(args.ElementAtOrDefault(0) ?? "") ||
                (args[0].Equals("prepare", StringComparison.OrdinalIgnoreCase) ? args.Length != 3 : args.Length != 2))
                throw new ArgumentException("사용법: prepare|host-seed|box-write|box-recheck|host-verify <manifest 또는 profile> <manifest>");

            switch (phase)
            {
                case "prepare":
                    stage = "prepare";
                    Prepare(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
                    break;
                default:
                    stage = "매니페스트 경로 확인";
                    manifestPath = Path.GetFullPath(args[1]);
                    EnsureFailureTraceTarget(manifestPath);
                    stage = "매니페스트 읽기·검증";
                    loadedManifest = LoadAndValidate(manifestPath);
                    stage = "box 단계 실행";
                    RunPhase(phase, loadedManifest);
                    break;
            }

            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine("실패: " + ex.Message);
            if (phase == "box-write" || phase == "box-recheck")
                TryWriteFailureTrace(manifestPath, phase, stage, loadedManifest?.RunId, ex);
            return 1;
        }
    }

    private static bool IsPhase(string phase) => phase.ToLowerInvariant() switch
    {
        "prepare" or "host-seed" or "box-write" or "box-recheck" or "host-verify" => true,
        _ => false
    };

    private static void Prepare(string profilePath, string manifestPath)
    {
        if (File.Exists(manifestPath))
            throw new IOException("기존 매니페스트를 덮어쓰지 않습니다: " + manifestPath);
        ProfileStore.RejectReparseAncestors(profilePath);
        var profile = JsonSerializer.Deserialize<BoxProfile>(File.ReadAllText(profilePath))
            ?? throw new InvalidDataException("제안된 SIDETALK 프로필을 읽을 수 없습니다.");
        var store = new ProfileStore();
        store.Validate(profile);

        var runId = Guid.NewGuid().ToString("N");
        var hostToken = RandomHex(32);
        var boxToken = RandomHex(32);
        var manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("매니페스트 폴더를 확인할 수 없습니다.");
        var resultPath = Path.Combine(manifestDirectory, "isolation-probe-result-" + runId + ".json");
        var manifest = CreateManifest(profile, runId, hostToken, boxToken, resultPath);
        ValidateManifest(manifest, manifestPath);
        ProfileStore.RejectReparseAncestors(manifestPath);
        if (!Directory.Exists(manifestDirectory))
            throw new DirectoryNotFoundException("매니페스트 폴더가 없습니다. 작업 폴더 안에 미리 만들어 주세요: " + manifestDirectory);
        using var stream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, manifest, JsonOptions);
        stream.Flush(true);
        Console.WriteLine($"준비 완료: 실행 {runId}, 시험 파일 4개, HKCU 시험 키 1개");
    }

    private static void RunPhase(string phase, ProbeManifest manifest)
    {
        switch (phase)
        {
            case "host-seed":
                HostSeed(manifest);
                break;
            case "box-write":
                BoxWrite(manifest);
                break;
            case "box-recheck":
                BoxRecheck(manifest);
                break;
            case "host-verify":
                HostVerify(manifest);
                break;
            default:
                throw new ArgumentException("알 수 없는 단계입니다.");
        }
    }

    private static void HostSeed(ProbeManifest manifest)
    {
        EnsureHostSentinelsAbsent(manifest);
        EnsureHostRegistryAbsent(manifest);
        foreach (var path in manifest.HostFiles)
            WriteNewFile(path, HostContent(manifest, path));
        using var key = CreateNewRegistryKey(manifest.RegistryPath);
        key.SetValue(RegistryValueName, HostContent(manifest, manifest.RegistryPath), RegistryValueKind.String);
        key.Flush();
        Console.WriteLine("호스트 시드 완료: 파일 4개, HKCU 시험 값 1개");
    }

    private static void BoxWrite(ProbeManifest manifest)
    {
        using var api = OpenSandboxieApi();
        RequireCurrentProcessInOwnedBox(api, manifest.Profile);
        EnsureHostSentinelsAbsent(manifest);
        EnsureHostRegistryAbsent(manifest);
        foreach (var path in manifest.HostFiles)
            WriteNewFile(path, BoxContent(manifest, path));
        using var key = CreateNewRegistryKey(manifest.RegistryPath);
        key.SetValue(RegistryValueName, BoxContent(manifest, manifest.RegistryPath), RegistryValueKind.String);
        key.Flush();
        WriteResult(manifest, "box-write", true);
        Console.WriteLine("박스 쓰기 완료: 소유 프로세스 확인, 파일 4개, HKCU 시험 값 1개");
    }

    private static void BoxRecheck(ProbeManifest manifest)
    {
        using var api = OpenSandboxieApi();
        RequireCurrentProcessInOwnedBox(api, manifest.Profile);
        EnsureFilesEqual(manifest, BoxContent);
        EnsureRegistryEqual(manifest, BoxContent(manifest, manifest.RegistryPath));
        WriteResult(manifest, "box-recheck", true);
        Console.WriteLine("박스 재실행 유지 확인: 소유 프로세스 확인, 파일 4개, HKCU 시험 값 1개");
    }

    private static void HostVerify(ProbeManifest manifest)
    {
        EnsureFilesEqual(manifest, HostContent);
        EnsureRegistryEqual(manifest, HostContent(manifest, manifest.RegistryPath));
        Console.WriteLine("호스트 원본 유지 확인: 파일 4개, HKCU 시험 값 1개");
    }

    private static SandboxieApi OpenSandboxieApi()
    {
        var dependency = DependencyDetector.Sandboxie();
        if (!dependency.Available || string.IsNullOrWhiteSpace(dependency.Path))
            throw new InvalidOperationException("Sandboxie Plus 설치 경로를 확인할 수 없습니다.");
        var directory = Path.GetDirectoryName(dependency.Path)
            ?? throw new InvalidDataException("Sandboxie Plus 설치 폴더를 확인할 수 없습니다.");
        return new SandboxieApi(directory);
    }

    private static void RequireCurrentProcessInOwnedBox(SandboxieApi api, BoxProfile profile)
    {
        var processes = api.Processes(profile);
        if (!processes.Any(p => p.Id == Environment.ProcessId))
            throw new InvalidOperationException("현재 시험 프로세스가 검증된 전용 box 안에 없습니다.");
    }

    private static void EnsureHostSentinelsAbsent(ProbeManifest manifest)
    {
        foreach (var path in manifest.HostFiles)
            if (File.Exists(path))
                throw new InvalidDataException("호스트 시험 파일이 보입니다. 격리를 확인하지 못해 중단합니다.");
    }

    private static void EnsureHostRegistryAbsent(ProbeManifest manifest)
    {
        using var key = Registry.CurrentUser.OpenSubKey(manifest.RegistryPath, writable: false);
        if (key is not null)
            throw new InvalidDataException("호스트 HKCU 시험 키가 보입니다. 격리를 확인하지 못해 중단합니다.");
    }

    private static void EnsureFilesEqual(ProbeManifest manifest, Func<ProbeManifest, string, string> content)
    {
        foreach (var path in manifest.HostFiles)
        {
            if (!File.Exists(path))
                throw new InvalidDataException("시험 파일이 없습니다: " + path);
            var actual = File.ReadAllText(path, Encoding.UTF8);
            var expected = content(manifest, path);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidDataException("시험 파일 토큰이 다릅니다: " + path);
        }
    }

    private static void EnsureRegistryEqual(ProbeManifest manifest, string expected)
    {
        using var key = Registry.CurrentUser.OpenSubKey(manifest.RegistryPath, writable: false)
            ?? throw new InvalidDataException("HKCU 시험 키가 없습니다.");
        var actual = key.GetValue(RegistryValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException("HKCU 시험 값 토큰이 다릅니다.");
    }

    private static RegistryKey CreateNewRegistryKey(string path)
    {
        using (var existing = Registry.CurrentUser.OpenSubKey(path, writable: false))
            if (existing is not null)
                throw new IOException("기존 HKCU 시험 키를 덮어쓰지 않습니다: " + path);
        var key = Registry.CurrentUser.CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException("HKCU 시험 키를 만들 수 없습니다.");
        return key;
    }

    private static void WriteNewFile(string path, string content)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidDataException("시험 파일 폴더를 확인할 수 없습니다.");
        ProfileStore.RejectReparseAncestors(path);
        Directory.CreateDirectory(parent);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
        writer.Flush();
        stream.Flush(true);
    }

    private static void WriteResult(ProbeManifest manifest, string phase, bool passed)
    {
        var path = phase == "box-write" ? manifest.ResultPath : RecheckResultPath(manifest);
        ProfileStore.RejectReparseAncestors(path);
        var result = new ProbeResult(manifest.Schema, manifest.RunId, phase, passed, DateTimeOffset.UtcNow);
        // A result path is created once per box phase. Existing host files are never overwritten.
        if (File.Exists(path))
            throw new IOException("기존 시험 결과를 덮어쓰지 않습니다: " + path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, result, JsonOptions);
        stream.Flush(true);
    }

    private static void EnsureFailureTraceTarget(string manifestPath)
    {
        if (!Path.IsPathFullyQualified(manifestPath) || !File.Exists(manifestPath))
            throw new InvalidDataException("기존 절대 경로 매니페스트만 사용할 수 있습니다.");
        var directory = Path.GetDirectoryName(manifestPath);
        if (directory is null || !Directory.Exists(directory))
            throw new InvalidDataException("매니페스트 폴더를 확인할 수 없습니다.");
        ProfileStore.RejectReparseAncestors(manifestPath);
    }

    private static void TryWriteFailureTrace(string manifestPath, string phase, string stage, string? runId, Exception exception)
    {
        try
        {
            EnsureFailureTraceTarget(manifestPath);
            var directory = Path.GetDirectoryName(manifestPath)
                ?? throw new InvalidDataException("매니페스트 결과 폴더를 확인할 수 없습니다.");
            var tracePath = Path.Combine(directory, "isolation-probe-failure-" + (runId ?? "unavailable") + "-" + Guid.NewGuid().ToString("N") + ".json");
            ProfileStore.RejectReparseAncestors(tracePath);
            var trace = new ProbeFailureTrace(Schema, runId ?? "unavailable", phase, stage, exception.GetType().Name, RedactTraceMessage(exception.Message), DateTimeOffset.UtcNow);
            using var stream = new FileStream(tracePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, trace, JsonOptions);
            stream.Flush(true);
            Console.Error.WriteLine("실패 추적: " + tracePath);
        }
        catch (Exception traceException) when (traceException is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine("실패 추적을 기록하지 못했습니다: " + traceException.Message);
        }
    }

    private static string RedactTraceMessage(string message) =>
        Regex.Replace(message, "(?i)(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])", "<redacted>");
    private static ProbeManifest CreateManifest(BoxProfile profile, string runId, string hostToken, string boxToken, string resultPath)
    {
        var user = RequireKnownFolder(Environment.SpecialFolder.UserProfile, "사용자 프로필");
        var roaming = RequireKnownFolder(Environment.SpecialFolder.ApplicationData, "Roaming");
        var local = RequireKnownFolder(Environment.SpecialFolder.LocalApplicationData, "Local");
        var common = RequireKnownFolder(Environment.SpecialFolder.CommonApplicationData, "ProgramData");
        return new ProbeManifest(
            Schema,
            runId,
            hostToken,
            boxToken,
            profile,
            new[]
            {
                Path.Combine(user, "SIDETALK-IsolationProbe", runId, SentinelFileName),
                Path.Combine(roaming, "SIDETALK-IsolationProbe", runId, SentinelFileName),
                Path.Combine(local, "SIDETALK-IsolationProbe", runId, SentinelFileName),
                Path.Combine(common, "Kakao", "SIDETALK-IsolationProbe", runId, SentinelFileName)
            },
            @"Software\SIDETALK\IsolationProbe\" + runId,
            resultPath);
    }

    private static ProbeManifest LoadAndValidate(string manifestPath)
    {
        ProfileStore.RejectReparseAncestors(manifestPath);
        var manifest = JsonSerializer.Deserialize<ProbeManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("격리 시험 매니페스트를 읽을 수 없습니다.");
        ValidateManifest(manifest, manifestPath);
        return manifest;
    }

    private static void ValidateManifest(ProbeManifest manifest, string manifestPath)
    {
        if (manifest.Profile is null || manifest.HostFiles is null || string.IsNullOrWhiteSpace(manifest.RegistryPath) || string.IsNullOrWhiteSpace(manifest.ResultPath) ||
            manifest.Schema != Schema || !IsLowerHex(manifest.RunId, 32) ||
            !IsLowerHex(manifest.HostToken, 64) || !IsLowerHex(manifest.BoxToken, 64) ||
            manifest.HostToken.Equals(manifest.BoxToken, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("격리 시험 식별자가 유효하지 않습니다.");
        var store = new ProfileStore();
        store.Validate(manifest.Profile);
        var manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("매니페스트 폴더를 확인할 수 없습니다.");
        var expected = CreateManifest(manifest.Profile, manifest.RunId, manifest.HostToken, manifest.BoxToken,
            Path.Combine(manifestDirectory, "isolation-probe-result-" + manifest.RunId + ".json"));
        if (manifest.HostFiles.Count != 4 || manifest.HostFiles.Zip(expected.HostFiles).Any(pair => !PathEquals(pair.First, pair.Second)) ||
            !string.Equals(manifest.RegistryPath, expected.RegistryPath, StringComparison.Ordinal) ||
            !PathEquals(manifest.ResultPath, expected.ResultPath))
            throw new InvalidDataException("매니페스트 경로가 생성 규칙과 다릅니다.");
        foreach (var path in manifest.HostFiles.Append(manifest.ResultPath).Append(RecheckResultPath(manifest)))
        {
            if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("시험 경로가 절대 경로가 아닙니다.");
            ProfileStore.RejectReparseAncestors(path);
        }
    }

    private static string RequireKnownFolder(Environment.SpecialFolder folder, string label)
    {
        // A hidden host folder need not exist in a fresh box. Query its logical path without creating it.
        var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Windows 알려진 폴더를 확인할 수 없습니다: " + label);
        ProfileStore.RejectReparseAncestors(path);
        return Path.GetFullPath(path);
    }

    private static string RecheckResultPath(ProbeManifest manifest) =>
        Path.Combine(Path.GetDirectoryName(manifest.ResultPath)!, Path.GetFileNameWithoutExtension(manifest.ResultPath) + "-recheck.json");

    private static string HostContent(ProbeManifest manifest, string _) => "SIDETALK-HOST-" + manifest.HostToken;
    private static string BoxContent(ProbeManifest manifest, string _) => "SIDETALK-BOX-" + manifest.BoxToken;
    private static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
    private static bool IsLowerHex(string value, int length) => value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = null };

    private sealed record ProbeManifest(int Schema, string RunId, string HostToken, string BoxToken, BoxProfile Profile, IReadOnlyList<string> HostFiles, string RegistryPath, string ResultPath);
    private sealed record ProbeResult(int Schema, string RunId, string Phase, bool Passed, DateTimeOffset CheckedAtUtc);
    private sealed record ProbeFailureTrace(int Schema, string RunId, string Phase, string Stage, string ErrorType, string Message, DateTimeOffset CheckedAtUtc);
}
