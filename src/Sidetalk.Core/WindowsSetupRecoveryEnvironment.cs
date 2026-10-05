using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Principal;
using System.Text;

namespace Sidetalk.Core;

/// <summary>
/// Windows-only native boundary for setup recovery.  It proves the ordinary-user
/// host, reads the service-selected INI/user section and all explicit sections,
/// and executes only a reviewed SbieIni.exe setting command.
/// </summary>
internal sealed class WindowsSetupRecoveryEnvironment : ISetupRecoveryEnvironment, ISecondaryAppearanceEnvironment
{
    private const string GlobalSection = "GlobalSettings";
    private const string TemplateKey = "Template";
    private const string AutoRunKey = "SbieCtrl_AutoRunSoftCompat";
    private static readonly string[] ManagementNames = ["SbieCtrl", "SandMan"];
    private BoxProfile? lastProfile;
    private ProfileStore? lastStore;

    SetupRecoveryObservation ISecondaryAppearanceEnvironment.Inspect(ProfileStore store, BoxProfile profile) => Inspect(store, profile);

    SetupRecoveryObservation ISecondaryAppearanceEnvironment.ApplyAppearance(ProfileStore store, BoxProfile profile,
        SetupRecoveryObservation baseline, string key, string value)
    {
        if (lastStore != store || lastProfile != profile || store.Load() != profile ||
            baseline.OwnedBoxName != profile.BoxName || !IsolationPolicy.Validate(profile, baseline.Configuration).Safe)
            throw new InvalidDataException("표시 설정의 전용 환경과 현재 정책이 검증된 요청과 다릅니다.");
        if (!(key == "BorderColor" && SecondaryAppearance.IsBorderValue(value)) &&
            !(key == "BoxAlias" && value == SecondaryAppearance.SecondaryLabel))
            throw new InvalidDataException("승인된 테두리 색과 보조 계정 이름만 저장할 수 있습니다.");
        var folder = baseline.Install.SandboxiePath;
        var exe = Path.Combine(folder, "SbieIni.exe");
        // Uses the same verified command path, empty-box and UI guards, and exact
        // one-setting file/API comparison as recovery. It never quiesces a UI.
        return RunVerifiedCommand(exe, folder, ["set", profile.BoxName, key, value], baseline,
            profile.BoxName, key, [value], CancellationToken.None);
    }

