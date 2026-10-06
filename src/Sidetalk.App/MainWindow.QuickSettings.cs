using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Sidetalk.Core;

namespace Sidetalk.App;

public partial class MainWindow
{
    private QuickLaunchStatus? _quickSettings;
    private SecondaryAppearanceState? _appearanceSettings;
    private UserLaunchIntegration? _launchIntegration;
    private bool _settingsLoading;
    private Task _settingsTask = Task.CompletedTask;

    internal void ShowEntryFeedback(string reason)
    {
        EntryFeedbackText.Text = reason;
        EntryFeedbackText.Visibility = Visibility.Visible;
        MainTabs.SelectedIndex = 0;
    }

    internal async Task<bool> RunExternalQuickLaunchAsync(Func<Task> run)
    {
        if (_closed || _isRenderMode || _operationInFlight || _actionInFlight) return false;
        // Hold ownership even while waiting for the whole current poll. Closing
        // this GUI cannot terminate a shortcut's in-flight command/journal.
        _operationInFlight = true;
        ApplyActionAvailability();
        try
        {
            await _settingsTask;
            await _inspectionTask;
            if (_closed) return false;
            ClearStaleSnapshot("빠른 실행 요청을 확인하고 있습니다.");
            await run();
            return true;
        }
        finally
        {
            _operationInFlight = false;
            ApplyActionAvailability();
            if (!_closed) await InspectAndApplyAsync();
        }
    }

    internal void UseLaunchIntegration(UserLaunchIntegration integration) => _launchIntegration = integration;

    internal Task ReloadConvenienceAsync() => LoadQuickSettingsAsync();

    private Task LoadQuickSettingsAsync()
    {
        if (_settingsLoading) return _settingsTask;
        if (_closed || _isRenderMode || _operationInFlight || _actionInFlight || _inspectionInFlight ||
            _controller is not IQuickLaunchController quick || _controller is not ISecondaryAppearanceController appearance)
            return Task.CompletedTask;
        _settingsLoading = true;
        ApplyActionAvailability();
        _settingsTask = LoadQuickSettingsCoreAsync(quick, appearance);
        return _settingsTask;
    }

    private async Task LoadQuickSettingsCoreAsync(IQuickLaunchController quick, ISecondaryAppearanceController appearance)
    {
        var generation = _inspectionGeneration;
        try
        {
            var status = await quick.InspectQuickLaunchAsync();
            if (!IsCurrentInspection(generation)) return;
            var identification = await appearance.InspectSecondaryAppearanceAsync();
            if (!IsCurrentInspection(generation)) return;
            ApplyQuickSettings(status, identification);
            // Production locations are created only for a real controller. Fakes
            // must explicitly inject locations and never inspect user shortcuts.
            if (_launchIntegration is null && _controller is SidetalkController)
            {
                HostProfileContext.RequireUnredirected(new ProfileStore());
                _launchIntegration = new(UserLaunchLocations.ForCurrentUser(),
                    Environment.ProcessPath ?? throw new InvalidOperationException("SIDETALK 설치 경로를 확인하지 못했습니다."));
            }
            if (_launchIntegration is not null) ApplyShortcutSettings(_launchIntegration.Inspect());
        }
        catch (Exception exception)
        {
            if (IsCurrentInspection(generation))
                QuickSettingsResultText.Text = "설정을 확인하지 못했습니다. " + ReadableException(exception);
        }
        finally
        {
            _settingsLoading = false;
            ApplyActionAvailability();
        }
    }

    internal void ApplyQuickSettings(QuickLaunchStatus quick, SecondaryAppearanceState appearance)
    {
        _quickSettings = quick;
        _appearanceSettings = appearance;
        QuickConsentCheckBox.IsChecked = quick.ConsentMatches;
        QuickConsentScopeText.Text = $"전용 환경: {quick.Snapshot.BoxName}\n{quick.Reason}";
        AppearanceStateText.Text = appearance.Message +
            (string.IsNullOrWhiteSpace(appearance.PendingReason) ? "" : "\n" + appearance.PendingReason);
        AppearanceColorCombo.Items.Clear();
        var palette = new[] { ("현재 색", appearance.ColorRgb), ("초록", "#22C55E"), ("파랑", "#3B82F6"),
            ("보라", "#A855F7"), ("주황", "#F97316"), ("청록", "#14B8A6") };
        foreach (var (name, color) in palette)
            AppearanceColorCombo.Items.Add(new ComboBoxItem { Content = name + " · " + color, Tag = color });
        AppearanceColorCombo.SelectedIndex = 0;
        AppearanceLabelCheckBox.IsChecked = appearance.Label == SecondaryAppearance.SecondaryLabel;
        UpdateAppearancePreview();
        ApplyActionAvailability();
    }

    internal void ApplyShortcutSettings(UserLaunchIntegrationStatus status)
    {
        DesktopShortcutCheckBox.IsChecked = status.Options.DesktopShortcut;
        StartMenuShortcutCheckBox.IsChecked = status.Options.StartMenuShortcut;
        LoginStartupCheckBox.IsChecked = status.Options.LoginStartup;
        ShortcutStateText.Text = status switch
        {
            { PreviousTargets.Count: > 0 } =>
                "SIDETALK가 만든 바로가기가 이전 설치 위치를 실행합니다:\n" + string.Join("\n", status.PreviousTargets) +
                "\n'바로가기 선택 저장'을 누르면 소유가 확인된 바로가기만 이 설치 위치로 갱신합니다. 사용자가 바꾼 바로가기는 그대로 둡니다.",
            { NeedsUpdate: true } => "SIDETALK가 만든 바로가기가 없습니다(제거·재설치 포함). '바로가기 선택 저장'으로 이 설치 위치에 다시 만드세요.",
            _ => "저장된 선택을 확인했습니다. SIDETALK가 만든 바로가기만 관리합니다."
        };
    }

