namespace Sidetalk.Core;

public sealed partial class SidetalkController : ISecondaryAppearanceController
{
    public async Task<SecondaryAppearanceState> InspectSecondaryAppearanceAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync();
        try
        {
            RequireSetupHost();
            using var lease = journal.TryAcquireInspectionLease();
            return await Task.Run(() => CreateAppearanceService().Inspect());
        }
        finally { ReleaseGate(); }
    }

    public async Task<SecondaryAppearanceState> ApplySecondaryAppearanceAsync(SecondaryAppearancePlan plan)
    {
        RequireWritable();
        await gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RequireSetupHost();
            using var lease = journal.AcquireLease();
            var profile = store.Load() ?? throw new InvalidOperationException("전용 환경이 없습니다.");
            var dependency = runtime.Sandboxie();
            if (!dependency.Available || dependency.Path is null) throw new InvalidOperationException("Sandboxie 설치를 확인하지 못했습니다.");
            var startExe = Path.Combine(Path.GetDirectoryName(dependency.Path)!, "Start.exe");
            if (journal.ReadPending(profile, startExe) is not null || commandLifecycle.IsPending || setupRecovery?.HasUnfinishedRecovery == true)
                throw new InvalidOperationException("실행·종료 또는 설정 복구의 미완료 기록을 먼저 확인하세요.");
            return await Task.Run(() => CreateAppearanceService().Apply(plan));
        }
        finally { ReleaseGate(); }
    }

    private SecondaryAppearanceService CreateAppearanceService() => new(store, new WindowsSetupRecoveryEnvironment());
}
