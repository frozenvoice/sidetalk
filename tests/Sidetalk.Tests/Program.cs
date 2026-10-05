using System.Text;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (args[0] == "--windows-recovery-integration")
                return WindowsRecoveryIntegration.Run(args);
            if (args.Length == 3 && args[0] == "--sandboxie-readonly")
                return NativeConfigurationProbe.Run(args[1], args[2]);
            Console.Error.WriteLine("Usage: --sandboxie-readonly <installation-directory> <new-report.json>");
            return 2;
        }
        var tests = new (string Name, Action Run)[]
        {
            ("ProfileStore creates and reloads the same profile", ProfileStoreCreatesAndReloads),
            ("ProfileStore refuses to recreate a corrupt profile", CorruptProfileRefusesRecreation),
            ("ProfileStore rejects foreign or injected profile identity", ProfileValidationRejectsForeignAndInjectedValues),
            ("IsolationPolicy accepts only the expected owned configuration", IsolationPolicyAcceptsExpectedConfiguration),
            ("IsolationPolicy rejects missing ownership and storage root", IsolationPolicyRejectsMissingOwnerOrRoot),
            ("IsolationPolicy rejects unsafe and duplicate values", IsolationPolicyRejectsUnsafeAndDuplicateValues),
            ("IsolationPolicy rejects global access and template settings", IsolationPolicyRejectsGlobalAccessAndTemplates),
            ("IsolationPolicy rejects unknown box settings", IsolationPolicyRejectsUnknownBoxSettings),
            ("IsolationPolicy rejects INI path CRLF injection", IsolationPolicyRejectsIniPathInjection),
            ("BuiltinTemplates fingerprints ignore ordering", BuiltinTemplatesFingerprintIgnoresOrdering),
            ("BuiltinTemplates fingerprints detect mutations", BuiltinTemplatesFingerprintDetectsMutations),
            ("RuntimeVerifier matches only the approved component versions", RuntimeVerifierMatchesApprovedVersions),
            ("RuntimeVerifier accepts only the official mixed driver matrix", RuntimeVerifierMatchesDriverMatrix),
            ("Sandboxie commands produce exact launch and stop arguments", SandboxieCommandsProduceExactArguments),
            ("Sandboxie commands reject non-SIDETALK paths and box names", SandboxieCommandsRejectInvalidInputs)
        };

        var failures = 0;
        var allTests = tests.Concat(ConfigurationRegressionTests.Cases).Concat(CommandLifecycleTests.Cases).Concat(ServiceIdentityTests.Cases).Concat(ProfileStoreAtomicTests.Cases).Concat(CommandJournalTests.Cases).Concat(ControllerRecoveryTests.Cases).Concat(ControllerSetupRecoveryTests.Cases).Concat(SetupRecoveryTests.Cases).Concat(RecoveryTemplateRulesTests.Cases).Concat(SandboxieIniSnapshotTests.Cases).Concat(SandboxieServiceReplyTests.Cases).Concat(SandboxieSettingCommandsTests.Cases).Concat(SetupRecoverySafetyTests.Cases).Concat(SetupRecoveryJournalTests.Cases).Concat(WindowsSetupRecoveryTokenTests.Cases).Concat(InspectionFailureTests.Cases).Concat(Net8PersistenceCompatibilityTests.Cases).Concat(AuthenticodeCompatibilityTests.Cases).ToArray();
        allTests = allTests.Concat(QuickLaunchTests.Cases).Concat(UserLaunchIntegrationTests.Cases).Concat(SecondaryAppearanceTests.Cases).ToArray();
        foreach (var test in allTests)
        {
            try
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}");
            }
        }

        Console.WriteLine($"{allTests.Length - failures}/{allTests.Length} tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void ProfileStoreCreatesAndReloads()
    {
        var root = TestContext.CreateRunDirectory("profile-persistence");
        var firstStore = new ProfileStore(root);
        var created = firstStore.Create();

        Assert.Equal(Path.GetFullPath(root), firstStore.Root);
        Assert.True(created.BoxName.Length <= 32, "Generated Sandboxie box names must be at most 32 characters.");
        Assert.True(File.Exists(firstStore.ProfilePath), "Create must persist profile.json.");
        Assert.Equal(created, firstStore.Load());
        Assert.Equal(created, firstStore.Create());

        var secondStore = new ProfileStore(root);
        Assert.Equal(created, secondStore.Load());
        Assert.Equal(created, secondStore.Create());
    }

    private static void CorruptProfileRefusesRecreation()
    {
        var root = TestContext.CreateRunDirectory("profile-corrupt");
        var store = new ProfileStore(root);
        Directory.CreateDirectory(root);
        var corrupt = "{ this is not a SIDETALK profile";
        File.WriteAllText(store.ProfilePath, corrupt, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.Throws<Exception>(() => store.Load());
        Assert.Equal(corrupt, File.ReadAllText(store.ProfilePath, Encoding.UTF8));
        Assert.Throws<Exception>(() => store.Create());
        Assert.Equal(corrupt, File.ReadAllText(store.ProfilePath, Encoding.UTF8));
    }

    private static void ProfileValidationRejectsForeignAndInjectedValues()
    {
        var root = TestContext.CreateRunDirectory("profile-validation");
        var store = new ProfileStore(root);
        var profile = store.Create();

        var invalidProfiles = new[]
        {
            ("foreign SID", profile with { UserSid = profile.UserSid + "-foreign" }),
            ("foreign storage", profile with { StoragePath = Path.Combine(root, "other-box") }),
            ("injected box name", profile with { BoxName = profile.BoxName + "\r\nOther=1" }),
            ("box name LF injection", profile with { BoxName = profile.BoxName + "\n" }),
            ("injected owner token", profile with { OwnerToken = profile.OwnerToken + "\r\nOther=1" }),
            ("owner token LF injection", profile with { OwnerToken = profile.OwnerToken + "\n" }),
            ("wrong box name", profile with { BoxName = "SideTalk_" + new string('0', 24) }),
            ("malformed owner token", profile with { OwnerToken = "not-a-token" })
        };

        foreach (var invalid in invalidProfiles)
            Assert.Throws<InvalidDataException>(() => store.Validate(invalid.Item2), invalid.Item1);
    }

    private static void IsolationPolicyAcceptsExpectedConfiguration()
    {
        var profile = TestContext.CreateProfile("policy-accepted");
        var expected = IsolationPolicy.Expected(profile);

        Assert.True(expected.TryGetValue("BoxInfo", out var ownerInfo), "The owner marker must use BoxInfo.");
        Assert.SequenceEqual(new[] { $"SIDETALK:{profile.OwnerToken}:policy=2" }, ownerInfo!);
        Assert.False(expected.ContainsKey("SidetalkOwner"), "The retired owner setting must not be emitted.");
        Assert.False(expected.ContainsKey("SidetalkPolicy"), "The retired policy setting must not be emitted.");

        var result = IsolationPolicy.Validate(profile, Copy(expected), new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase));
        Assert.True(result.Owned, "The expected owner marker and root must establish ownership.");
        Assert.True(result.Safe, string.Join("; ", result.Issues));
        Assert.Empty(result.Issues);

        var ini = IsolationPolicy.ToIni(profile);
        Assert.Contains($"[{profile.BoxName}]", ini);
        Assert.Contains($"BoxInfo=SIDETALK:{profile.OwnerToken}:policy=2", ini);
        Assert.False(ini.Contains("SidetalkOwner=", StringComparison.Ordinal));
        Assert.False(ini.Contains("SidetalkPolicy=", StringComparison.Ordinal));
    }

    private static void IsolationPolicyRejectsMissingOwnerOrRoot()
    {
        var profile = TestContext.CreateProfile("policy-required");
        var expected = IsolationPolicy.Expected(profile);

        var withoutOwner = Copy(expected);
        withoutOwner.Remove("BoxInfo");
        var ownerResult = IsolationPolicy.Validate(profile, withoutOwner, EmptyGlobals());
        Assert.False(ownerResult.Owned);
        Assert.False(ownerResult.Safe);
        Assert.IssueContains(ownerResult, "BoxInfo");

        var withoutRoot = Copy(expected);
        withoutRoot.Remove("FileRootPath");
        var rootResult = IsolationPolicy.Validate(profile, withoutRoot, EmptyGlobals());
        Assert.False(rootResult.Owned);
        Assert.False(rootResult.Safe);
        Assert.IssueContains(rootResult, "FileRootPath");

        var foreignOwner = Copy(expected);
        foreignOwner["BoxInfo"] = [$"SIDETALK:{new string('f', 32)}:policy=2"];
        var foreignOwnerResult = IsolationPolicy.Validate(profile, foreignOwner, EmptyGlobals());
        Assert.False(foreignOwnerResult.Owned);
        Assert.False(foreignOwnerResult.Safe);
        Assert.IssueContains(foreignOwnerResult, "BoxInfo");
    }

    private static void IsolationPolicyRejectsUnsafeAndDuplicateValues()
    {
        var profile = TestContext.CreateProfile("policy-unsafe");
        var expected = IsolationPolicy.Expected(profile);

        var autoDelete = Copy(expected);
        autoDelete["AutoDelete"] = ["y"];
        var deleteResult = IsolationPolicy.Validate(profile, autoDelete, EmptyGlobals());
        Assert.False(deleteResult.Safe);
        Assert.IssueContains(deleteResult, "AutoDelete");

        var duplicate = Copy(expected);
        duplicate["Enabled"] = ["y", "y"];
        var duplicateResult = IsolationPolicy.Validate(profile, duplicate, EmptyGlobals());
        Assert.False(duplicateResult.Safe);
        Assert.IssueContains(duplicateResult, "Enabled");
    }

    private static void IsolationPolicyRejectsGlobalAccessAndTemplates()
    {
        var profile = TestContext.CreateProfile("policy-global");
        var expected = IsolationPolicy.Expected(profile);

        var globalAccess = EmptyGlobals();
        globalAccess["OpenFilePath"] = [Path.Combine(TestContext.WorkspaceRoot, "outside.txt")];
        var accessResult = IsolationPolicy.Validate(profile, Copy(expected), globalAccess);
        Assert.False(accessResult.Safe);
        Assert.IssueContains(accessResult, "OpenFilePath");

        var globalTemplate = EmptyGlobals();
        globalTemplate["Template"] = ["DefaultBox"];
        var templateResult = IsolationPolicy.Validate(profile, Copy(expected), globalTemplate);
        Assert.False(templateResult.Safe);
        Assert.IssueContains(templateResult, "Template");
    }

    private static void IsolationPolicyRejectsUnknownBoxSettings()
    {
        var profile = TestContext.CreateProfile("policy-unknown");
        var box = Copy(IsolationPolicy.Expected(profile));
        box["OpenFilePath"] = [Path.Combine(TestContext.WorkspaceRoot, "outside.txt")];
        box["UnknownSetting"] = ["anything"];

        var result = IsolationPolicy.Validate(profile, box, EmptyGlobals());
        Assert.False(result.Safe);
        Assert.IssueContains(result, "OpenFilePath");
        Assert.IssueContains(result, "UnknownSetting");
    }

    private static void IsolationPolicyRejectsIniPathInjection()
    {
        var profile = TestContext.CreateProfile("policy-crlf");
        var injectedPath = Path.Combine(TestContext.WorkspaceRoot, "artifacts", "tests", "Injected\r\nFileRootPath=C:\\outside");
        var injectedProfile = profile with { StoragePath = injectedPath };

        Assert.Throws<InvalidDataException>(() => IsolationPolicy.Expected(injectedProfile));
        Assert.Throws<InvalidDataException>(() => IsolationPolicy.ToIni(injectedProfile));
    }

    private static void BuiltinTemplatesFingerprintIgnoresOrdering()
    {
        var first = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Gamma"] = ["value-two", "value-one"],
            ["Alpha"] = ["enabled"]
        };
        var reordered = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alpha"] = ["enabled"],
            ["Gamma"] = ["value-one", "value-two"]
        };

        Assert.Equal(BuiltinTemplates.Fingerprint(first), BuiltinTemplates.Fingerprint(reordered));
    }

    private static void BuiltinTemplatesFingerprintDetectsMutations()
    {
        var original = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Gamma"] = ["value-two", "value-one"],
            ["Alpha"] = ["enabled"]
        };
        var changed = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Gamma"] = ["value-two", "value-three"],
            ["Alpha"] = ["enabled"]
        };

        Assert.NotEqual(BuiltinTemplates.Fingerprint(original), BuiltinTemplates.Fingerprint(changed));
    }

    private static void RuntimeVerifierMatchesDriverMatrix()
    {
        Assert.True(RuntimeVerifier.MatchesComponentVersion("5.73.2", driver: true));
        Assert.True(RuntimeVerifier.MatchesComponentVersion("5.73.2.0", driver: true));
        Assert.True(RuntimeVerifier.MatchesComponentVersion("5.73.4", driver: false));
        Assert.False(RuntimeVerifier.MatchesComponentVersion("5.73.4", driver: true));
        Assert.False(RuntimeVerifier.MatchesComponentVersion("5.73.2", driver: false));
        Assert.False(RuntimeVerifier.MatchesComponentVersion("5.73.2.1", driver: true));
        Assert.False(RuntimeVerifier.MatchesComponentVersion("5.73.20", driver: true));
    }
    private static void RuntimeVerifierMatchesApprovedVersions()
    {
        var cases = new[]
        {
            (Actual: "1.18.4", Expected: "1.18.4", Matches: true),
            (Actual: "1.18.4.0", Expected: "1.18.4", Matches: true),
            (Actual: "  1.18.4  ", Expected: "1.18.4", Matches: true),
            (Actual: "1.18.40", Expected: "1.18.4", Matches: false),
            (Actual: "1.18.4.1", Expected: "1.18.4", Matches: false),
            (Actual: "5.73.4", Expected: "1.18.4", Matches: false),
            (Actual: "invalid", Expected: "1.18.4", Matches: false),
            (Actual: "5.73.4", Expected: "5.73.4", Matches: true)
        };

        foreach (var test in cases)
            Assert.Equal(test.Matches, RuntimeVerifier.MatchesVersion(test.Actual, test.Expected));
    }

    private static void SandboxieCommandsProduceExactArguments()
    {
        var root = TestContext.CreateRunDirectory("commands");
        var startExe = Path.Combine(root, "Sandboxie", "Start.exe");
        var kakaoExe = Path.Combine(root, "Kakao", "KakaoTalk.exe");
        var boxName = "SideTalk_0123456789abcdef0123456";

        var launch = SandboxieCommands.Launch(startExe, boxName, kakaoExe);
        Assert.Equal(startExe, launch.FileName);
        Assert.SequenceEqual(new[] { "/silent", "/box:" + boxName, kakaoExe }, launch.ArgumentList);
        Assert.Equal(Path.GetDirectoryName(kakaoExe), launch.WorkingDirectory);
        Assert.False(launch.UseShellExecute);
        Assert.True(launch.CreateNoWindow);

        var stop = SandboxieCommands.Stop(startExe, boxName);
        Assert.Equal(startExe, stop.FileName);
        Assert.SequenceEqual(new[] { "/silent", "/box:" + boxName, "/terminate" }, stop.ArgumentList);
        Assert.Equal(Path.GetDirectoryName(startExe), stop.WorkingDirectory);
        Assert.False(stop.UseShellExecute);
        Assert.True(stop.CreateNoWindow);
    }

    private static void SandboxieCommandsRejectInvalidInputs()
    {
        var root = TestContext.CreateRunDirectory("commands-invalid");
        var startExe = Path.Combine(root, "Sandboxie", "Start.exe");
        var kakaoExe = Path.Combine(root, "Kakao", "KakaoTalk.exe");
        var boxName = "SideTalk_0123456789abcdef0123456";

        Assert.Throws<ArgumentException>(() => SandboxieCommands.Launch("Start.exe", boxName, kakaoExe), "relative Start.exe");
        Assert.Throws<ArgumentException>(() => SandboxieCommands.Launch(Path.Combine(root, "Sandboxie", "Other.exe"), boxName, kakaoExe), "wrong Start.exe name");
        Assert.Throws<ArgumentException>(() => SandboxieCommands.Launch(startExe, "SideTalk_0123456789ABCDEF0123456", kakaoExe), "uppercase box name");
        Assert.Throws<ArgumentException>(() => SandboxieCommands.Stop(startExe, "SideTalk_0123456789abcdef0123456\r\nOther"), "injected box name");
        Assert.Throws<ArgumentException>(() => SandboxieCommands.Launch(startExe, boxName, "KakaoTalk.exe"), "relative KakaoTalk.exe");
        Assert.Throws<ArgumentException>(() => SandboxieCommands.Launch(startExe, boxName, Path.Combine(root, "Kakao", "Other.exe")), "wrong KakaoTalk.exe name");
    }

    private static Dictionary<string, string[]> Copy(IReadOnlyDictionary<string, string[]> source) =>
        source.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string[]> EmptyGlobals() => new(StringComparer.OrdinalIgnoreCase);
}

