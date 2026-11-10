using System.Diagnostics;
using Sidetalk.Core;

namespace Sidetalk.Tests;

/// <summary>Synthetic box folders only; no real KakaoTalk, Sandboxie or account data is read.</summary>
internal static class StaleProgramCopyTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases =
    [
        ("Program copy: mapping follows only the documented drive layout inside the box", MappingStaysInsideBox),
        ("Program copy: only an older Kakao-signed executable is offered", ScanOffersOnlyVerifiedOlderCopy),
        ("Program copy: linked or oversized folders are refused", ScanRefusesLinks),
        ("Program copy: inspection reports it without writing and launch stays available", InspectionReportsWithoutWriting),
        ("Program copy: approved move preserves the copy and never touches account data", SetAsidePreservesEverything),
        ("Program copy: running box, read-only, unsafe policy and stale approval change nothing", BlockedStatesChangeNothing)
    ];

    private const string HostExe = @"C:\Program Files (x86)\Kakao\KakaoTalk\KakaoTalk.exe";

    private static void MappingStaysInsideBox()
    {
        var storage = @"C:\Data\SIDETALK\Boxes\SideTalk_x";
        Assert.Equal(storage + @"\drive\C\Program Files (x86)\Kakao\KakaoTalk", StaleProgramCopy.MappedDirectory(storage, HostExe));
        Assert.Equal(storage + @"\drive\D\Apps\Kakao", StaleProgramCopy.MappedDirectory(storage, @"d:\Apps\Kakao\KakaoTalk.exe"));
        Assert.True(StaleProgramCopy.MappedDirectory(storage, @"\\server\share\Kakao\KakaoTalk.exe") is null, "UNC host paths must not map.");
        Assert.True(StaleProgramCopy.MappedDirectory(storage, @"KakaoTalk.exe") is null, "Relative host paths must not map.");
        Assert.True(StaleProgramCopy.MappedDirectory(storage, @"C:\KakaoTalk.exe") is null, "A drive-root executable has no program folder to map.");
    }

    private static void ScanOffersOnlyVerifiedOlderCopy()
    {
        var h = new Harness("program-copy-scan");
        Assert.True(h.Scan() is null, "No folder must mean nothing to offer.");
        Directory.CreateDirectory(h.CopyDirectory);
        Assert.True(h.Scan() is null, "A folder without KakaoTalk.exe must not be offered.");
        h.WriteCopy();
        var scan = h.Scan() ?? throw new InvalidOperationException("The older signed copy was not found.");
        Assert.Equal(h.CopyDirectory, scan.Directory);
        Assert.Equal("26.8.2.5324", scan.CopyVersion);
        Assert.Equal("26.9.0.5337", scan.HostVersion);
        Assert.Equal(2, scan.Files.Count);
        Assert.Equal(scan.Fingerprint, h.Scan()!.Fingerprint);

        h.Runtime.CopyIdentity = new("26.8.2.5324", false);
        Assert.True(h.Scan() is null, "An executable without the Kakao signature must not be offered.");
        foreach (var version in new[] { "26.9.0.5337", "26.10.0.1", "알 수 없음", "" })
        {
            h.Runtime.CopyIdentity = new(version, true);
            Assert.True(h.Scan() is null, $"Version '{version}' is not older than the host and must not be offered.");
        }
        h.Runtime.CopyIdentity = new("26.8.2.5324", true);
        h.Runtime.KakaoAvailable = false;
        Assert.True(h.Scan() is null, "An unverified host installation must not be compared.");
        h.Runtime.KakaoAvailable = true;
        File.AppendAllText(Path.Combine(h.CopyDirectory, "KakaoTalk.exe"), "changed");
        Assert.False(scan.Fingerprint == h.Scan()!.Fingerprint, "A changed file must change the approval fingerprint.");
    }

    private static void ScanRefusesLinks()
    {
        var h = new Harness("program-copy-links");
        h.WriteCopy();
        var target = Path.Combine(h.Store.Root, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "account.dat"), "must never be listed");
        var link = Path.Combine(h.CopyDirectory, "linked");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
        {
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        try
        {
            Assert.Throws<InvalidDataException>(() => h.Scan());
            using var controller = h.Controller();
            var snapshot = controller.InspectAsync().GetAwaiter().GetResult();
            Assert.True(snapshot.StaleProgramCopy is null, "An untrusted folder must be reported as nothing to offer.");
            Assert.True(snapshot.CanLaunch && snapshot.InspectionFailure is null, "An untrusted copy folder must not fail inspection.");
        }
        finally { Directory.Delete(link); }
    }

    private static void InspectionReportsWithoutWriting()
    {
        var h = new Harness("program-copy-inspect");
        h.WriteCopy();
        var before = h.SnapshotBox();
        using var controller = h.Controller();
        var snapshot = controller.InspectAsync().GetAwaiter().GetResult();
        var copy = snapshot.StaleProgramCopy ?? throw new InvalidOperationException("Inspection did not report the older copy.");
        Assert.True(copy.CanSetAside && snapshot.CanLaunch, "The verified empty state must offer the move and keep launch available.");
        Assert.Equal("26.8.2.5324", copy.CopyVersion);
        Assert.Equal(2, copy.FileCount);
        h.AssertBox(before);
        Assert.False(Directory.Exists(Path.Combine(h.Store.Root, StaleProgramCopy.PreservedFolderName)));
        Assert.Equal(0, h.Runner.Starts);
    }

    private static void SetAsidePreservesEverything()
    {
        var h = new Harness("program-copy-move");
        h.WriteCopy();
        var executable = File.ReadAllBytes(Path.Combine(h.CopyDirectory, "KakaoTalk.exe"));
        var account = File.ReadAllBytes(h.AccountSentinel);
        var profile = File.ReadAllBytes(h.Store.ProfilePath);
        using var controller = h.Controller();
        var approved = controller.InspectAsync().GetAwaiter().GetResult().StaleProgramCopy!;
        var messages = new List<string>();
        var result = controller.SetAsideStaleProgramCopyAsync(approved, new SynchronousProgress(messages.Add)).GetAwaiter().GetResult();

        Assert.True(result.StaleProgramCopy is null && result.CanLaunch, "After the move the box must show no older copy and stay launchable.");
        Assert.Contains("보존 위치", result.RecoveryResultDetail!);
        Assert.False(Directory.Exists(h.CopyDirectory), "The older copy must no longer shadow the host files.");
        var preserved = Directory.GetDirectories(Path.Combine(h.Store.Root, StaleProgramCopy.PreservedFolderName)).Single();
        Assert.SequenceEqual(executable, File.ReadAllBytes(Path.Combine(preserved, "KakaoTalk", "KakaoTalk.exe")));
        Assert.True(File.Exists(Path.Combine(preserved, "KakaoTalk", "resources", "old.dat")), "Nested program files must be preserved.");
        Assert.True(File.Exists(Path.Combine(preserved, "record.json")) && File.Exists(Path.Combine(preserved, "completed.json")),
            "The move must leave its record and completion.");
        Assert.False(File.Exists(Path.Combine(preserved, "failed.json")));
        Assert.SequenceEqual(account, File.ReadAllBytes(h.AccountSentinel));
        Assert.SequenceEqual(profile, File.ReadAllBytes(h.Store.ProfilePath));
        Assert.True(messages.Count >= 2, "Progress was not reported.");
        Assert.Equal(0, h.Runner.Starts);

        // The same approval cannot be replayed once the copy is gone.
        Assert.Throws<InvalidOperationException>(() => controller.SetAsideStaleProgramCopyAsync(approved).GetAwaiter().GetResult());
        Assert.Equal(1, Directory.GetDirectories(Path.Combine(h.Store.Root, StaleProgramCopy.PreservedFolderName)).Length);
    }

    private static void BlockedStatesChangeNothing()
    {
        var h = new Harness("program-copy-blocked");
        h.WriteCopy();
        var before = h.SnapshotBox();
        StaleProgramCopyObservation approved;
        using (var ready = h.Controller())
            approved = ready.InspectAsync().GetAwaiter().GetResult().StaleProgramCopy!;

        using (var readOnly = h.Controller(readOnly: true))
        {
            Assert.False(readOnly.InspectAsync().GetAwaiter().GetResult().StaleProgramCopy!.CanSetAside, "Read-only inspection must not offer the move.");
            Assert.Throws<InvalidOperationException>(() => readOnly.SetAsideStaleProgramCopyAsync(approved).GetAwaiter().GetResult());
        }

        h.Runtime.ProcessIds = [4321];
        using (var running = h.Controller())
        {
            var copy = running.InspectAsync().GetAwaiter().GetResult().StaleProgramCopy!;
            Assert.False(copy.CanSetAside, "A running box must not offer the move.");
            Assert.Contains("종료한 뒤", copy.Guidance);
            Assert.Throws<InvalidOperationException>(() => running.SetAsideStaleProgramCopyAsync(approved).GetAwaiter().GetResult());
        }
        h.Runtime.ProcessIds = [];

        h.Runtime.UnsafePolicy = true;
        using (var unsafePolicy = h.Controller())
        {
            Assert.True(unsafePolicy.InspectAsync().GetAwaiter().GetResult().StaleProgramCopy is null, "An unverified policy must not report or offer the move.");
            Assert.Throws<InvalidDataException>(() => unsafePolicy.SetAsideStaleProgramCopyAsync(approved).GetAwaiter().GetResult());
        }
        h.Runtime.UnsafePolicy = false;

        h.Runtime.WrongRoot = true;
        using (var wrongRoot = h.Controller())
            Assert.Throws<InvalidDataException>(() => wrongRoot.SetAsideStaleProgramCopyAsync(approved).GetAwaiter().GetResult());
        h.Runtime.WrongRoot = false;

        h.Runtime.RejectHost = true;
        using (var wrongHost = h.Controller())
            Assert.Throws<InvalidOperationException>(() => wrongHost.SetAsideStaleProgramCopyAsync(approved).GetAwaiter().GetResult());
        h.Runtime.RejectHost = false;
        h.AssertBox(before);

        // A file that changed after the approval invalidates it.
        File.AppendAllText(Path.Combine(h.CopyDirectory, "resources", "old.dat"), "changed after approval");
        var changed = h.SnapshotBox();
        using (var stale = h.Controller())
            Assert.Throws<InvalidDataException>(() => stale.SetAsideStaleProgramCopyAsync(approved).GetAwaiter().GetResult());
        h.AssertBox(changed);
        Assert.False(Directory.Exists(Path.Combine(h.Store.Root, StaleProgramCopy.PreservedFolderName)), "A refused move must not create preservation folders.");
        Assert.Equal(0, h.Runner.Starts);
    }

    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class Harness
    {
        public ProfileStore Store { get; }
        public BoxProfile Profile { get; }
        public FakeRuntime Runtime { get; }
        public FakeRunner Runner { get; } = new();
        public string CopyDirectory { get; }
        public string AccountSentinel { get; }

        public Harness(string name)
        {
            Store = new ProfileStore(TestContext.CreateRunDirectory(name));
            Profile = Store.Create();
            Runtime = new FakeRuntime(Profile);
            CopyDirectory = Path.Combine(Profile.StoragePath, "drive", "C", "Program Files (x86)", "Kakao", "KakaoTalk");
            AccountSentinel = Path.Combine(Profile.StoragePath, "user", "current", "AppData", "Local", "Kakao", "KakaoTalk", "users", "synthetic", "chat.edb");
            Directory.CreateDirectory(Path.GetDirectoryName(AccountSentinel)!);
            File.WriteAllText(AccountSentinel, "synthetic account sentinel — never real data");
        }

        public void WriteCopy()
        {
            Directory.CreateDirectory(Path.Combine(CopyDirectory, "resources"));
            File.WriteAllText(Path.Combine(CopyDirectory, "KakaoTalk.exe"), "synthetic older executable — never executed");
            File.WriteAllText(Path.Combine(CopyDirectory, "resources", "old.dat"), "synthetic older resource");
        }

        public StaleProgramCopyScan? Scan() => Runtime.ReadStaleProgramCopy(Profile, Runtime.Kakao());
        public SidetalkController Controller(bool readOnly = false) => new(Store, Runner, Runtime, new FakeRecovery(), readOnly: readOnly);

        public Dictionary<string, byte[]> SnapshotBox() => Directory.EnumerateFiles(Profile.StoragePath, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        public void AssertBox(Dictionary<string, byte[]> expected)
        {
            var actual = SnapshotBox();
            Assert.Equal(expected.Count, actual.Count);
            foreach (var (path, bytes) in expected) Assert.SequenceEqual(bytes, actual[path]);
        }
    }

    private sealed class FakeRuntime(BoxProfile profile) : ISidetalkRuntime
    {
        public ProgramFileIdentity CopyIdentity { get; set; } = new("26.8.2.5324", true);
        public bool KakaoAvailable { get; set; } = true;
        public int[] ProcessIds { get; set; } = [];
        public bool UnsafePolicy { get; set; }
        public bool WrongRoot { get; set; }
        public bool RejectHost { get; set; }
        public DependencyInfo Kakao() => new("공식 카카오톡", HostExe, "26.9.0.5337", "synthetic signed host", KakaoAvailable);
        public DependencyInfo Sandboxie() => new("Sandboxie Plus", @"C:\Program Files\Sandboxie-Plus\SandMan.exe", "1.18.4", "synthetic", true);
        public void RequireHost() { if (RejectHost) throw new InvalidOperationException("Unverified host."); }
        public void VerifyCommandFiles(string startExe, string kakaoExe) { }
        public StaleProgramCopyScan? ReadStaleProgramCopy(BoxProfile requested, DependencyInfo hostKakao) =>
            StaleProgramCopy.Scan(requested, hostKakao, _ => CopyIdentity);
        public ISidetalkRuntimeSession Open(DependencyInfo sandboxie) => new Session(this, profile);
        private sealed class Session(FakeRuntime runtime, BoxProfile profile) : ISidetalkRuntimeSession
        {
            public RuntimeVerifier.ServiceVerification ServiceIdentity { get; } = new(45000, false, "synthetic", null);
            public ConfigurationLayers ReadConfiguration(string boxName)
            {
                var box = IsolationPolicy.Expected(profile);
                if (runtime.UnsafePolicy) box["OpenFilePath"] = ["C:\\Users\\*"];
                var globals = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                return new(box, box, globals, BuiltinTemplates.ExpectedGlobals(globals));
            }
            public string FileRoot(string boxName) => runtime.WrongRoot ? profile.StoragePath + "-other" : profile.StoragePath;
            public IReadOnlyList<BoxProcess> Processes(BoxProfile requested) =>
                runtime.ProcessIds.Select(id => new BoxProcess(id, "KakaoTalk.exe", profile.UserSid, 1)).ToArray();
            public void Dispose() { }
        }
    }

    private sealed class FakeRunner : ISidetalkCommandRunner
    {
        public int Starts { get; private set; }
        public ISidetalkCommandHandle Start(ProcessStartInfo command) { Starts++; throw new InvalidOperationException("No process creation expected"); }
    }

    private sealed class FakeRecovery : ICommandProcessRecovery
    {
        public ISidetalkCommandHandle Recover(CommandProcessIdentity identity) => throw new InvalidOperationException("No reconciliation expected");
        public ISidetalkCommandHandle RecoverUnidentified(string expectedStartExe) => throw new InvalidOperationException("No reconciliation expected");
    }
}