    private void ApplyQuickSettingsAvailability(bool busy)
    {
        var blocked = busy || _settingsLoading;
        QuickConsentCheckBox.IsEnabled = !blocked && _quickSettings?.Approval is not null;
        SaveQuickConsentButton.IsEnabled = !blocked && _quickSettings?.Approval is not null;
        ReloadQuickSettingsButton.IsEnabled = !blocked;
        DesktopShortcutCheckBox.IsEnabled = !blocked && _launchIntegration is not null;
        StartMenuShortcutCheckBox.IsEnabled = DesktopShortcutCheckBox.IsEnabled;
        LoginStartupCheckBox.IsEnabled = DesktopShortcutCheckBox.IsEnabled;
        SaveShortcutsButton.IsEnabled = DesktopShortcutCheckBox.IsEnabled;
        RemoveShortcutsButton.IsEnabled = DesktopShortcutCheckBox.IsEnabled;
        AppearanceColorCombo.IsEnabled = !blocked && _appearanceSettings is { CanApply: true, Plan: not null };
        AppearanceLabelCheckBox.IsEnabled = AppearanceColorCombo.IsEnabled;
        SaveAppearanceButton.IsEnabled = AppearanceColorCombo.IsEnabled;
    }

    private async Task ChangeQuickSettingsAsync(Func<Task> change)
    {
        if (_closed || _isRenderMode || _operationInFlight || _actionInFlight || _inspectionInFlight || _settingsLoading) return;
        _operationInFlight = true;
        ApplyActionAvailability();
        try
        {
            await change();
        }
        catch (Exception exception)
        {
            QuickSettingsResultText.Text = "저장하지 못했습니다. " + ReadableException(exception);
        }
        finally
        {
            _operationInFlight = false;
            ApplyActionAvailability();
            await InspectAndApplyAsync();
        }
    }

    private async void SaveQuickConsentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_quickSettings?.Approval is not { } approval || _controller is not IQuickLaunchController quick) return;
        var enabled = QuickConsentCheckBox.IsChecked == true;
        await ChangeQuickSettingsAsync(async () =>
        {
            _quickSettings = await quick.SetQuickLaunchConsentAsync(approval, enabled);
            QuickConsentCheckBox.IsChecked = _quickSettings.ConsentMatches;
            QuickSettingsResultText.Text = enabled ? "이 전용 프로필의 빠른 실행 동의를 저장했습니다." : "빠른 실행 동의를 해제했습니다.";
            QuickConsentScopeText.Text = $"전용 환경: {_quickSettings.Snapshot.BoxName}\n{_quickSettings.Reason}";
        });
    }

    private async void SaveShortcutsButton_Click(object sender, RoutedEventArgs e)
    {
        var selection = new UserLaunchOptions(DesktopShortcutCheckBox.IsChecked == true,
            StartMenuShortcutCheckBox.IsChecked == true, LoginStartupCheckBox.IsChecked == true);
        await SaveShortcutsAsync(selection);
    }

    private async void RemoveShortcutsButton_Click(object sender, RoutedEventArgs e) => await SaveShortcutsAsync(new());

    private Task SaveShortcutsAsync(UserLaunchOptions selection) => ChangeQuickSettingsAsync(() =>
    {
        if (_launchIntegration is null) throw new InvalidOperationException("바로가기 저장 위치를 먼저 확인하세요.");
        ApplyShortcutSettings(_launchIntegration.Apply(selection));
        QuickSettingsResultText.Text = "바로가기와 로그인 시 실행 선택을 저장하고 다시 확인했습니다.";
        return Task.CompletedTask;
    });

    private async void SaveAppearanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_appearanceSettings?.Plan is not { } plan || _controller is not ISecondaryAppearanceController appearance ||
            AppearanceColorCombo.SelectedItem is not ComboBoxItem { Tag: string color }) return;
        var selection = plan.WithSelection(color,
            AppearanceLabelCheckBox.IsChecked == true ? SecondaryAppearance.SecondaryLabel : null);
        await ChangeQuickSettingsAsync(async () =>
        {
            var state = await appearance.ApplySecondaryAppearanceAsync(selection);
            if (_quickSettings is not null) ApplyQuickSettings(_quickSettings, state);
            QuickSettingsResultText.Text = state.Message;
            // Cosmetic scope changes also invalidate earlier no-confirm consent.
            if (_controller is IQuickLaunchController quick)
            {
                _quickSettings = await quick.InspectQuickLaunchAsync();
                QuickConsentCheckBox.IsChecked = _quickSettings.ConsentMatches;
                QuickConsentScopeText.Text = $"전용 환경: {_quickSettings.Snapshot.BoxName}\n{_quickSettings.Reason}";
            }
        });
    }

    private async void ReloadQuickSettingsButton_Click(object sender, RoutedEventArgs e) => await LoadQuickSettingsAsync();

    private void AppearanceColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAppearancePreview();

    private void UpdateAppearancePreview()
    {
        if (AppearancePreview is null || AppearanceColorCombo.SelectedItem is not ComboBoxItem { Tag: string color }) return;
        AppearancePreview.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }
}