    public SetupRecoveryObservation Inspect(ProfileStore store, BoxProfile? profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HostProfileContext.RequireUnredirected(store);
        var sid = ProfileStore.CurrentSid();
        if (profile is not null) store.Validate(profile);
        lastProfile = profile;
        lastStore = store;

        var dependency = DependencyDetector.Sandboxie();
        if (!dependency.Available || dependency.Path is null)
            throw new InvalidOperationException("검토된 Sandboxie Plus 설치를 찾지 못했습니다.");
        var folder = Path.GetDirectoryName(Path.GetFullPath(dependency.Path))
            ?? throw new InvalidDataException("Sandboxie 설치 폴더를 확인하지 못했습니다.");
        var service = RuntimeVerifier.RequireInstallation(folder);
        var startPath = Path.Combine(folder, "Start.exe");
        var sbieIniPath = Path.Combine(folder, "SbieIni.exe");
        RuntimeVerifier.RequireComponent(startPath);
        RuntimeVerifier.RequireComponent(sbieIniPath);
        VerifyManagerFiles(folder);

        using var api = new SandboxieApi(folder);
        var location = api.QueryConfigurationLocation();
        var configPath = RequireActualFile(location.Path);
        var iniBytes = ReadStableBytes(configPath);
        var user = api.QueryUserSettingsSection();
        var userSuffix = user.Section.StartsWith("UserSettings_", StringComparison.OrdinalIgnoreCase) ? user.Section[13..] : string.Empty;
        if (userSuffix.Length != 8 || !userSuffix.All(Uri.IsHexDigit))
            throw new InvalidDataException("공유 UserSettings_Default 또는 검토되지 않은 사용자 섹션은 자동 복구 대상이 아닙니다." );
        var sections = api.ReadAllSections(includeTemplates: false);
        var userSection = sections.TryGetValue(user.Section, out var existingUser)
            ? existingUser
            : new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var reader = new SandboxieConfigurationReader(api.Query);
        RecoveryTemplateRules.Verify(reader);
        var iniAfterApi = ReadStableBytes(configPath);
        if (!iniBytes.SequenceEqual(iniAfterApi)) throw new IOException("Sandboxie INI가 조회 중 변경되었습니다.");
        if (profile is not null && sections.TryGetValue(profile.BoxName, out var ownedSection) && ownedSection.ContainsKey("FileRootPath"))
        {
            var nativeRoot = api.FileRoot(profile.BoxName);
            if (!SandboxieApi.SameNativePath(nativeRoot, profile.StoragePath))
                throw new InvalidDataException("전용 환경의 실제 FileRootPath가 프로필 저장 경로와 다릅니다.");
        }
        SandboxieIniSnapshot.RequireEquivalent(iniBytes, sections);
        cancellationToken.ThrowIfCancellationRequested();

        var global = sections.TryGetValue(GlobalSection, out var explicitGlobal)
            ? explicitGlobal.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var unknownTemplates = global.TryGetValue(TemplateKey, out var selectors)
            ? selectors.Where(name => !RecoveryTemplateRules.IsReviewed(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
        var proposed = new List<SetupRecoveryChange>();
        var desiredTemplates = selectors?.Where(name => !RecoveryTemplateRules.IsReviewed(name)).ToArray() ?? Array.Empty<string>();
        if (selectors is not null && !selectors.SequenceEqual(desiredTemplates, StringComparer.Ordinal))
            proposed.Add(new SetupRecoveryChange(GlobalSection, TemplateKey, selectors.ToArray(), desiredTemplates,
                SetupRecoveryChangeKind.RemoveGlobalTemplate, "검토된 Sandboxie 전역 호환성 템플릿 제거"));

        var currentAutoRun = ReadValue(userSection, AutoRunKey);
        if (!currentAutoRun.SequenceEqual(["n"], StringComparer.Ordinal))
            proposed.Add(new SetupRecoveryChange(user.Section, AutoRunKey, currentAutoRun, ["n"],
                SetupRecoveryChangeKind.SetUserSetting, "SbieCtrl 자동 호환성 팝업 재발 방지"));

        var otherBoxes = sections.Keys.Where(IsBoxSection)
            .Where(name => profile is null || !name.Equals(profile.BoxName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        string? owned = null;
        string? storage = null;
        ConfigurationLayers configuration;
        if (profile is null)
        {
            var effectiveGlobal = api.ReadSection(GlobalSection, includeTemplates: true);
            configuration = new(
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
                global,
                effectiveGlobal);
        }
        else
        {
            configuration = api.ReadConfiguration(profile.BoxName);
            if (IsolationPolicy.Validate(profile, configuration).Owned)
            {
                owned = profile.BoxName;
                storage = profile.StoragePath;
            }
            if (configuration.ExplicitBox.Count != 0 &&
                !sections.TryGetValue(profile.BoxName, out _))
                throw new InvalidDataException("전용 환경 설정 섹션 조회가 일관되지 않습니다.");
        }

        var candidateGlobal = global.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        if (candidateGlobal.TryGetValue(TemplateKey, out var candidateSelectors))
        {
            var remaining = candidateSelectors.Where(name => !RecoveryTemplateRules.IsReviewed(name)).ToArray();
            if (remaining.Length == 0) candidateGlobal.Remove(TemplateKey);
            else candidateGlobal[TemplateKey] = remaining;
        }
        var baselineTemplateSafe = false;
        try
        {
            baselineTemplateSafe = SandboxieConfigurationReader.Same(configuration.ExplicitBox, configuration.EffectiveBox) &&
                SandboxieConfigurationReader.Same(RecoveryTemplateRules.ExpectedGlobals(global), configuration.EffectiveGlobals);
        }
        catch (InvalidDataException) { baselineTemplateSafe = false; }
        var candidateEffectiveGlobal = unknownTemplates.Length == 0
            ? RecoveryTemplateRules.ExpectedGlobals(candidateGlobal)
            : new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var policySafe = baselineTemplateSafe && (profile is null
            ? otherBoxes.Length == 0 && unknownTemplates.Length == 0
            : IsolationPolicy.Validate(profile, new ConfigurationLayers(
                configuration.ExplicitBox,
                configuration.ExplicitBox,
                candidateGlobal,
                candidateEffectiveGlobal)).Safe);
        var processIds = profile is null ? Array.Empty<int>() : api.Processes(profile).Select(process => process.Id).ToArray();
        var management = CaptureManagementProcesses(folder, sid);
        var tools = CaptureActiveSbieIni(folder, sid);
        var activeSbieIni = tools.HasAny;
        var ui = new SetupRecoveryUiState(
            Fresh: !activeSbieIni && !management.HasUnsafe,
            ApprovedManagementProcesses: management.Current,
            CompatibilityDialogObserved: false,
            Detail: activeSbieIni ? "SbieIni.exe 실행 중에는 복구를 진행하지 않습니다."
                : management.HasUnsafe ? "다른 사용자·세션 또는 신원 조회 불가 관리 도구 때문에 공유 설정 복구를 차단했습니다. 현재 격리 정책의 실행 판단은 별도로 확인합니다."
                : "현재 사용자·세션의 관리 UI와 설정 도구 상태를 확인했습니다.")
        { ForeignManagementProcesses = management.Foreign.Concat(tools.Foreign).ToArray(),
          UnqueryableManagementProcessIds = management.Unqueryable.Concat(tools.Unqueryable).ToArray() };
        var finalBytes = ReadStableBytes(configPath);
        if (!iniBytes.SequenceEqual(finalBytes)) throw new IOException("Sandboxie 설정이 전체 검사 도중 변경되었습니다.");
        var finalSections = api.ReadAllSections(includeTemplates: false);
        SandboxieIniSnapshot.RequireEquivalent(finalBytes, finalSections);
        if (!SameExcept(sections, finalSections, string.Empty, string.Empty))
            throw new IOException("Sandboxie API 설정이 전체 검사 도중 변경되었습니다.");
        var install = new SetupRecoveryInstallContext(folder, startPath, dependency.Version, true,
            $"Sandboxie 서비스 PID {service.ProcessId}, 공식 서명·버전·드라이버 확인");
        return new SetupRecoveryObservation(
            configPath,
            iniBytes,
            configuration,
            sections,
            proposed.OrderBy(change => change.Kind == SetupRecoveryChangeKind.SetUserSetting ? 0 : 1).ToArray(),
            owned,
            sid,
            storage,
            otherBoxes,
            unknownTemplates,
            processIds,
            true,
            install,
            ui,
            true,
            true,
            policySafe,
            $"공식 SbieIni 서비스 RPC 경로·사용자 섹션·전체 명시 섹션과 고정 템플릿 기준을 확인했습니다. 사용자 섹션={user.Section}",
            DateTimeOffset.UtcNow) { CurrentUserSettingsSection = user.Section };
    }

    public SetupRecoveryUiResult QuiesceManagementUi(SetupRecoveryObservation observation, SetupRecoveryPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = observation.Install.SandboxiePath;
        var current = CaptureManagementProcesses(folder, observation.UserSid ?? ProfileStore.CurrentSid());
        if (current.HasUnsafe || CaptureActiveSbieIni(folder, observation.UserSid ?? ProfileStore.CurrentSid()).HasAny)
            return new(false, "SbieIni.exe가 실행 중입니다. 설정 도구를 닫고 다시 조회하세요.");
        var approved = observation.Ui.ApprovedManagementProcesses.Select(item => item.Normalize())
            .ToDictionary(item => (item.ProcessId, item.CreationTimeUtcTicks));
        if (current.Current.Any(item => !approved.ContainsKey((item.ProcessId, item.CreationTimeUtcTicks))))
            return new(false, "새 Sandboxie 관리 UI가 발견되어 안전한 복구를 진행할 수 없습니다. 자동 복구를 중단하고 현재 상태를 다시 확인하세요.");
        foreach (var item in current.Current)
            if (!TerminateApprovedManagementProcess(item, observation.Ui.ApprovedManagementProcesses, folder, observation.UserSid ?? ProfileStore.CurrentSid()))
                return new(false, "승인된 Sandboxie 관리 UI를 정확한 프로세스 신원으로 종료하지 못했습니다.");
        if (CaptureManagementProcesses(folder, observation.UserSid ?? ProfileStore.CurrentSid()).HasAny)
            return new(false, "승인된 Sandboxie 관리 UI가 종료되지 않았습니다.");
        return SetupRecoveryUiResult.NoUi;
    }

    public SetupRecoveryChangeResult ApplyChange(SetupRecoveryObservation observation, SetupRecoveryPlan plan, SetupRecoveryChange change, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lastStore is null || lastStore.Load() != lastProfile ||
            plan.Fingerprint.ProfileSha256 != SetupRecoveryFingerprint.FromObservation(observation, lastProfile).ProfileSha256)
            return new(false, false, "복구 대상 프로필이 검증된 요청 범위와 다릅니다.");
        if (change.IsNoOp) return new(true, true, "변경할 값이 없습니다.");
        var current = ReadObservedValue(observation, change.Section, change.Key);
        if (!current.SequenceEqual(change.CurrentValues, StringComparer.Ordinal))
            return new(false, false, "복구 직전 설정 값이 승인된 현재 값과 다릅니다.");
        var isGlobalRemoval = change.Kind == SetupRecoveryChangeKind.RemoveGlobalTemplate &&
            change.Section.Equals(GlobalSection, StringComparison.OrdinalIgnoreCase) && change.Key.Equals(TemplateKey, StringComparison.OrdinalIgnoreCase);
        var isUserSetting = change.Kind == SetupRecoveryChangeKind.SetUserSetting &&
            change.Key.Equals(AutoRunKey, StringComparison.OrdinalIgnoreCase) && change.Section.StartsWith("UserSettings_", StringComparison.OrdinalIgnoreCase) &&
            change.DesiredValues.SequenceEqual(new[] { "n" }, StringComparer.Ordinal);
        var isOwnedSetting = (change.Kind is SetupRecoveryChangeKind.AddOwnedBoxSetting or SetupRecoveryChangeKind.RepairOwnedBoxSetting) &&
            lastProfile is not null && change.Section.Equals(lastProfile.BoxName, StringComparison.OrdinalIgnoreCase);
        if (!isGlobalRemoval && !isUserSetting && !isOwnedSetting)
            return new(false, false, "복구 범위를 벗어난 Sandboxie 설정 변경입니다.");
        var folder = observation.Install.SandboxiePath;
        var exe = Path.Combine(folder, "SbieIni.exe");
        RuntimeVerifier.RequireComponent(exe);
        if (CaptureManagementProcesses(folder, ProfileStore.CurrentSid()).HasAny ||
            CaptureActiveSbieIni(folder, ProfileStore.CurrentSid()).HasAny)
            return new(false, false, "Sandboxie 관리 UI 또는 SbieIni.exe가 실행 중입니다.");
        if (isUserSetting)
        {
            using var api = new SandboxieApi(folder);
            if (!api.QueryUserSettingsSection().Section.Equals(change.Section, StringComparison.OrdinalIgnoreCase))
                return new(false, false, "현재 사용자 RPC 섹션이 승인된 섹션과 다릅니다.");
        }
        if (isOwnedSetting)
        {
            var expected = IsolationPolicy.ExpectedForRecovery(lastProfile!, observation.Configuration.ExplicitBox);
            if (!expected.TryGetValue(change.Key, out var expectedValues) || !expectedValues.SequenceEqual(change.DesiredValues, StringComparer.Ordinal))
                return new(false, false, "전용 환경 설정 목표가 IsolationPolicy.Expected와 다릅니다.");
        }
        if (isGlobalRemoval)
        {
            var removed = change.CurrentValues.Except(change.DesiredValues, StringComparer.OrdinalIgnoreCase).ToArray();
            if (removed.Length == 0 || removed.Any(value => !RecoveryTemplateRules.IsReviewed(value)) || change.DesiredValues.Count != 0)
                return new(false, false, "삭제 승인된 검토 템플릿 범위와 다릅니다.");
            var baseline = observation;
            foreach (var name in removed)
            {
                var desired = ReadObservedValue(baseline, change.Section, change.Key).Where(value => !value.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                baseline = RunVerifiedCommand(exe, folder, ["delete", change.Section, change.Key, name], baseline, change.Section, change.Key, desired, cancellationToken);
            }
            return new(true, true, "검토된 전역 템플릿을 각각 제거하고 파일·API를 확인했습니다.");
        }
        var settingBaseline = observation;
        foreach (var step in SandboxieSettingCommands.Replace(change))
            settingBaseline = RunVerifiedCommand(exe, folder, step.Arguments, settingBaseline,
                change.Section, change.Key, step.ExpectedValues, cancellationToken);
        return new(true, true, "설정 명령 종료와 파일·API 결과를 확인했습니다.");
    }

    private SetupRecoveryObservation RunVerifiedCommand(string exe, string folder, IReadOnlyList<string> arguments,
        SetupRecoveryObservation baseline, string section, string key, IReadOnlyList<string> desired, CancellationToken cancellationToken)
    {
        var store = lastStore ?? throw new InvalidOperationException("검증된 설정 저장소가 없습니다.");
        var profile = lastProfile;
        var before = Inspect(store, profile, cancellationToken).Normalize();
        if (SetupRecoveryFingerprint.FromObservation(before, profile).ScopeKey != SetupRecoveryFingerprint.FromObservation(baseline, profile).ScopeKey ||
            before.BoxProcessIds.Count != 0 || !before.Ui.Fresh || before.Ui.ApprovedManagementProcesses.Count != 0 ||
            store.Load() != profile)
            throw new InvalidDataException("설정 명령 직전 상태가 바뀌어 복구를 중단했습니다.");
        RuntimeVerifier.RequireComponent(exe);
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = exe, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = folder
        }};
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException("SbieIni.exe를 시작하지 못했습니다.");
        // Drain both pipes concurrently; never include tool output (which can contain
        // setting values) in diagnostics or the durable operation history.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var elapsed = Stopwatch.StartNew();
        while (!process.WaitForExit(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException("SbieIni.exe의 종료를 확인하지 못했습니다. 미완료 기록을 보존하며 도구가 종료되기 전에는 다음 복구를 차단합니다.");
        }
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException($"SbieIni.exe 설정 명령 실패 (종료 코드 {process.ExitCode}).");
        var after = Inspect(store, profile, cancellationToken).Normalize();
        if (store.Load() != profile || before.IniBytes.SequenceEqual(after.IniBytes) ||
            !ReadObservedValue(after, section, key).SequenceEqual(desired, StringComparer.Ordinal) ||
            !SameExcept(before.ExplicitSections, after.ExplicitSections, section, key) ||
            after.BoxProcessIds.Count != 0 || !after.Ui.Fresh || after.Ui.ApprovedManagementProcesses.Count != 0 ||
            before.ConfigPath != after.ConfigPath)
            throw new InvalidDataException("SbieIni.exe 종료 후 파일·API 결과가 승인된 한 항목 변경과 일치하지 않습니다.");
        return after;
    }

    private static bool SameExcept(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> before,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> after, string allowedSection, string allowedKey)
    {
        foreach (var section in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var left = before.TryGetValue(section, out var l) ? l : new Dictionary<string, string[]>();
            var right = after.TryGetValue(section, out var r) ? r : new Dictionary<string, string[]>();
            foreach (var key in left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase))
            {
                if (section.Equals(allowedSection, StringComparison.OrdinalIgnoreCase) && key.Equals(allowedKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (!ReadValue(left, key).SequenceEqual(ReadValue(right, key), StringComparer.Ordinal)) return false;
            }
        }
        return true;
    }
    private static string RequireActualFile(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Sandboxie가 반환한 INI 경로가 존재하지 않습니다.", path);
        ProfileStore.RejectReparseAncestors(path);
        return path;
    }

    private static byte[] ReadStableBytes(string path)
    {
        var first = File.ReadAllBytes(path);
        var second = File.ReadAllBytes(path);
        if (!first.SequenceEqual(second)) throw new IOException("Sandboxie INI가 조회 중 변경되었습니다.");
        return first;
    }

    private static string[] ReadValue(IReadOnlyDictionary<string, string[]> section, string key) =>
        section.TryGetValue(key, out var values) ? values.ToArray() : Array.Empty<string>();

    private static string[] ReadObservedValue(SetupRecoveryObservation observation, string section, string key)
    {
        if (observation.ExplicitSections.TryGetValue(section, out var values)) return ReadValue(values, key);
        if (section.Equals(GlobalSection, StringComparison.OrdinalIgnoreCase)) return ReadValue(observation.Configuration.ExplicitGlobals, key);
        if (section.Equals(observation.OwnedBoxName, StringComparison.OrdinalIgnoreCase)) return ReadValue(observation.Configuration.ExplicitBox, key);
        return Array.Empty<string>();
    }

    private static bool IsBoxSection(string name) =>
        !name.Equals(GlobalSection, StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith("UserSettings_", StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith("Template_", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("DefaultTemplates", StringComparison.OrdinalIgnoreCase);

    private sealed record ManagementProcessObservation(
        IReadOnlyList<SetupRecoveryProcessIdentity> Current,
        IReadOnlyList<SetupRecoveryProcessIdentity> Foreign,
        IReadOnlyList<int> Unqueryable)
    {
        public bool HasUnsafe => Foreign.Count != 0 || Unqueryable.Count != 0;
        public bool HasAny => Current.Count != 0 || HasUnsafe;
    }

    private static ManagementProcessObservation CaptureManagementProcesses(string folder, string sid) =>
        CaptureTools(folder, sid, ManagementNames);

    private static ManagementProcessObservation CaptureActiveSbieIni(string folder, string sid) =>
        CaptureTools(folder, sid, ["SbieIni"]);

    private static ManagementProcessObservation CaptureTools(string folder, string sid, IReadOnlyList<string> names)
    {
        var candidates = new List<(int Id, string ImageName)>();
        foreach (var name in names)
            foreach (var process in Process.GetProcessesByName(name))
                using (process) candidates.Add((process.Id, name + ".exe"));
        using var ownProcess = Process.GetCurrentProcess();
        var observation = ClassifyManagementProcesses(candidates.Select(item => item.Id).ToArray(),
            pid => CaptureProcess(pid, folder, candidates.Single(item => item.Id == pid).ImageName), sid, ownProcess.SessionId);
        return new(observation.ApprovedManagementProcesses, observation.ForeignManagementProcesses, observation.UnqueryableManagementProcessIds);
    }

    /// <summary>Classifies identities only after the native capture has verified the
    /// installed image. An inaccessible identity stays a shared-INI blocker.</summary>
    internal static SetupRecoveryUiState ClassifyManagementProcesses(IReadOnlyList<int> processIds,
        Func<int, SetupRecoveryProcessIdentity> capture, string currentSid, int currentSession)
    {
        var current = new List<SetupRecoveryProcessIdentity>();
        var foreign = new List<SetupRecoveryProcessIdentity>();
        var unqueryable = new List<int>();
        foreach (var pid in processIds)
        {
            try
            {
                var identity = capture(pid).Normalize();
                if (identity.ProcessId != pid || identity.UserSid is null || identity.SessionId is null)
                    throw new InvalidDataException("관리 도구의 사용자·세션 신원이 불완전합니다.");
                if (identity.UserSid == currentSid && identity.SessionId == currentSession) current.Add(identity);
                else foreign.Add(identity);
            }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                unqueryable.Add(pid);
            }
        }
        return new(foreign.Count == 0 && unqueryable.Count == 0, current)
        { ForeignManagementProcesses = foreign, UnqueryableManagementProcessIds = unqueryable };
    }

    private static SetupRecoveryProcessIdentity CaptureProcess(int processId, string folder, string expectedName)
    {
        var path = QueryProcessPath(processId);
        var full = Path.GetFullPath(path);
        var fileName = expectedName;
        var expected = Path.Combine(folder, fileName);
        if (!full.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Sandboxie 관리 도구 경로가 검토된 설치 폴더와 다릅니다.");
        if (!ProcessIdToSessionId((uint)processId, out var actualSession))
            throw new InvalidDataException("Sandboxie 관리 도구의 세션을 조회하지 못했습니다.");
        var actualSid = QueryProcessSid(processId);
        VerifyManagerImage(full);
        var creationTicks = QueryCreationTicks(processId);
        return new SetupRecoveryProcessIdentity(processId, creationTicks, full, actualSid, checked((int)actualSession));
    }

    private static string QueryProcessPath(int pid)
    {
        var handle = OpenProcess(0x1000, false, (uint)pid);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Sandboxie 관리 도구 프로세스를 조회하지 못했습니다.");
        try
        {
            var buffer = new StringBuilder(32768);
            uint length = (uint)buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref length))
                throw new InvalidOperationException("Sandboxie 관리 도구 실행 경로를 조회하지 못했습니다.");
            return buffer.ToString();
        }
        finally { CloseHandle(handle); }
    }

    private static bool TerminateApprovedManagementProcess(SetupRecoveryProcessIdentity candidate, IReadOnlyList<SetupRecoveryProcessIdentity> approved, string folder, string sid)
    {
        var handle = OpenProcess(0x00101001, false, (uint)candidate.ProcessId);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var buffer = new StringBuilder(32768);
            uint length = (uint)buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref length)) return false;
            var full = Path.GetFullPath(buffer.ToString());
            var fileName = Path.GetFileName(full);
            if (!full.Equals(Path.Combine(folder, fileName), StringComparison.OrdinalIgnoreCase)) return false;
            if (fileName.Equals("SbieCtrl.exe", StringComparison.OrdinalIgnoreCase) == false && fileName.Equals("SandMan.exe", StringComparison.OrdinalIgnoreCase) == false) return false;
            if (!ProcessIdToSessionId((uint)candidate.ProcessId, out var session) || session != (uint)Process.GetCurrentProcess().SessionId) return false;
            var actualSid = QueryProcessSid(handle);
            if (!actualSid.Equals(sid, StringComparison.Ordinal)) return false;
            var creation = QueryCreationTicks(handle);
            VerifyManagerImage(full);
            var actual = new SetupRecoveryProcessIdentity(candidate.ProcessId, creation, full, actualSid, checked((int)session));
            if (!actual.Equals(candidate) || !approved.Select(item => item.Normalize()).Contains(actual)) return false;
            if (!TerminateProcess(handle, 1)) return false;
            return WaitForSingleObject(handle, 5000) == 0;
        }
        finally { CloseHandle(handle); }
    }

    private static long QueryCreationTicks(IntPtr handle)
    {
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
            throw new InvalidOperationException("Sandboxie 관리 도구 생성 시각을 확인하지 못했습니다.");
        return DateTime.FromFileTimeUtc(creation.ToInt64()).Ticks;
    }

    private static long QueryCreationTicks(int pid)
    {
        var handle = OpenProcess(0x1000, false, (uint)pid);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Sandboxie 관리 도구 생성 시각을 확인하지 못했습니다.");
        try { return QueryCreationTicks(handle); }
        finally { CloseHandle(handle); }
    }


    private static string QueryProcessSid(IntPtr process)
    {
        if (!OpenProcessToken(process, 0x0008, out var token)) { token?.Dispose(); throw new InvalidOperationException("Sandboxie 관리 도구 토큰을 열지 못했습니다."); }
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User?.Value ?? throw new InvalidDataException("Sandboxie 관리 도구 SID가 없습니다.");
    }

    private static string QueryProcessSid(int pid)
    {
        var process = OpenProcess(0x1000, false, (uint)pid);
        if (process == IntPtr.Zero) throw new InvalidOperationException("Sandboxie 관리 도구 토큰을 조회하지 못했습니다.");
        try { return QueryProcessSid(process); }
        finally { CloseHandle(process); }
    }


    private static void VerifyManagerFiles(string folder)
    {
        foreach (var name in ManagementNames)
        {
            var path = Path.Combine(folder, name + ".exe");
            if (!File.Exists(path)) continue;
            VerifyManagerImage(path);
        }
    }

    private static void VerifyManagerImage(string path)
    {
        Authenticode.RequirePublisher(path, "NOVASYNC LABS PTE. LTD.", "Xanasoft", "Xanasoft Ltd.", "David Xanatos", "Open Source Developer, David Xanatos", "Sandboxie Holdings, LLC");
        var version = FileVersionInfo.GetVersionInfo(path).FileVersion?.Trim() ?? string.Empty;
        var expected = Path.GetFileName(path).Equals("SandMan.exe", StringComparison.OrdinalIgnoreCase) ? RuntimeVerifier.PlusVersion : RuntimeVerifier.CoreVersion;
        if (!RuntimeVerifier.MatchesVersion(version, expected))
            throw new InvalidDataException("Sandboxie 관리 도구 버전이 검토 기준과 다릅니다.");
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [StructLayout(LayoutKind.Sequential)] private readonly struct NativeFileTime(uint low, uint high)
    {
        public readonly uint Low = low;
        public readonly uint High = high;
        public long ToInt64() => ((long)High << 32) | Low;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(IntPtr process, out NativeFileTime creation, out NativeFileTime exit, out NativeFileTime kernel, out NativeFileTime user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

}
