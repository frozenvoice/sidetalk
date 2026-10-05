using Sidetalk.Core;

namespace Sidetalk.App;

/// <summary>One cancellable UI request. Only Core can grant dispatch authority.</summary>
internal sealed class QuickLaunchFlow(IQuickLaunchController controller)
{
    internal static bool NeedsGui(QuickLaunchResult result) => result.Outcome switch
    {
        QuickLaunchOutcome.Cancelled => result.Snapshot.CommandPending != false,
        QuickLaunchOutcome.Launched => result.Snapshot.CommandPending != false ||
            !result.Snapshot.CurrentConfigurationVerified || result.Snapshot.BoxProcessIds.Count == 0,
        _ => true
    };

    internal static bool StartupAllowed(bool interactive, bool system, bool elevated, int sessionId,
        UserLaunchIntegrationStatus selection) => interactive && !system && !elevated && sessionId > 0 &&
        selection.Options.LoginStartup && !selection.NeedsUpdate;

    internal async Task<QuickLaunchResult> RunAsync(Func<Task<bool>> confirm,
        CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("전용 프로필과 현재 실행 상태를 확인하고 있습니다…");
        var status = await controller.InspectQuickLaunchAsync();
        if (cancellationToken.IsCancellationRequested)
            return new(QuickLaunchOutcome.Cancelled, status.Snapshot, "빠른 실행 요청을 취소했습니다.");
        if (status.Snapshot.InspectionFailure is not null || !status.Snapshot.ActionsPermitted ||
            !status.Snapshot.CurrentConfigurationVerified || status.Snapshot.CommandPending != false)
            return new(status.Snapshot.CommandPending == true ? QuickLaunchOutcome.Uncertain : QuickLaunchOutcome.Blocked,
                status.Snapshot, status.Reason);
        if (status.Approval is null || (!status.Snapshot.CanLaunch && status.Snapshot.BoxProcessIds.Count == 0))
            return new(QuickLaunchOutcome.Blocked, status.Snapshot, status.Reason);

        QuickLaunchApproval? approval = null;
        if (status.Snapshot.CanLaunch && status.Approval is not null && !status.ConsentMatches)
        {
            progress?.Report("이번 실행의 확인을 기다리고 있습니다.");
            var accepted = await confirm();
            if (cancellationToken.IsCancellationRequested || !accepted)
                return new(QuickLaunchOutcome.Cancelled, status.Snapshot, "빠른 실행 요청을 취소했습니다.");
            approval = status.Approval;
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("실행 직전 검증과 명령 결과를 확인하고 있습니다…");
        // Core re-derives the scope under its gate and durable journal lease.
        // Once dispatched, cancellation does not interrupt journal completion.
        return await controller.LaunchSecondaryAsync(approval, cancellationToken);
    }
}
