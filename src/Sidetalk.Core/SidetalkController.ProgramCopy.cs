namespace Sidetalk.Core;

public sealed partial class SidetalkController
{
    /// <summary>Read-only. A scan that cannot be trusted is reported as nothing to offer, never as a failure of inspection.</summary>
    private StaleProgramCopyScan? TryScanProgramCopy(BoxProfile profile, DependencyInfo kakao)
    {
        try { return runtime.ReadStaleProgramCopy(profile, kakao); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { return null; }
    }

    private StaleProgramCopyObservation ObserveProgramCopy(StaleProgramCopyScan scan, bool canSetAside, bool running) =>
        new(scan.Directory, scan.CopyVersion, scan.HostVersion, scan.Files.Count, scan.TotalBytes, scan.Fingerprint, canSetAside,
            canSetAside ? "전용 환경이 비어 있어 지금 치울 수 있습니다."
            : running ? "보조 카카오톡과 남은 전용 프로세스를 종료한 뒤 치울 수 있습니다."
            : readOnly ? "읽기 전용 조회에서는 옮기지 않습니다. SIDETALK 창에서 진행하세요."
            : "현재 상태에서는 치울 수 없습니다. 상태를 다시 확인하세요.");

    private sealed record ProgramCopyScope(BoxProfile Profile, StaleProgramCopyScan Scan);

    /// <summary>Every condition is read again from the real state; an earlier observation grants nothing.</summary>
    private ProgramCopyScope CaptureProgramCopyScope()
    {
        var profile = store.Load() ?? throw new InvalidDataException("전용 프로필을 확인하지 못했습니다.");
        if (setupRecovery?.HasUnfinishedRecovery == true)
            throw new InvalidOperationException("완료되지 않은 설정 복구 기록이 있어 프로그램 사본을 옮기지 않았습니다.");
        var sandboxie = runtime.Sandboxie();
        var kakao = runtime.Kakao();
        if (!sandboxie.Available || sandboxie.Path is null || !kakao.Available || kakao.Path is null)
            throw new InvalidDataException("공식 카카오톡과 Sandboxie 설치를 확인하지 못했습니다.");
        runtime.VerifyCommandFiles(Path.Combine(Path.GetDirectoryName(sandboxie.Path)!, "Start.exe"), kakao.Path);
        using var session = runtime.Open(sandboxie);
        var configuration = session.ReadConfiguration(profile.BoxName);
        var policy = IsolationPolicy.Validate(profile, configuration);
        if (!policy.Owned || !policy.Safe)
            throw new InvalidDataException("전용 환경의 소유·격리 설정을 확인하지 못해 프로그램 사본을 옮기지 않았습니다.");
        if (!SandboxieApi.SameNativePath(session.FileRoot(profile.BoxName), profile.StoragePath))
            throw new InvalidDataException("전용 환경의 실제 저장 경로를 확인하지 못했습니다.");
        if (session.Processes(profile).Count != 0)
            throw new InvalidOperationException("전용 환경에서 프로세스가 실행 중입니다. 보조 카카오톡을 종료한 뒤 다시 시도하세요.");
        var scan = runtime.ReadStaleProgramCopy(profile, kakao)
            ?? throw new InvalidOperationException("오래된 프로그램 사본을 더 이상 확인할 수 없습니다. 상태를 다시 확인하세요.");
        if (store.Load() != profile) throw new InvalidDataException("조회 중 전용 프로필이 변경됐습니다.");
        return new(profile, scan);
    }

    /// <summary>
    /// Explicitly approved. Moves only the verified older program copy out of the owned, empty box
    /// into the SIDETALK data root. Account data, settings, other boxes and the host installation
    /// are not touched, and nothing is deleted.
    /// </summary>
    public async Task<AppSnapshot> SetAsideStaleProgramCopyAsync(StaleProgramCopyObservation approved, IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(approved);
        RequireWritable();
        await gate.WaitAsync();
        try
        {
            RequireSetupHost();
            using var lease = journal.AcquireLease();
            return await Task.Run(() =>
            {
                RequireNoPendingCommandForSetup();
                var scope = CaptureProgramCopyScope();
                if (!scope.Scan.Fingerprint.Equals(approved.Fingerprint, StringComparison.Ordinal) ||
                    !SandboxieApi.SameNativePath(scope.Scan.Directory, approved.Directory))
                    throw new InvalidDataException("승인 후 프로그램 사본이나 공식 설치가 바뀌었습니다. 상태를 다시 확인하고 승인하세요.");
                setupCancellation.Token.ThrowIfCancellationRequested();
                progress?.Report("오래된 프로그램 사본을 보존 폴더로 옮기는 중…");
                var final = CaptureProgramCopyScope();
                if (final.Profile != scope.Profile || !final.Scan.Fingerprint.Equals(scope.Scan.Fingerprint, StringComparison.Ordinal))
                    throw new InvalidDataException("옮기기 직전 상태가 바뀌어 프로그램 사본을 옮기지 않았습니다.");
                var preserved = StaleProgramCopy.SetAside(final.Scan, store.Root, final.Profile);
                progress?.Report("옮긴 뒤 전용 환경을 다시 확인하는 중…");
                var snapshot = Inspect(leaseHeld: true);
                return snapshot with
                {
                    RecoveryResultDetail = snapshot.StaleProgramCopy is null
                        ? $"오래된 프로그램 사본({final.Scan.CopyVersion})을 보존 폴더로 옮겼습니다. 이제 공식 설치({final.Scan.HostVersion})의 파일로 실행합니다. 계정·대화 데이터는 바꾸지 않았습니다. 보존 위치: {preserved}"
                        : $"프로그램 사본을 옮겼지만 다시 확인한 결과 오래된 사본이 여전히 보입니다. 보존 위치: {preserved}"
                };
            });
        }
        finally { ReleaseGate(); }
    }
}
