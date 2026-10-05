using System.Text;
using System.Text.Json;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class SecondaryAppearanceTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases { get; } =
    [
        ("Secondary appearance: preserves existing choice, identity and unrelated settings", PreserveAndPersist),
        ("Secondary appearance: strict cosmetics never permit access policy changes", StrictCosmetics),
        ("Secondary appearance: stale approval and running box or manager never write", Guards),
        ("Secondary appearance: backup failure preserves intent without external writes", BackupFailure),
        ("Secondary appearance: write and postcheck failures retain evidence for explicit retry", WriteFailures),
        ("Secondary appearance: completion failure does not report success or repeat writes", CompletionFailure),
        ("Secondary appearance: timeout and unrelated setting races stay blocked", UncertainAndChangedScope),
        ("Secondary appearance: damaged backup or completion is preserved and rejected", CorruptEvidence)
    ];

    private static void PreserveAndPersist()
    {
        var h = new Harness();
        h.Environment.Box["BorderColor"] = ["#F6823B,on,6,192,in,6"];
        h.Environment.Box["BoxAlias"] = ["기존 표시"];
        var before = File.ReadAllBytes(h.Store.ProfilePath);
        var initial = h.Service().Inspect();
        Assert.Equal("#3B82F6", initial.ColorRgb);
        Assert.Equal("기존 표시", initial.Label);
        Assert.False(Directory.Exists(Path.Combine(h.Store.Root, "secondary-appearance")));
        Assert.SequenceEqual(h.Environment.Box["BorderColor"], IsolationPolicy.ExpectedForRecovery(h.Profile, h.Environment.Box)["BorderColor"]);
        var result = h.Service().Apply(initial.Plan!.WithSelection("#22C55E", SecondaryAppearance.SecondaryLabel));
        Assert.Equal("#22C55E", result.ColorRgb);
        Assert.Equal("보조 계정", result.Label);
        Assert.Equal(2, h.Environment.Writes);
        Assert.Equal("#5EC522,on,6,192,in,6", h.Environment.Box["BorderColor"].Single());
        Assert.True(IsolationPolicy.Validate(h.Profile, h.Environment.Box, h.Environment.Globals).Safe);
        SecondaryAppearanceStore.RequireResolved(h.Store);
        Assert.Equal(result.ColorRgb, h.Service().Inspect().ColorRgb);
        Assert.SequenceEqual(before, File.ReadAllBytes(h.Store.ProfilePath));
        Assert.Equal("sentinel", File.ReadAllText(Path.Combine(h.Store.Root, "sentinel.txt")));
        Assert.Equal("unchanged", h.Environment.Other["BoxInfo"].Single());
        Assert.Equal("y", h.Environment.Box["BoxNameTitle"].Single());
        Assert.Equal("2", IsolationPolicy.Revision);

        var preserveAlias = new Harness();
        preserveAlias.Environment.Box["BoxAlias"] = ["기존 표시"];
        var colorOnly = preserveAlias.Service().Inspect().Plan!.WithSelection("#A855F7", null);
        var colorOnlyResult = preserveAlias.Service().Apply(colorOnly);
        Assert.Equal("기존 표시", colorOnlyResult.Label);
        Assert.Equal(1, preserveAlias.Environment.Writes);
    }

    private static void StrictCosmetics()
    {
        var h = new Harness();
        Assert.Equal("#5B6821", h.Service().Inspect().ColorRgb);
        foreach (var invalid in new[] { "#123456,off,4", "#123456,on,0", "#123456,on,4,256,in", "#123456,on,4\r\nOpenFilePath=*", "#123456,on,4,192,unknown" })
        {
            h.Environment.Box["BorderColor"] = [invalid];
            Assert.False(IsolationPolicy.Validate(h.Profile, h.Environment.Box, h.Environment.Globals).Safe);
        }
        h.Environment.Box["BorderColor"] = [SecondaryAppearance.DefaultBorder, SecondaryAppearance.DefaultBorder];
        Assert.False(IsolationPolicy.Validate(h.Profile, h.Environment.Box, h.Environment.Globals).Safe);
        h.Environment.Box["BorderColor"] = ["#5EC522,all,4,192,in"];
        h.Environment.Box["OpenFilePath"] = ["*"];
        Assert.False(IsolationPolicy.Validate(h.Profile, h.Environment.Box, h.Environment.Globals).Safe);
        h.Environment.Box.Remove("OpenFilePath");
        h.Environment.Box["BoxNameTitle"] = ["n"];
        Assert.False(IsolationPolicy.Validate(h.Profile, h.Environment.Box, h.Environment.Globals).Safe);
        Assert.Throws<ArgumentException>(() => new SecondaryAppearancePlan("scope", "#123456").WithSelection("#123456", "arbitrary name"));
    }

    private static void Guards()
    {
        var h = new Harness();
        var plan = h.Service().Inspect().Plan!.WithSelection("#22C55E");
        h.Environment.Globals["NotifyStartRunAccessDenied"] = ["y"];
        Assert.Throws<InvalidDataException>(() => h.Service().Apply(plan));
        h.Environment.Globals.Clear();
        h.Environment.BoxPids = [42];
        Assert.False(h.Service().Inspect().CanApply);
        Assert.Throws<InvalidOperationException>(() => h.Service().Apply(plan));
        h.Environment.BoxPids = [];
        h.Environment.Fresh = false;
        Assert.False(h.Service().Inspect().CanApply);
        Assert.Throws<InvalidOperationException>(() => h.Service().Apply(plan));
        Assert.Equal(0, h.Environment.Writes);
        Assert.False(Directory.Exists(Path.Combine(h.Store.Root, "secondary-appearance")));
    }

    private static void BackupFailure()
    {
        var h = new Harness();
        var plan = h.Service().Inspect().Plan!.WithSelection("#22C55E");
        Assert.Throws<IOException>(() => h.Service(path => { if (path.EndsWith(".ini.backup", StringComparison.Ordinal)) throw new IOException("backup failed"); }).Apply(plan));
        Assert.Equal(0, h.Environment.Writes);
        Assert.Throws<InvalidDataException>(() => SecondaryAppearanceStore.RequireResolved(h.Store));
        var intent = Directory.EnumerateFiles(Path.Combine(h.Store.Root, "secondary-appearance"), "*.intent.json").Single();
        var original = File.ReadAllBytes(intent);
        h.Service().Apply(h.Service().Inspect().Plan!);
        Assert.SequenceEqual(original, File.ReadAllBytes(intent));
        SecondaryAppearanceStore.RequireResolved(h.Store);
    }

    private static void WriteFailures()
    {
        foreach (var after in new[] { false, true })
        {
            var h = new Harness();
            var original = File.ReadAllBytes(h.Store.ProfilePath);
            var plan = h.Service().Inspect().Plan!.WithSelection("#22C55E", SecondaryAppearance.SecondaryLabel);
            h.Environment.FailBefore = !after;
            h.Environment.FailAfter = after;
            Assert.Throws<InvalidOperationException>(() => h.Service().Apply(plan));
            Assert.Throws<InvalidDataException>(() => SecondaryAppearanceStore.RequireResolved(h.Store));
            var directory = Path.Combine(h.Store.Root, "secondary-appearance");
            var backup = Directory.EnumerateFiles(directory, "*.ini.backup").Single();
            var bytes = File.ReadAllBytes(backup);
            Assert.Equal(0, Directory.EnumerateFiles(directory, "*.completed.json").Count());
            Assert.Equal(1, Directory.EnumerateFiles(directory, "*.failure.*.json").Count());
            h.Environment.FailBefore = h.Environment.FailAfter = false;
            h.Service().Apply(h.Service().Inspect().Plan!);
            SecondaryAppearanceStore.RequireResolved(h.Store);
            Assert.SequenceEqual(bytes, File.ReadAllBytes(backup));
            Assert.SequenceEqual(original, File.ReadAllBytes(h.Store.ProfilePath));
            Assert.Equal("보조 계정", h.Environment.Box["BoxAlias"].Single());
        }

        var failedRecord = new Harness();
        failedRecord.Environment.FailBefore = true;
        var failedPlan = failedRecord.Service().Inspect().Plan!.WithSelection("#22C55E");
        Assert.Throws<InvalidOperationException>(() => failedRecord.Service(path =>
        {
            if (path.Contains(".failure.", StringComparison.Ordinal)) throw new IOException("failure publication failed");
        }).Apply(failedPlan));
        var failedDirectory = Path.Combine(failedRecord.Store.Root, "secondary-appearance");
        Assert.Equal(1, Directory.EnumerateFiles(failedDirectory, "*.intent.json").Count());
        Assert.Equal(1, Directory.EnumerateFiles(failedDirectory, "*.ini.backup").Count());
        Assert.Equal(0, Directory.EnumerateFiles(failedDirectory, "*.completed.json").Count());
        Assert.Throws<InvalidDataException>(() => SecondaryAppearanceStore.RequireResolved(failedRecord.Store));
        Assert.Equal(0, failedRecord.Environment.Writes);
    }

    private static void CompletionFailure()
    {
        var h = new Harness();
        var plan = h.Service().Inspect().Plan!.WithSelection("#22C55E");
        Assert.Throws<InvalidOperationException>(() => h.Service(path => { if (path.EndsWith(".completed.json", StringComparison.Ordinal)) throw new IOException("completion failed"); }).Apply(plan));
        Assert.Equal(1, h.Environment.Writes);
        Assert.Throws<InvalidDataException>(() => SecondaryAppearanceStore.RequireResolved(h.Store));
        var retry = h.Service().Inspect();
        Assert.True(retry.CanApply);
        h.Service().Apply(retry.Plan!);
        Assert.Equal(1, h.Environment.Writes);
        SecondaryAppearanceStore.RequireResolved(h.Store);
    }

    private static void UncertainAndChangedScope()
    {
        var h = new Harness();
        h.Environment.Timeout = true;
        Assert.Throws<InvalidOperationException>(() => h.Service().Apply(h.Service().Inspect().Plan!.WithSelection("#22C55E")));
        h.Environment.Fresh = false;
        Assert.False(h.Service().Inspect().CanApply);
        Assert.Throws<InvalidDataException>(() => SecondaryAppearanceStore.RequireResolved(h.Store));
        h.Environment.Fresh = true;
        h.Environment.Timeout = false;
        h.Environment.Other["BoxInfo"] = ["changed foreign box"];
        Assert.False(h.Service().Inspect().CanApply);
        Assert.True(h.Service().Inspect().Plan is null);
        Assert.Equal(0, h.Environment.Writes);
    }

    private static void CorruptEvidence()
    {
        var h = new Harness();
        h.Environment.FailBefore = true;
        Assert.Throws<InvalidOperationException>(() => h.Service().Apply(h.Service().Inspect().Plan!.WithSelection("#22C55E")));
        var directory = Path.Combine(h.Store.Root, "secondary-appearance");
        var backup = Directory.EnumerateFiles(directory, "*.ini.backup").Single();
        File.AppendAllText(backup, "tamper");
        var damaged = File.ReadAllBytes(backup);
        h.Environment.FailBefore = false;
        Assert.Throws<InvalidOperationException>(() => h.Service().Apply(h.Service().Inspect().Plan!));
        Assert.SequenceEqual(damaged, File.ReadAllBytes(backup));
        Assert.Equal(0, h.Environment.Writes);
        var intent = Directory.EnumerateFiles(directory, "*.intent.json").Single();
        var completed = intent.Replace(".intent.json", ".completed.json", StringComparison.Ordinal);
        File.WriteAllText(completed, "{");
        Assert.Throws<JsonException>(() => SecondaryAppearanceStore.RequireResolved(h.Store));
        Assert.Equal("{", File.ReadAllText(completed));

        var editedIntent = new Harness();
        editedIntent.Environment.FailBefore = true;
        Assert.Throws<InvalidOperationException>(() => editedIntent.Service().Apply(editedIntent.Service().Inspect().Plan!.WithSelection("#22C55E")));
        var editedPath = Directory.EnumerateFiles(Path.Combine(editedIntent.Store.Root, "secondary-appearance"), "*.intent.json").Single();
        var edited = File.ReadAllText(editedPath).Replace("unchanged", "edited evidence", StringComparison.Ordinal);
        File.WriteAllText(editedPath, edited);
        Assert.Throws<InvalidDataException>(() => SecondaryAppearanceStore.RequireResolved(editedIntent.Store));
        Assert.Equal(edited, File.ReadAllText(editedPath));
        Assert.Equal(0, editedIntent.Environment.Writes);
    }

    private sealed class Harness
    {
        public ProfileStore Store { get; } = new(TestContext.CreateRunDirectory("secondary-appearance"));
        public BoxProfile Profile { get; }
        public EnvironmentFixture Environment { get; }
        public Harness()
        {
            Profile = Store.Create();
            File.WriteAllText(Path.Combine(Store.Root, "sentinel.txt"), "sentinel");
            Environment = new(Profile);
        }
        public SecondaryAppearanceService Service(Action<string>? beforeWrite = null) => new(Store, Environment, beforeWrite);
    }

    private sealed class EnvironmentFixture(BoxProfile profile) : ISecondaryAppearanceEnvironment
    {
        public Dictionary<string, string[]> Box { get; } = IsolationPolicy.Expected(profile);
        public Dictionary<string, string[]> Globals { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string[]> Other { get; } = new(StringComparer.OrdinalIgnoreCase) { ["BoxInfo"] = ["unchanged"] };
        public int Writes { get; private set; }
        public int[] BoxPids { get; set; } = [];
        public bool Fresh { get; set; } = true;
        public bool FailBefore { get; set; }
        public bool FailAfter { get; set; }
        public bool Timeout { get; set; }

        public SetupRecoveryObservation Inspect(ProfileStore store, BoxProfile expected)
        {
            Assert.Equal(profile, expected);
            var box = Copy(Box); var globals = Copy(Globals);
            var sections = new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
            { [profile.BoxName] = box, ["GlobalSettings"] = globals, ["ForeignBox"] = Copy(Other) };
            return new(Path.Combine(store.Root, "fake-sandboxie.ini"), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sections)),
                new(box, box, globals, BuiltinTemplates.ExpectedGlobals(globals)), sections, [], profile.BoxName, profile.UserSid,
                profile.StoragePath, ["ForeignBox"], [], BoxPids.ToArray(), true,
                new(Path.Combine(store.Root, "install"), Path.Combine(store.Root, "install", "Start.exe"), RuntimeVerifier.PlusVersion, true, "synthetic"),
                new(Fresh, []), true, true, true, "synthetic", DateTimeOffset.UtcNow);
        }

        public SetupRecoveryObservation ApplyAppearance(ProfileStore store, BoxProfile expected, SetupRecoveryObservation baseline, string key, string value)
        {
            // The fixture never runs SbieIni; prove durability before simulated external mutation.
            var directory = Path.Combine(store.Root, "secondary-appearance");
            Assert.Equal(1, Directory.EnumerateFiles(directory, "*.intent.json").Count());
            Assert.Equal(1, Directory.EnumerateFiles(directory, "*.ini.backup").Count());
            Assert.Throws<InvalidDataException>(() => SecondaryAppearanceStore.RequireResolved(store));
            if (Timeout) throw new TimeoutException("SbieIni exit unknown");
            if (FailBefore) throw new IOException("setting write failed");
            Assert.True(key is "BorderColor" or "BoxAlias");
            Box[key] = [value]; Writes++;
            if (FailAfter) throw new IOException("post-write API check failed");
            return Inspect(store, expected);
        }
        private static Dictionary<string, string[]> Copy(Dictionary<string, string[]> values) => values.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }
}
