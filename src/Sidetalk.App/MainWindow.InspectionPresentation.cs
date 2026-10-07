using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Sidetalk.App;

public partial class MainWindow
{
    // Presentation only. These fields never confer permission to dispatch a command.
    private DateTimeOffset? _lastSuccessfulInspectionAt;
    private string? _mainInspectionFailure;
    private string? _recoveryInspectionFailure;
    private string? _evidenceInspectionFailure;
    private string? _recoveryFeedback;
    private bool _actionFeedbackRequiresRevalidation;
    private bool _manualRefreshInFlight;
    private Task _manualRefreshTask = Task.CompletedTask;
    private CancellationTokenSource? _manualRefreshCancellation;
    private readonly ToolTip _inspectionDetailsToolTip = new() { Placement = PlacementMode.Bottom };

    private void AutomaticInspectionIndicator_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _inspectionDetailsToolTip.PlacementTarget = AutomaticInspectionIndicator;
        _inspectionDetailsToolTip.IsOpen = true;
    }

    private void AutomaticInspectionIndicator_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _inspectionDetailsToolTip.IsOpen = false;

    private Task RequestManualRefreshAsync(bool allowAutomaticRecovery)
    {
        if (_manualRefreshInFlight) return _manualRefreshTask;
        if (_closed || _isRenderMode || _windowMinimized || _operationInFlight || _actionInFlight || _settingsLoading)
            return Task.CompletedTask;

        _manualRefreshInFlight = true;
        _manualRefreshTask = RunManualRefreshAsync(allowAutomaticRecovery);
        return _manualRefreshTask;
    }

    private async Task RunManualRefreshAsync(bool allowAutomaticRecovery)
    {
        using var cancellation = new CancellationTokenSource();
        _manualRefreshCancellation = cancellation;
        _ = ShowManualRefreshProgressAsync(cancellation.Token);
        ApplyActionAvailability();
        try { await InspectAndApplyAsync(allowAutomaticRecovery); }
        finally
        {
            cancellation.Cancel();
            if (ReferenceEquals(_manualRefreshCancellation, cancellation)) _manualRefreshCancellation = null;
            _manualRefreshInFlight = false;
            SetText(ManualRefreshText, "상태 새로 확인");
            if (!_closed) ApplyActionAvailability();
        }
    }

    private async Task ShowManualRefreshProgressAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            if (!cancellationToken.IsCancellationRequested && !_closed && !_windowMinimized)
                SetText(ManualRefreshText, "확인 중…");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void CancelManualRefreshPresentation()
    {
        _inspectionDetailsToolTip.IsOpen = false;
        _manualRefreshCancellation?.Cancel();
        SetText(ManualRefreshText, "상태 새로 확인");
    }

    private IProgress<string> RecoveryProgressForCurrentOperation()
    {
        var generation = _inspectionGeneration;
        return new Progress<string>(message =>
        {
            if (_operationInFlight && IsCurrentInspection(generation)) SetRecoveryProgress(message);
        });
    }

    private void UpdateInspectionDetails()
    {
        var detail = "자동으로 상태를 확인합니다.\n마지막 성공 확인: " +
            (_lastSuccessfulInspectionAt is { } successful
                ? successful.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "없음");
        if (_mainInspectionFailure is { } main) detail += "\n최근 상태 확인 실패: " + main;
        if (_recoveryInspectionFailure is { } recovery) detail += "\n최근 복구 확인 실패: " + recovery;
        if (_evidenceInspectionFailure is { } evidence) detail += "\n최근 중단 흔적 확인 실패: " + evidence;
        _inspectionDetailsToolTip.Content = detail;
        AutomaticInspectionIndicator.ToolTip = _inspectionDetailsToolTip;
        // HelpText is discoverable on focus, never a periodic live announcement.
        AutomationProperties.SetHelpText(AutomaticInspectionIndicator, detail);
        SetText(InspectionDetailText, detail);
    }

    private static void SetText(TextBlock target, string text)
    {
        if (string.Equals(target.Text, text, StringComparison.Ordinal)) return;
        target.Text = text;
        if (AutomationProperties.GetLiveSetting(target) != AutomationLiveSetting.Off)
            UIElementAutomationPeer.FromElement(target)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static void ReplaceIfChanged(ObservableCollection<string> target, IEnumerable<string> values)
    {
        var next = values.ToArray();
        if (target.SequenceEqual(next, StringComparer.Ordinal)) return;
        target.Clear();
        foreach (var value in next) target.Add(value);
    }
}
