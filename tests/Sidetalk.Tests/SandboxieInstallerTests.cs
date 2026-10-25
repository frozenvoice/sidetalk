using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class SandboxieInstallerTests
{
    public static IEnumerable<(string Name, Action Run)> Cases =>
    [
        ("Sandboxie support: compatibility, UI version and download target are one release", SupportValuesAgree),
        ("Sandboxie installer refuses a release outside the official versioned GitHub asset", ReleaseDefinitionMustBeOfficial),
        ("Sandboxie detection separates missing, unresolved, unsigned, unsupported and stopped installations", DetectionSeparatesConditions),
        ("Sandboxie installer is offered only when no installation or trace exists", OfferOnlyWhenNothingInstalled),
        ("Sandboxie download verifies size, hash and signature and holds the file read-only", DownloadVerifiesAndHolds),
        ("Sandboxie download with a wrong SHA-256 is deleted and never verified or launched", WrongHashIsDeleted),
        ("Sandboxie download rejects an unsigned file through the real Authenticode verifier", UnsignedFileIsRejected),
        ("Sandboxie download enforces the pinned size for headers, oversized and truncated bodies", SizeIsEnforced),
        ("Sandboxie download follows only bounded HTTPS redirects to official hosts", RedirectsAreBounded),
        ("Sandboxie download reports network, HTTP, stall and overall timeout failures and cleans up", NetworkFailuresCleanUp),
        ("Sandboxie download cancellation deletes the partial file", CancellationCleansUp),
        ("Sandboxie installer run maps UAC refusal, cancel, restart and start failure without claiming success", RunOutcomesAreGuidanceOnly),
        ("Sandboxie installer never downloads or launches over an installation that appeared", ExistingInstallationIsProtected),
        ("Controller gives distinct Sandboxie guidance and blocks an unavailable service", ControllerSeparatesSandboxieConditions)
    ];

    private static readonly byte[] Payload = CreatePayload();

    private static byte[] CreatePayload()
    {
        var bytes = new byte[200_000];
        new Random(18_4).NextBytes(bytes);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        return bytes;
    }

    private static SandboxieInstallerRelease Release(byte[]? content = null, long? size = null, string? sha = null) => SandboxieSupport.Installer with
    {
        Size = size ?? (content ?? Payload).Length,
        Sha256 = sha ?? Convert.ToHexString(SHA256.HashData(content ?? Payload))
    };

    private static DependencyInfo Missing() => new("Sandboxie Plus", null, "미설치", "없음", false) { Condition = DependencyCondition.NotInstalled };

    private static void SupportValuesAgree()
    {
        var installer = SandboxieSupport.Installer;
        Assert.Equal(SandboxieSupport.PlusVersion, RuntimeVerifier.PlusVersion);
        Assert.Equal(SandboxieSupport.CoreVersion, RuntimeVerifier.CoreVersion);
        Assert.Equal(SandboxieSupport.DriverVersion, RuntimeVerifier.DriverVersion);
        Assert.Equal(RuntimeVerifier.PlusVersion, installer.Version);
        Assert.Equal($"Sandboxie-Plus-x64-v{RuntimeVerifier.PlusVersion}.exe", installer.FileName);
        Assert.True(SandboxieInstaller.IsOfficialDownload(installer.DownloadUri, installer), "The pinned asset must be the official versioned GitHub download.");
        Assert.Equal($"https://github.com/sandboxie-plus/Sandboxie/releases/tag/v{RuntimeVerifier.PlusVersion}", installer.ReleasePage.ToString());
        Assert.Equal(24_853_640L, installer.Size);
        Assert.Equal("CFCEDA1B1A63ABCD2B3E5FBFDC4895F15DB56316F5A9F2F06AEA230E209B120E", installer.Sha256);
        Assert.Equal("NOVASYNC LABS PTE. LTD.", installer.Publisher);
        Assert.True(RuntimeVerifier.MatchesVersion(installer.Version, RuntimeVerifier.PlusVersion), "The download must pass the compatibility check.");
        Assert.False(RuntimeVerifier.MatchesVersion("1.18.5", RuntimeVerifier.PlusVersion), "A newer release must stay unsupported until reviewed.");
        _ = SandboxieInstaller.CreateDefault();
    }

    private static void ReleaseDefinitionMustBeOfficial()
    {
        var official = SandboxieSupport.Installer;
        var invalid = new (string Name, SandboxieInstallerRelease Release)[]
        {
            ("other host", official with { DownloadUri = new Uri("https://example.com/sandboxie-plus/Sandboxie/releases/download/v1.18.4/Sandboxie-Plus-x64-v1.18.4.exe") }),
            ("plain HTTP", official with { DownloadUri = new Uri("http://github.com/sandboxie-plus/Sandboxie/releases/download/v1.18.4/Sandboxie-Plus-x64-v1.18.4.exe") }),
            ("latest instead of the version", official with { DownloadUri = new Uri("https://github.com/sandboxie-plus/Sandboxie/releases/latest/download/Sandboxie-Plus-x64-v1.18.4.exe") }),
            ("other version asset", official with { DownloadUri = new Uri("https://github.com/sandboxie-plus/Sandboxie/releases/download/v1.18.5/Sandboxie-Plus-x64-v1.18.5.exe") }),
            ("ARM64 asset", official with { FileName = "Sandboxie-Plus-ARM64-v1.18.4.exe" }),
            ("query string", official with { DownloadUri = new Uri(official.DownloadUri + "?x=1") }),
            ("unversioned release page", official with { ReleasePage = new Uri("https://sandboxie-plus.com/downloads/") }),
            ("missing hash", official with { Sha256 = "" }),
            ("missing size", official with { Size = 0 }),
            ("missing publisher", official with { Publisher = " " })
        };
        foreach (var (name, release) in invalid)
            Assert.Throws<ArgumentException>(() => new SandboxieInstaller(release, new FakeHandler((_, _) => Fail(new InvalidOperationException())),
                new RecordingVerifier(), new FakeLauncher(), Missing, TestContext.CreateRunDirectory("sbie-release")), name);
    }

    private static void DetectionSeparatesConditions()
    {
        static string Version(string _) => "1.18.4";
        static void Signed(string _) { }
        static RuntimeVerifier.SandboxieServiceState Running() => RuntimeVerifier.SandboxieServiceState.Running;
        var path = @"C:\Program Files\Sandboxie-Plus\SandMan.exe";

        var missing = DependencyDetector.ClassifySandboxie(null, false, Version, Signed, Running);
        Assert.Equal(DependencyCondition.NotInstalled, missing.Condition);
        Assert.False(missing.Available);
        Assert.True(SandboxieInstaller.CanOffer(missing), "A clean machine must be offered the install.");

        var unresolved = DependencyDetector.ClassifySandboxie(null, true, Version, Signed, Running);
        Assert.Equal(DependencyCondition.PathUnresolved, unresolved.Condition);
        Assert.False(SandboxieInstaller.CanOffer(unresolved), "An installation trace must never be treated as missing.");

        var unsigned = DependencyDetector.ClassifySandboxie(path, false, Version, _ => throw new InvalidDataException("서명 없음"), Running);
        Assert.Equal(DependencyCondition.SignatureInvalid, unsigned.Condition);
        Assert.False(unsigned.Available);
        Assert.Equal(path, unsigned.Path);

        var unsupported = DependencyDetector.ClassifySandboxie(path, false, _ => "1.19.0", Signed, Running);
        Assert.Equal(DependencyCondition.UnsupportedVersion, unsupported.Condition);
        Assert.True(unsupported.Available, "Version gating stays in the controller's existing block.");

        foreach (var state in new[] { RuntimeVerifier.SandboxieServiceState.NotRunning, RuntimeVerifier.SandboxieServiceState.Missing })
        {
            var stopped = DependencyDetector.ClassifySandboxie(path, false, Version, Signed, () => state);
            Assert.Equal(DependencyCondition.ServiceUnavailable, stopped.Condition);
            Assert.Contains("SbieSvc", stopped.Detail);
        }

        Assert.Equal(DependencyCondition.Verified, DependencyDetector.ClassifySandboxie(path, false, Version, Signed, Running).Condition);
        // An unreadable SCM status is not a fault; RuntimeVerifier still requires the running service later.
        Assert.Equal(DependencyCondition.Verified, DependencyDetector.ClassifySandboxie(path, false, Version, Signed, () => RuntimeVerifier.SandboxieServiceState.Unreadable).Condition);
    }

    private static void OfferOnlyWhenNothingInstalled()
    {
        Assert.True(SandboxieInstaller.CanOffer(Missing()));
        foreach (var condition in Enum.GetValues<DependencyCondition>().Where(value => value != DependencyCondition.NotInstalled))
            Assert.False(SandboxieInstaller.CanOffer(Missing() with { Condition = condition }), condition.ToString());
        Assert.False(SandboxieInstaller.CanOffer(Missing() with { Path = @"C:\x\SandMan.exe" }), "A found executable is an installation.");
        Assert.False(SandboxieInstaller.CanOffer(Missing() with { Available = true }), "An available dependency is an installation.");
    }

    private static void DownloadVerifiesAndHolds()
    {
        var root = TestContext.CreateRunDirectory("sbie-download");
        var verifier = new RecordingVerifier();
        var launcher = new FakeLauncher();
        var installer = Create(root, Serve(Payload), verifier, launcher);
        var reports = new List<SandboxieDownloadProgress>();
        using var verified = installer.DownloadAsync(new SyncProgress(reports.Add), CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(File.Exists(verified.FilePath), "The verified installer must exist until it runs.");
        Assert.Equal(SandboxieSupport.Installer.FileName, Path.GetFileName(verified.FilePath));
        Assert.True(verified.FilePath.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Downloads must stay inside the work root.");
        Assert.SequenceEqual(SHA256.HashData(Payload), SHA256.HashData(File.ReadAllBytes(verified.FilePath)));
        Assert.Equal(1, verifier.Calls);
        Assert.True(reports.Count >= 2 && reports[^1].Verifying && reports.Where(r => !r.Verifying).Max(r => r.Received) == Payload.Length,
            "Progress must report real bytes and then verification.");
        Assert.Throws<IOException>(() => new FileStream(verified.FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose(), "verified file write");
        Assert.Equal(0, launcher.Calls);
        verified.Dispose();
        AssertNoWorkDirectories(root);
    }

    private static void WrongHashIsDeleted()
    {
        var root = TestContext.CreateRunDirectory("sbie-hash");
        var verifier = new RecordingVerifier();
        var launcher = new FakeLauncher();
        var tampered = (byte[])Payload.Clone();
        tampered[^1] ^= 0xFF;
        var installer = Create(root, Serve(tampered), verifier, launcher);
        var error = Assert.Throws<SandboxieInstallerException>(() => installer.DownloadAsync(null, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal(SandboxieInstallerFailure.Integrity, error.Kind);
        Assert.Equal(0, verifier.Calls);
        Assert.Equal(0, launcher.Calls);
        AssertNoWorkDirectories(root);
    }

    private static void UnsignedFileIsRejected()
    {
        var root = TestContext.CreateRunDirectory("sbie-signature");
        var launcher = new FakeLauncher();
        var installer = new SandboxieInstaller(Release(), Serve(Payload), new AuthenticodeSandboxieInstallerVerifier(), launcher, Missing, root);
        var error = Assert.Throws<SandboxieInstallerException>(() => installer.DownloadAsync(null, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal(SandboxieInstallerFailure.Signature, error.Kind);
        Assert.Equal(0, launcher.Calls);
        AssertNoWorkDirectories(root);
    }

    private static void SizeIsEnforced()
    {
        var root = TestContext.CreateRunDirectory("sbie-size");
        var cases = new (string Name, FakeHandler Handler)[]
        {
            ("declared length", Serve(Payload.Concat(new byte[] { 1 }).ToArray())),
            ("oversized body without length", new FakeHandler((_, _) => Ok(new StreamContent(new ChunkStream(Payload.Concat(new byte[4096]).ToArray()))))),
            ("truncated body without length", new FakeHandler((_, _) => Ok(new StreamContent(new ChunkStream(Payload[..^10])))))
        };
        foreach (var (name, handler) in cases)
        {
            var verifier = new RecordingVerifier();
            var error = Assert.Throws<SandboxieInstallerException>(() => Create(root, handler, verifier, new FakeLauncher())
                .DownloadAsync(null, CancellationToken.None).GetAwaiter().GetResult(), name);
            Assert.Equal(SandboxieInstallerFailure.Size, error.Kind);
            Assert.Equal(0, verifier.Calls);
            AssertNoWorkDirectories(root);
        }
    }

    private static void RedirectsAreBounded()
    {
        var root = TestContext.CreateRunDirectory("sbie-redirect");
        var asset = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/fixture?sig=x");
        var visited = new List<Uri>();
        var official = new FakeHandler((request, _) =>
        {
            visited.Add(request.RequestUri!);
            return request.RequestUri == asset ? Ok(new ByteArrayContent(Payload)) : Redirect(asset);
        });
        using (var verified = Create(root, official).DownloadAsync(null, CancellationToken.None).GetAwaiter().GetResult())
            Assert.SequenceEqual(new[] { SandboxieSupport.Installer.DownloadUri, asset }, visited);

        var refused = new (string Name, FakeHandler Handler)[]
        {
            ("foreign host", new FakeHandler((_, _) => Redirect(new Uri("https://downloads.example.net/Sandboxie-Plus-x64-v1.18.4.exe")))),
            ("plain HTTP", new FakeHandler((_, _) => Redirect(new Uri("http://objects.githubusercontent.com/fixture")))),
            ("look-alike host", new FakeHandler((_, _) => Redirect(new Uri("https://github.com.example.net/fixture")))),
            ("missing location", new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Found))),
            ("redirect loop", new FakeHandler((_, _) => Redirect(new Uri("https://github.com/sandboxie-plus/Sandboxie/loop"))))
        };
        foreach (var (name, handler) in refused)
        {
            var error = Assert.Throws<SandboxieInstallerException>(() => Create(root, handler).DownloadAsync(null, CancellationToken.None).GetAwaiter().GetResult(), name);
            Assert.Equal(SandboxieInstallerFailure.Redirect, error.Kind);
            AssertNoWorkDirectories(root);
        }
    }

    private static void NetworkFailuresCleanUp()
    {
        var root = TestContext.CreateRunDirectory("sbie-network");
        var cases = new (string Name, SandboxieInstaller Installer, SandboxieInstallerFailure Kind)[]
        {
            ("offline", Create(root, new FakeHandler((_, _) => Fail(new HttpRequestException("offline")))), SandboxieInstallerFailure.Network),
            ("HTTP 503", Create(root, new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))), SandboxieInstallerFailure.Network),
            ("connection reset", Create(root, new FakeHandler((_, _) => Ok(new StreamContent(new ChunkStream(Payload, failAfter: 4096))))), SandboxieInstallerFailure.Network),
            ("stalled body", Create(root, new FakeHandler((_, _) => Ok(new StreamContent(new ChunkStream(Payload, stallAfter: 4096)))), stall: TimeSpan.FromMilliseconds(200)), SandboxieInstallerFailure.Timeout),
            ("overall timeout", Create(root, FakeHandler.Async(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Ok(new ByteArrayContent(Payload)); }), timeout: TimeSpan.FromMilliseconds(200)), SandboxieInstallerFailure.Timeout)
        };
        foreach (var (name, installer, kind) in cases)
        {
            var error = Assert.Throws<SandboxieInstallerException>(() => installer.DownloadAsync(null, CancellationToken.None).GetAwaiter().GetResult(), name);
            Assert.Equal(kind, error.Kind);
            Assert.True(error.Message.Length > 0 && error.Message.Any(ch => ch >= '가' && ch <= '힣'), $"{name} must explain itself in Korean.");
            AssertNoWorkDirectories(root);
        }
    }

    private static void CancellationCleansUp()
    {
        var root = TestContext.CreateRunDirectory("sbie-cancel");
        using var cancel = new CancellationTokenSource();
        var installer = Create(root, new FakeHandler((_, _) => Ok(new StreamContent(new ChunkStream(Payload, stallAfter: 8192)))));
        var reports = 0;
        var download = installer.DownloadAsync(new SyncProgress(_ => { if (++reports == 1) cancel.Cancel(); }), cancel.Token);
        Assert.Throws<OperationCanceledException>(() => download.GetAwaiter().GetResult());
        AssertNoWorkDirectories(root);
    }

    private static void RunOutcomesAreGuidanceOnly()
    {
        var root = TestContext.CreateRunDirectory("sbie-run");
        var cases = new (string Name, Func<string, int> Behaviour, SandboxieInstallerOutcome Outcome)[]
        {
            ("exit 0", _ => 0, SandboxieInstallerOutcome.Exited),
            ("UAC declined", _ => throw new Win32Exception(1223), SandboxieInstallerOutcome.ElevationDeclined),
            ("cancel before install", _ => 2, SandboxieInstallerOutcome.Cancelled),
            ("cancel during install", _ => 5, SandboxieInstallerOutcome.Cancelled),
            ("restart required", _ => 8, SandboxieInstallerOutcome.RestartRequired),
            ("fatal", _ => 4, SandboxieInstallerOutcome.Failed),
            ("start failure", _ => throw new Win32Exception(2), SandboxieInstallerOutcome.Failed),
            ("unknown code", _ => 42, SandboxieInstallerOutcome.Failed)
        };
        foreach (var (name, behaviour, outcome) in cases)
        {
            var launcher = new FakeLauncher(behaviour);
            var installer = Create(root, Serve(Payload), launcher: launcher);
            var verified = installer.DownloadAsync(null, CancellationToken.None).GetAwaiter().GetResult();
            var run = installer.RunAsync(verified, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(outcome, run.Outcome);
            Assert.Equal(1, launcher.Calls);
            Assert.Equal(verified.FilePath, launcher.LastPath);
            Assert.True(launcher.SawFileWhileRunning, $"{name}: the verified file must exist while the installer runs.");
            Assert.False(run.Detail.Contains("완료되었습니다", StringComparison.Ordinal) || run.Detail.Contains("설치됐습니다", StringComparison.Ordinal),
                $"{name}: an installer exit is not evidence of installation.");
            AssertNoWorkDirectories(root);
        }
        Assert.Equal(SandboxieInstallerOutcome.Exited, SandboxieInstaller.Interpret(0).Outcome);
    }

    private static void ExistingInstallationIsProtected()
    {
        var root = TestContext.CreateRunDirectory("sbie-existing");
        var installed = new DependencyInfo("Sandboxie Plus", @"C:\Program Files\Sandboxie-Plus\SandMan.exe", "1.19.0", "설치됨", true)
            { Condition = DependencyCondition.UnsupportedVersion };
        var requests = 0;
        var handler = new FakeHandler((_, _) => { requests++; return Ok(new ByteArrayContent(Payload)); });
        foreach (var current in new[] { installed, installed with { Condition = DependencyCondition.Verified }, Missing() with { Condition = DependencyCondition.PathUnresolved } })
        {
            var refused = Assert.Throws<SandboxieInstallerException>(() => Create(root, handler, detect: () => current)
                .DownloadAsync(null, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(SandboxieInstallerFailure.Refused, refused.Kind);
        }
        Assert.Equal(0, requests);

        var state = Missing();
        var launcher = new FakeLauncher();
        var installer = Create(root, Serve(Payload), launcher: launcher, detect: () => state);
        var verified = installer.DownloadAsync(null, CancellationToken.None).GetAwaiter().GetResult();
        state = installed;
        var error = Assert.Throws<SandboxieInstallerException>(() => installer.RunAsync(verified, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Equal(SandboxieInstallerFailure.Refused, error.Kind);
        Assert.Equal(0, launcher.Calls);
        AssertNoWorkDirectories(root);

        var throwing = Create(root, handler, detect: () => throw new UnauthorizedAccessException("registry"));
        Assert.Equal(SandboxieInstallerFailure.Refused, Assert.Throws<SandboxieInstallerException>(() =>
            throwing.DownloadAsync(null, CancellationToken.None).GetAwaiter().GetResult()).Kind);
    }

    private static void ControllerSeparatesSandboxieConditions()
    {
        var path = @"C:\Program Files\Sandboxie-Plus\SandMan.exe";
        var cases = new (DependencyInfo Sandboxie, Readiness State, string Text)[]
        {
            (Missing(), Readiness.MissingDependency, "설치되어 있지 않습니다"),
            (new("Sandboxie Plus", path, "1.18.4", "서명 없음", false) { Condition = DependencyCondition.SignatureInvalid }, Readiness.MissingDependency, "공식 서명"),
            (Missing() with { Version = "", Condition = DependencyCondition.PathUnresolved }, Readiness.MissingDependency, "설치 위치"),
            (new("Sandboxie Plus", path, "1.19.0", "다름", true) { Condition = DependencyCondition.UnsupportedVersion }, Readiness.Blocked, "자동으로 바꾸지 않습니다"),
            (new("Sandboxie Plus", path, "1.18.4", "Sandboxie 서비스(SbieSvc)가 실행 중이 아닙니다.", true) { Condition = DependencyCondition.ServiceUnavailable }, Readiness.Blocked, "시작하거나 재시작하지 않습니다"),
            (new("Sandboxie Plus", path, "1.18.4", "설치됨", true) { Condition = DependencyCondition.Verified }, Readiness.SetupRequired, "")
        };
        foreach (var (sandboxie, state, text) in cases)
        {
            var store = new ProfileStore(TestContext.CreateRunDirectory("sbie-controller"));
            var runtime = new ConditionRuntime(sandboxie);
            using var controller = new SidetalkController(store, new RefusingRunner(), runtime, new RefusingRecovery());
            var snapshot = controller.InspectAsync().GetAwaiter().GetResult();
            Assert.Equal(state, snapshot.State);
            Assert.Equal(sandboxie.Condition, snapshot.Sandboxie.Condition);
            Assert.False(snapshot.CanLaunch || snapshot.CanStop, $"{sandboxie.Condition} must not permit commands.");
            if (text.Length > 0) Assert.Contains(text, snapshot.Detail);
            Assert.Equal(0, runtime.OpenCount);
            Assert.False(File.Exists(store.ProfilePath), "Inspection must not create a profile.");
        }
    }

    private static SandboxieInstaller Create(string root, FakeHandler handler, ISandboxieInstallerVerifier? verifier = null,
        FakeLauncher? launcher = null, Func<DependencyInfo>? detect = null, TimeSpan? timeout = null, TimeSpan? stall = null) =>
        new(Release(), handler, verifier ?? new RecordingVerifier(), launcher ?? new FakeLauncher(), detect ?? Missing, root, timeout, stall);

    private static FakeHandler Serve(byte[] content) => new((_, _) => Ok(new ByteArrayContent(content)));
    private static HttpResponseMessage Ok(HttpContent content) => new(HttpStatusCode.OK) { Content = content };
    private static HttpResponseMessage Fail(Exception error) => throw error;
    private static HttpResponseMessage Redirect(Uri location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = location;
        return response;
    }

    private static void AssertNoWorkDirectories(string root) =>
        Assert.Empty(Directory.GetDirectories(root, SandboxieInstaller.WorkDirectoryPrefix + "*"));

    private sealed class SyncProgress(Action<SandboxieDownloadProgress> report) : IProgress<SandboxieDownloadProgress>
    {
        public void Report(SandboxieDownloadProgress value) => report(value);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send) => this.send = (request, token) => Task.FromResult(send(request, token));
        private FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, bool _) => this.send = send;
        public static FakeHandler Async(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(send, true);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    /// <summary>A non-seekable body, so no Content-Length is known; it can fail or stall mid-way.</summary>
    private sealed class ChunkStream(byte[] content, int failAfter = -1, int stallAfter = -1) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (failAfter >= 0 && position >= failAfter) throw new IOException("connection reset");
            if (stallAfter >= 0 && position >= stallAfter) await Task.Delay(Timeout.Infinite, cancellationToken);
            var count = Math.Min(Math.Min(buffer.Length, 4096), content.Length - position);
            content.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingVerifier : ISandboxieInstallerVerifier
    {
        public int Calls { get; private set; }
        public void Verify(string path, SandboxieInstallerRelease release)
        {
            Calls++;
            Assert.True(File.Exists(path), "Verification needs the downloaded file.");
        }
    }

    private sealed class FakeLauncher(Func<string, int>? behaviour = null) : ISandboxieInstallerLauncher
    {
        public int Calls { get; private set; }
        public string? LastPath { get; private set; }
        public bool SawFileWhileRunning { get; private set; }
        public Task<int> RunAsync(string path, CancellationToken cancellationToken)
        {
            Calls++;
            LastPath = path;
            SawFileWhileRunning = File.Exists(path);
            return Task.FromResult((behaviour ?? (_ => 0))(path));
        }
    }

    private sealed class ConditionRuntime(DependencyInfo sandboxie) : ISidetalkRuntime
    {
        public int OpenCount { get; private set; }
        public DependencyInfo Kakao() => new("공식 카카오톡", @"C:\Program Files (x86)\Kakao\KakaoTalk\KakaoTalk.exe", "26.8.0", "시험", true) { Condition = DependencyCondition.Verified };
        public DependencyInfo Sandboxie() => sandboxie;
        public void RequireHost() { }
        public ISidetalkRuntimeSession Open(DependencyInfo dependency)
        {
            OpenCount++;
            throw new InvalidOperationException("Inspection must not open Sandboxie for this condition.");
        }
        public void VerifyCommandFiles(string startExe, string kakaoExe) => throw new InvalidOperationException("No command in this test.");
    }

    private sealed class RefusingRunner : ISidetalkCommandRunner
    {
        public ISidetalkCommandHandle Start(ProcessStartInfo command) => throw new InvalidOperationException("No command in this test.");
    }

    private sealed class RefusingRecovery : ICommandProcessRecovery
    {
        public ISidetalkCommandHandle Recover(CommandProcessIdentity identity) => throw new InvalidOperationException("No recovery in this test.");
        public ISidetalkCommandHandle RecoverUnidentified(string expectedStartExe) => throw new InvalidOperationException("No recovery in this test.");
    }
}