internal static class TestContext
{
    public static string WorkspaceRoot { get; } = FindWorkspaceRoot();

    public static string CreateRunDirectory(string name)
    {
        var path = Path.Combine(WorkspaceRoot, "artifacts", "tests", $"run-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}", name);
        Directory.CreateDirectory(path);
        return path;
    }

    public static BoxProfile CreateProfile(string name)
    {
        var store = new ProfileStore(CreateRunDirectory(name));
        return store.Create();
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PRODUCT.md")) && Directory.Exists(Path.Combine(directory.FullName, "src")))
                return directory.FullName;

        return Path.GetFullPath(Directory.GetCurrentDirectory());
    }
}

internal static class Assert
{
    public static void True(bool condition, string message = "Expected condition to be true.")
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void False(bool condition, string message = "Expected condition to be false.") => True(!condition, message);

    public static void Empty<T>(IReadOnlyCollection<T> values)
    {
        if (values.Count != 0) throw new InvalidOperationException($"Expected an empty collection, got {values.Count} items.");
    }

    public static void Equal<T>(T expected, T? actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    public static void NotEqual<T>(T first, T second)
    {
        if (EqualityComparer<T>.Default.Equals(first, second))
            throw new InvalidOperationException($"Expected values to differ, both were {first}.");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException($"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }

    public static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected text to contain {expectedSubstring}.");
    }

    public static void IssueContains(PolicyResult result, string key)
    {
        if (!result.Issues.Any(issue => issue.Contains(key, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Expected policy issues to mention {key}: {string.Join("; ", result.Issues)}");
    }

    public static TException Throws<TException>(Action action, string? label = null) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException error)
        {
            return error;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Expected {typeof(TException).Name}{Suffix(label)}, got {error.GetType().Name}.", error);
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}{Suffix(label)}.");
    }

    private static string Suffix(string? label) => label is null ? string.Empty : $" for {label}";
}
