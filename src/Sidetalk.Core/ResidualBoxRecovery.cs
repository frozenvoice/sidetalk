namespace Sidetalk.Core;

/// <summary>Controller-issued one-time scope; never a general Stop permission or automatic consent.</summary>
public sealed class ResidualBoxRecoveryApproval
{
    internal ResidualBoxRecoveryApproval(BoxProfile profile, SetupRecoveryPlan repairPlan, IReadOnlyList<BoxProcess> processes,
        string startExe, string kakaoExe)
    {
        Profile = profile;
        RepairPlan = repairPlan;
        Processes = Array.AsReadOnly(processes.ToArray());
        StartExe = startExe;
        KakaoExe = kakaoExe;
    }

    internal BoxProfile Profile { get; }
    internal IReadOnlyList<BoxProcess> Processes { get; }
    internal string StartExe { get; }
    internal string KakaoExe { get; }
    public SetupRecoveryPlan RepairPlan { get; }
    public string BoxName => Profile.BoxName;
    public IReadOnlyList<BoxProcessObservation> ResidualProcesses => AdminRightsObservation.FromProcesses(Profile, Processes);
}

internal static class ResidualBoxRecoverySafety
{
    internal static void RequirePolicy(BoxProfile profile, ConfigurationLayers configuration)
    {
        var current = configuration.ExplicitBox.TryGetValue(AdminRightsRecovery.Key, out var values) ? values : [];
        if (!AdminRightsRecovery.IsRestorableValue(current) ||
            !IsolationPolicy.Validate(profile, configuration).Owned ||
            !SandboxieConfigurationReader.Same(configuration.ExplicitBox, configuration.EffectiveBox))
            throw new InvalidDataException("관리자 권한 제한만 복구 가능한 정확한 전용 정책을 확인하지 못했습니다.");
        // No template removal or global shadow: every other existing rule must already be safe.
        foreach (var (key, expected) in new[] { ("AutoDelete", "n"), ("NeverDelete", "y"), ("AutoRecover", "n") })
            if (!configuration.ExplicitBox.TryGetValue(key, out var protection) ||
                !protection.SequenceEqual([expected], StringComparer.Ordinal))
                throw new InvalidDataException("전용 환경의 데이터 삭제·복구 방지 정책을 확인하지 못했습니다: " + key);
        var box = configuration.ExplicitBox.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        box[AdminRightsRecovery.Key] = ["y"];
        if (!IsolationPolicy.Validate(profile, new(box, box, configuration.ExplicitGlobals, configuration.EffectiveGlobals)).Safe)
            throw new InvalidDataException("관리자 권한 제한 이외의 정책 차이가 있어 잔여 프로세스 정리를 차단했습니다.");
    }

    internal static void RequireProcesses(BoxProfile profile, IReadOnlyList<BoxProcess> processes, bool residualRequired)
    {
        if (processes.Select(process => process.Id).Distinct().Count() != processes.Count ||
            processes.Any(process => !AdminRightsObservation.IsKnownProcess(process, profile)))
            throw new InvalidDataException("전용 프로세스의 이름·PID·사용자 신원을 확인하지 못했습니다.");
        if (processes.Any(AdminRightsObservation.IsKakao))
            throw new InvalidOperationException("보조 카카오톡이 실행 중입니다. 트레이 메뉴에서 정상 종료한 뒤 다시 확인하세요.");
        if (residualRequired && processes.Count == 0)
            throw new InvalidOperationException("전용 환경이 비어 있습니다. 관리자 권한 제한 복구 계획을 다시 확인하세요.");
    }
}
