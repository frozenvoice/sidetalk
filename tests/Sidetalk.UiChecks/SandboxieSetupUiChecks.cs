using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sidetalk.App;
using Sidetalk.Core;

// Sandboxie Plus installation guidance and the in-app install flow, on production WPF controls with
// a synthetic controller. Core's real SandboxieInstaller runs with an in-memory HTTP handler, a
// recording verifier and a fake process launcher: nothing is downloaded from the network, no
// installer is executed and the machine's Sandboxie installation is never read or changed.
internal static class SandboxieSetupUiChecks
{
    private const string SandMan = @"C:\Program Files\Sandboxie-Plus\SandMan.exe";
    private static readonly byte[] Payload = CreatePayload();
    private static int cases;

    public static void Run(App app, AppSnapshot ready, SetupRecoveryPlan healthy, string output)
    {
        var work = Path.Combine(output, "sandboxie-install-work");
        Directory.CreateDirectory(work);
        try
        {
            var metrics = new List<object>();
            RenderConditions(ready, healthy, output, work);
            RenderScaled(ready, healthy, output, work, metrics);
            HighContrast(app, ready, healthy, output, work);
            Flows(ready, healthy, output, work);
            Check(Directory.GetDirectories(work, SandboxieInstaller.WorkDirectoryPrefix + "*").Length == 0, "An installer work folder was left behind");
            File.WriteAllText(Path.Combine(output, "sandboxie-setup-metrics.json"), JsonSerializer.Serialize(new
            {
                Scope = "Synthetic controller and Core SandboxieInstaller with in-memory HTTP, recording verifier and fake launcher; no network, installer execution or real Sandboxie access",
                Cases = cases,
                Scales = metrics,
                Limitations = "LayoutTransform and RenderTargetBitmap scale probes; real monitor DPI, OS high contrast, UAC and the official installer are untested here."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Sandboxie setup UI checks passed: {cases} cases (conditions, scaled layout, injected high contrast, install flows).");
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    private static void RenderConditions(AppSnapshot ready, SetupRecoveryPlan healthy, string output, string work)
    {
        var titles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var condition in new[] { DependencyCondition.NotInstalled, DependencyCondition.PathUnresolved, DependencyCondition.SignatureInvalid,
                     DependencyCondition.UnsupportedVersion, DependencyCondition.ServiceUnavailable, DependencyCondition.Verified })
        {
            var snapshot = With(ready, condition);
            using var controller = new InstallController(snapshot, healthy);
            var window = Create(controller, work, out _);
            try
            {
                Show(window);
                var card = Find<Border>(window, "SandboxieSetupCard");
                var install = Find<Button>(window, "SandboxieInstallButton");
                var release = Find<Button>(window, "SandboxieReleasePageButton");
                var summary = Text(window, "SandboxieSummaryText");
                if (condition == DependencyCondition.Verified)
                {
                    Check(!card.IsVisible, "A verified installation still shows installation guidance");
                    Check(summary == "1.18.4 · 확인됨", $"Verified summary was '{summary}'");
                }
                else
                {
                    Check(card.IsVisible, $"{condition} does not show guidance on the main tab");
                    Check(install.IsVisible == (condition == DependencyCondition.NotInstalled), $"{condition} install button visibility is wrong");
                    Check(release.IsVisible && ((string)release.Content).Contains(SandboxieSupport.PlusVersion, StringComparison.Ordinal),
                        $"{condition} lacks the exact-version official release page");
                    Check(Text(window, "SandboxieSupportedVersionText").Contains($"Sandboxie Plus {SandboxieSupport.PlusVersion}", StringComparison.Ordinal),
                        $"{condition} hides the supported version");
                    Check(titles.Add(Text(window, "SandboxieSetupTitleText")), $"{condition} reuses another condition's title");
                    Check(condition == DependencyCondition.NotInstalled || !Text(window, "SandboxieSetupTitleText").Contains("설치가 필요", StringComparison.Ordinal),
                        $"{condition} is presented as missing");
                    if (condition == DependencyCondition.NotInstalled) Check(install.IsEnabled, "Install is not actionable on a clean machine");
                    AssertCardFits(window, $"{condition} default");
                }
                Check(summary == ExpectedSummary(condition), $"{condition} component summary was '{summary}'");
                if (condition == DependencyCondition.UnsupportedVersion)
                    Check(Text(window, "SandboxieSupportedVersionText").Contains("설치된 버전: 1.19.2", StringComparison.Ordinal), "Installed unsupported version is hidden");
                var name = condition.ToString().ToLowerInvariant();
                window.RenderToPng(Path.Combine(output, $"ui-sandboxie-{name}-fixture.png"));
                if (condition is DependencyCondition.NotInstalled or DependencyCondition.UnsupportedVersion)
                {
                    window.PrepareRender(false, true);
                    window.UpdateLayout();
                    AssertCardFits(window, $"{condition} minimum window");
                    window.RenderToPng(Path.Combine(output, $"ui-sandboxie-{name}-min-fixture.png"));
                }
                Check(controller.WritableCallCount == 0, "Rendering guidance invoked a controller write");
                cases++;
            }
            finally { window.Close(); }
        }
    }

    private static void RenderScaled(AppSnapshot ready, SetupRecoveryPlan healthy, string output, string work, List<object> metrics)
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            using var controller = new InstallController(With(ready, DependencyCondition.NotInstalled), healthy);
            var window = Create(controller, work, out _);
            try
            {
                var name = scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture).Replace('.', '_');
                window.PrepareRender(false, true);
                Show(window);
                var content = (FrameworkElement)window.Content;
                var card = Find<Border>(window, "SandboxieSetupCard");

                // Larger text in the minimum 740x640 window: the layout Windows gets at a higher scale.
                content.LayoutTransform = new ScaleTransform(scale, scale);
                window.UpdateLayout();
                card.BringIntoView();
                window.UpdateLayout();
                AssertCardFits(window, $"layout scale {scale}");
                // RenderToPng sizes the bitmap from the untransformed content, so capture the window itself.
                var layout = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                layout.Render(window);
                Save(layout, Path.Combine(output, $"ui-sandboxie-layout-{name}x-fixture.png"));
                var scaledCard = new { card.ActualWidth, card.ActualHeight };

                // The unscaled layout rendered at the matching DPI.
                content.LayoutTransform = Transform.Identity;
                window.UpdateLayout();
                AssertCardFits(window, $"DPI {96 * scale}");
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale),
                    96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(window);
                Save(bitmap, Path.Combine(output, $"ui-sandboxie-dpi-{name}x-fixture.png"));
                metrics.Add(new { Scale = scale, ScaledCardWidth = scaledCard.ActualWidth, ScaledCardHeight = scaledCard.ActualHeight,
                    Window = $"{window.ActualWidth}x{window.ActualHeight}", Bitmap = $"{bitmap.PixelWidth}x{bitmap.PixelHeight}" });
                cases++;
            }
            finally { window.Close(); }
        }
    }

    private static void HighContrast(App app, AppSnapshot ready, SetupRecoveryPlan healthy, string output, string work)
    {
        var originalBrushes = app.Resources.Keys.OfType<string>().Where(key => app.Resources[key] is SolidColorBrush)
            .ToDictionary(key => key, key => (Brush)app.Resources[key]);
        var colors = new Dictionary<string, Color>
        {
            ["Window"] = Colors.Black, ["WindowText"] = Colors.White, ["Highlight"] = Colors.DarkGreen,
            ["HighlightText"] = Colors.Yellow, ["GrayText"] = Colors.Silver, ["ControlText"] = Colors.Aqua
        };
        var contrast = true;
        using var controller = new InstallController(With(ready, DependencyCondition.NotInstalled), healthy);
        var window = Create(controller, work, out _);
        using var palette = new ThemePalette(app, () => contrast, role => colors[role]);
        try
        {
            Show(window);
            var title = Find<TextBlock>(window, "SandboxieSetupTitleText");
            var body = Find<TextBlock>(window, "SandboxieSetupBodyText");
            var install = Find<Button>(window, "SandboxieInstallButton");
            var release = Find<Button>(window, "SandboxieReleasePageButton");
            var progress = Find<ProgressBar>(window, "SandboxieDownloadProgress");
            Check(Color(title.Foreground) == colors["WindowText"] && Color(body.Foreground) == colors["WindowText"], "Guidance text kept a fixed color in contrast mode");
            Check(Color(install.Background) == colors["Highlight"] && Color(install.Foreground) == colors["HighlightText"], "Install button lost the OS highlight pair");
            Check(Color(release.Foreground) == colors["WindowText"] || Color(release.Foreground) == colors["ControlText"], "Release button ink ignored the OS palette");
            Check(Color(progress.Foreground) == colors["Highlight"] && Color(progress.Background) == colors["Window"] && Color(progress.BorderBrush) == colors["WindowText"],
                "Progress fill and track are not distinguishable with OS contrast colors");
            Check(install.Focusable && install.IsTabStop && release.IsTabStop, "Guidance buttons are not keyboard reachable");
            Check(AutomationName(Find<Border>(window, "SandboxieSetupCard")) == "Sandboxie Plus 설치 안내", "Guidance card has no accessible name");
            Check(System.Windows.Automation.AutomationProperties.GetLiveSetting(title) == System.Windows.Automation.AutomationLiveSetting.Polite &&
                System.Windows.Automation.AutomationProperties.GetLiveSetting(Find<TextBlock>(window, "SandboxieSetupStatusText")) == System.Windows.Automation.AutomationLiveSetting.Polite,
                "Stage and status changes are not announced");
            window.RenderToPng(Path.Combine(output, "ui-sandboxie-contrast-fixture.png"));
            cases++;
        }
        finally
        {
            palette.Dispose();
            foreach (var (key, brush) in originalBrushes) app.Resources[key] = brush;
            window.Close();
        }
    }

    private static void Flows(AppSnapshot ready, SetupRecoveryPlan healthy, string output, string work)
    {
        var missing = With(ready, DependencyCondition.NotInstalled);
        var verified = With(ready, DependencyCondition.Verified);

        // Success: consent, real download progress, verification, the wizard stage, then a fresh inspection.
        {
            using var controller = new InstallController(missing, healthy);
            var window = Create(controller, work, out var fixture);
            fixture.Launcher.Behaviour = _ =>
            {
                Check(Text(window, "SandboxieSetupTitleText") == "공식 설치 마법사를 진행하세요", "The wizard stage was not shown while the installer ran");
                Check(Find<Button>(window, "SandboxieReleasePageButton").Visibility != Visibility.Visible && Find<Button>(window, "SandboxieInstallButton").Visibility != Visibility.Visible,
                    "Competing actions stayed available while the installer ran");
                controller.Current = verified;
                return 0;
            };
            Prime(window, missing);
            Pump(StartInstall(window));
            Check(fixture.ConfirmCalls == 1 && fixture.ConfirmedVersion == SandboxieSupport.PlusVersion, "Consent was not requested once for the supported version");
            Check(fixture.Requests == 1 && fixture.Verifier.Calls == 1 && fixture.Launcher.Calls == 1, "Download, verification and launch did not each run once");
            Check(fixture.Launcher.SawFile, "The installer did not exist while it ran");
            Check(fixture.Progress.Count > 0, "The download stream produced no progress");
            Check(Text(window, "SandboxieSetupTitleText") == $"Sandboxie Plus {SandboxieSupport.PlusVersion} 설치를 확인했어요", "Verified installation was not confirmed after re-inspection");
            Check(Find<Button>(window, "SandboxieContinueSetupButton").Visibility == Visibility.Visible && Find<Button>(window, "SandboxieInstallButton").Visibility != Visibility.Visible,
                "Success did not lead to the setup step");
            Check(controller.InspectCount >= 2, "The result was not re-inspected after the installer exited");
            Find<Button>(window, "SandboxieContinueSetupButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(Find<TabControl>(window, "MainTabs").SelectedIndex == 1 && Find<Border>(window, "SandboxieSetupCard").Visibility == Visibility.Collapsed,
                "Continue did not open 초기 설정 · 진단");
            Check(controller.WritableCallCount == 0, "The install flow invoked launch, stop or recovery");
            window.Close();
            cases++;
        }

        // Declined consent downloads nothing.
        Expect(missing, healthy, work, fixture => fixture.Confirm = false, window =>
        {
            Check(Text(window, "SandboxieSetupStatusText").Contains("설치를 시작하지 않았습니다", StringComparison.Ordinal), "Declined consent was not explained");
            Check(Find<Button>(window, "SandboxieInstallButton").Visibility == Visibility.Visible, "Install was not offered again after declining");
        }, requests: 0, launches: 0);

        // An installation trace found by the fresh read is never offered an install.
        {
            using var controller = new InstallController(missing, healthy);
            var window = Create(controller, work, out var fixture);
            Prime(window, missing);
            controller.Current = With(ready, DependencyCondition.PathUnresolved);
            Pump(StartInstall(window));
            Check(fixture.ConfirmCalls == 0 && fixture.Requests == 0 && fixture.Launcher.Calls == 0, "An existing installation trace reached consent or download");
            Check(Find<Button>(window, "SandboxieInstallButton").Visibility != Visibility.Visible, "Install stayed offered over an existing installation");
            Check(Text(window, "SandboxieSetupTitleText") == "Sandboxie Plus 설치 위치를 확인하지 못했어요", "The existing installation condition was not shown");
            window.Close();
            cases++;
        }

        Expect(missing, healthy, work, fixture => fixture.Body = Tampered(), window => AssertFailed(window, "설치 파일 검증에 실패했어요", "SHA-256"), requests: 1, launches: 0);
        Expect(missing, healthy, work, fixture => fixture.Verifier.Fail = true, window => AssertFailed(window, "설치 파일 검증에 실패했어요", "공식 서명"), requests: 1, launches: 0);
        Expect(missing, healthy, work, fixture => fixture.Offline = true, window => AssertFailed(window, "설치 파일을 받지 못했어요", "인터넷 연결"), requests: 1, launches: 0);
        Expect(missing, healthy, work, fixture => fixture.Launcher.Behaviour = _ => throw new Win32Exception(1223),
            window => AssertFailed(window, "설치가 취소됐어요", "관리자 승인(UAC)"), requests: 1, launches: 1);
        Expect(missing, healthy, work, fixture => fixture.Launcher.Behaviour = _ => 2,
            window => AssertFailed(window, "설치가 취소됐어요", "종료 코드 2"), requests: 1, launches: 1);
        Expect(missing, healthy, work, fixture => fixture.Launcher.Behaviour = _ => throw new Win32Exception(2),
            window => AssertFailed(window, "설치를 마치지 못했어요", "시작하지 못했습니다"), requests: 1, launches: 1);
        Expect(missing, healthy, work, fixture => fixture.Launcher.Behaviour = _ => 0,
            window => AssertFailed(window, "설치를 확인하지 못했어요", "찾지 못했습니다"), requests: 1, launches: 1);

        // Restart required: installed, but the service is not running yet.
        {
            using var controller = new InstallController(missing, healthy);
            var window = Create(controller, work, out var fixture);
            fixture.Launcher.Behaviour = _ => { controller.Current = With(ready, DependencyCondition.ServiceUnavailable); return 8; };
            Show(window);
            Prime(window, missing);
            Pump(StartInstall(window));
            Check(Text(window, "SandboxieSetupTitleText") == "Windows를 다시 시작해야 할 수 있어요", "Restart guidance missing");
            Check(Text(window, "SandboxieSetupBodyText").Contains("서비스를 시작하거나 재시작하지 않습니다", StringComparison.Ordinal), "Service non-interference not stated");
            Check(Find<Button>(window, "SandboxieInstallButton").Visibility != Visibility.Visible, "Reinstall offered over an installed Sandboxie");
            window.RenderToPng(Path.Combine(output, "ui-sandboxie-restart-fixture.png"));
            window.Close();
            cases++;
        }

        // Sandboxie appeared while downloading: Core refuses to launch over it.
        {
            using var controller = new InstallController(missing, healthy);
            var window = Create(controller, work, out var fixture);
            fixture.OnServe = () => controller.Current = With(ready, DependencyCondition.UnsupportedVersion);
            Prime(window, missing);
            Pump(StartInstall(window));
            Check(fixture.Launcher.Calls == 0, "The installer ran over an installation that appeared during download");
            Check(Text(window, "SandboxieSetupTitleText") == "지원하지 않는 Sandboxie 버전이에요", "The appeared installation was not presented");
            Check(Text(window, "SandboxieSetupStatusText").Contains("기존 설치", StringComparison.Ordinal), "The refusal was not explained");
            window.Close();
            cases++;
        }

        // Cancelling the download deletes the partial file and offers the install again.
        {
            using var controller = new InstallController(missing, healthy);
            var window = Create(controller, work, out var fixture);
            fixture.Stall = true;
            Show(window);
            Prime(window, missing);
            var flow = StartInstall(window);
            var cancel = Find<Button>(window, "SandboxieCancelDownloadButton");
            PumpUntil(() => cancel.Visibility == Visibility.Visible && cancel.IsEnabled && fixture.Progress.Count > 0, "download in progress");
            window.RenderToPng(Path.Combine(output, "ui-sandboxie-downloading-fixture.png"));
            Check(Find<ProgressBar>(window, "SandboxieDownloadProgress").Visibility == Visibility.Visible && Text(window, "SandboxieSetupStatusText").StartsWith("다운로드 중", StringComparison.Ordinal),
                "Download progress is not visible");
            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(flow);
            Check(Text(window, "SandboxieSetupStatusText").Contains("다운로드를 취소했습니다", StringComparison.Ordinal), "Cancellation was not explained");
            Check(fixture.Launcher.Calls == 0 && Find<Button>(window, "SandboxieInstallButton").Visibility == Visibility.Visible, "Cancelled download launched or stopped offering the install");
            window.Close();
            cases++;
        }
    }

    private static void Expect(AppSnapshot missing, SetupRecoveryPlan healthy, string work, Action<Fixture> arrange, Action<MainWindow> assert, int requests, int launches)
    {
        using var controller = new InstallController(missing, healthy);
        var window = Create(controller, work, out var fixture);
        arrange(fixture);
        Prime(window, missing);
        Pump(StartInstall(window));
        Check(fixture.Requests == requests && fixture.Launcher.Calls == launches,
            $"Expected {requests} download(s) and {launches} launch(es), got {fixture.Requests} and {fixture.Launcher.Calls}");
        Check(!Text(window, "SandboxieSetupTitleText").Contains("확인했어요", StringComparison.Ordinal), "A failed or cancelled attempt was reported as installed");
        Check(controller.WritableCallCount == 0, "The install flow invoked launch, stop or recovery");
        assert(window);
        window.Close();
        cases++;
    }

    private static void AssertFailed(MainWindow window, string title, string reason)
    {
        Check(Text(window, "SandboxieSetupTitleText") == title, $"Failure title was '{Text(window, "SandboxieSetupTitleText")}', expected '{title}'");
        Check(Text(window, "SandboxieSetupStatusText").Contains(reason, StringComparison.Ordinal), $"Failure reason lacks '{reason}': {Text(window, "SandboxieSetupStatusText")}");
        var retry = Find<Button>(window, "SandboxieInstallButton");
        Check(retry.Visibility == Visibility.Visible && retry.IsEnabled && (string)retry.Content == "다시 시도", "Retry is not offered after a failure on a clean machine");
        Check(Find<Button>(window, "SandboxieReleasePageButton").Visibility == Visibility.Visible, "Manual official release fallback is hidden after a failure");
    }

    private static void AssertCardFits(MainWindow window, string label)
    {
        var card = Find<Border>(window, "SandboxieSetupCard");
        var cardBounds = card.TransformToAncestor(window).TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
        Check(cardBounds.Right <= window.ActualWidth + 1, $"{label}: the guidance card is wider than the window");
        foreach (var element in FindVisualChildren<FrameworkElement>(card).Where(element => element.IsVisible && element is Button or TextBlock))
        {
            var bounds = element.TransformToAncestor(card).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            Check(bounds.Left >= -1 && bounds.Right <= card.ActualWidth + 1, $"{label}: {element.Name} '{Describe(element)}' extends outside the card");
            if (element is Button button)
                // DesiredSize includes the margin; ActualWidth does not.
                Check(button.ActualWidth + button.Margin.Left + button.Margin.Right + 0.5 >= button.DesiredSize.Width &&
                      button.ActualHeight + button.Margin.Top + button.Margin.Bottom + 0.5 >= button.DesiredSize.Height,
                    $"{label}: button '{button.Content}' is clipped");
            if (element is TextBlock text && text.TextWrapping == TextWrapping.NoWrap && text.TemplatedParent is null)
                Check(text.ActualWidth + 0.5 >= text.DesiredSize.Width, $"{label}: '{text.Text}' is clipped");
        }
    }

    private static string Describe(FrameworkElement element) => element switch
    {
        Button button => button.Content?.ToString() ?? "",
        TextBlock text => text.Text,
        _ => element.GetType().Name
    };

    private static AppSnapshot With(AppSnapshot ready, DependencyCondition condition)
    {
        DependencyInfo sandboxie = condition switch
        {
            DependencyCondition.NotInstalled => new("Sandboxie Plus", null, "미설치", "설치 경로를 찾지 못했습니다.", false),
            DependencyCondition.PathUnresolved => new("Sandboxie Plus", null, "", "Sandboxie 설치 흔적은 있지만 SandMan.exe를 찾지 못했습니다.", false),
            DependencyCondition.SignatureInvalid => new("Sandboxie Plus", SandMan, "1.18.4", "공식 서명을 검증하지 못했습니다 (0x800B0100): SandMan.exe", false),
            DependencyCondition.UnsupportedVersion => new("Sandboxie Plus", SandMan, "1.19.2", "설치됨 · 배포자 서명 확인 · 지원 버전 1.18.4와 다름", true),
            DependencyCondition.ServiceUnavailable => new("Sandboxie Plus", SandMan, "1.18.4", "설치됨 · 배포자 서명 확인 · Sandboxie 서비스(SbieSvc)가 실행 중이 아닙니다.", true),
            _ => new("Sandboxie Plus", SandMan, "1.18.4", "설치됨 · 배포자 서명 확인", true)
        };
        sandboxie = sandboxie with { Condition = condition };
        var state = condition switch
        {
            DependencyCondition.NotInstalled or DependencyCondition.PathUnresolved or DependencyCondition.SignatureInvalid => Readiness.MissingDependency,
            DependencyCondition.Verified => Readiness.SetupRequired,
            _ => Readiness.Blocked
        };
        return ready with
        {
            State = state,
            Title = state == Readiness.SetupRequired ? "전용 환경을 준비하세요" : state == Readiness.Blocked ? "Sandboxie 상태를 확인해주세요" : "실행 준비가 필요해요",
            Detail = "화면 검증용 합성 상태입니다.",
            Sandboxie = sandboxie,
            CanLaunch = false,
            CanStop = false,
            CurrentConfigurationVerified = false,
            ProcessPolicy = ProcessPolicyStatus.NotObserved,
            Checks = []
        };
    }

    private static string ExpectedSummary(DependencyCondition condition) => condition switch
    {
        DependencyCondition.NotInstalled => "설치 필요",
        DependencyCondition.PathUnresolved => "설치 위치 확인 필요",
        DependencyCondition.SignatureInvalid => "서명 확인 실패",
        DependencyCondition.UnsupportedVersion => "1.19.2 · 지원 버전 아님",
        DependencyCondition.ServiceUnavailable => "1.18.4 · 서비스 확인 필요",
        _ => "1.18.4 · 확인됨"
    };

    private static MainWindow Create(InstallController controller, string work, out Fixture fixture)
    {
        var created = new Fixture();
        fixture = created;
        var release = SandboxieSupport.Installer with { Size = Payload.Length, Sha256 = Convert.ToHexString(SHA256.HashData(Payload)) };
        var installer = new SandboxieInstaller(release, new FixtureHandler(created), created.Verifier, created.Launcher,
            () => controller.Current.Sandboxie, work, stallTimeout: TimeSpan.FromSeconds(30));
        var window = new MainWindow(controller, installer, offered =>
        {
            created.ConfirmCalls++;
            created.ConfirmedVersion = offered.Version;
            return created.Confirm;
        })
        { ShowActivated = false };
        return window;
    }

    // As a real click does, the flow starts inside a dispatcher operation.
    private static Task StartInstall(MainWindow window) => window.Dispatcher.InvokeAsync(window.InstallSandboxieAsync).Task.Unwrap();

    private static void Prime(MainWindow window, AppSnapshot snapshot)
    {
        window.ApplySnapshot(snapshot);
        Check(Find<Button>(window, "SandboxieInstallButton").Visibility == Visibility.Visible, "Install is not offered before the flow starts");
    }

    private static void Show(MainWindow window)
    {
        window.Show();
        Pump(window.RefreshNowAsync());
        window.Dispatcher.Invoke(() => window.UpdateLayout(), DispatcherPriority.Render);
    }

    private static byte[] CreatePayload()
    {
        var bytes = new byte[300_000];
        new Random(1184).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Tampered()
    {
        var bytes = (byte[])Payload.Clone();
        bytes[1234] ^= 0x5A;
        return bytes;
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static string AutomationName(DependencyObject element) => System.Windows.Automation.AutomationProperties.GetName(element);
    private static Color Color(Brush brush) => ((SolidColorBrush)brush).Color;

    private static T Find<T>(MainWindow window, string? name, DependencyObject? descendant = null) where T : DependencyObject
    {
        if (name is not null)
            return window.FindName(name) as T ?? throw new InvalidOperationException($"Missing production control {name} ({typeof(T).Name})");
        for (var current = descendant; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        throw new InvalidOperationException($"No ancestor {typeof(T).Name}");
    }

    private static string Text(MainWindow window, string name) => Find<TextBlock>(window, name).Text;

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            watchdog.Tick += (_, _) => { watchdog.Stop(); frame.Continue = false; };
            task.GetAwaiter().OnCompleted(() => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
            watchdog.Start();
            Dispatcher.PushFrame(frame);
            watchdog.Stop();
            Check(task.IsCompleted, "Sandboxie install flow exceeded the deadlock watchdog");
        }
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Check(DateTime.UtcNow < deadline, $"Timed out waiting for {what}");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Sandboxie setup fixture: " + message);
    }

    private sealed class Fixture
    {
        public bool Confirm { get; set; } = true;
        public int ConfirmCalls { get; set; }
        public string? ConfirmedVersion { get; set; }
        public int Requests { get; set; }
        public byte[] Body { get; set; } = Payload;
        public bool Offline { get; set; }
        public bool Stall { get; set; }
        public Action? OnServe { get; set; }
        public List<long> Progress { get; } = [];
        public RecordingVerifier Verifier { get; } = new();
        public FakeLauncher Launcher { get; } = new();
    }

    private sealed class FixtureHandler(Fixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            fixture.Requests++;
            Check(request.RequestUri == SandboxieSupport.Installer.DownloadUri, "The flow requested something other than the official asset");
            if (fixture.Offline) throw new HttpRequestException("fixture offline");
            fixture.OnServe?.Invoke();
            HttpContent content = fixture.Stall ? new StreamContent(new ProgressStream(fixture.Body, fixture, stallAfter: 65_536)) : new StreamContent(new ProgressStream(fixture.Body, fixture));
            if (!fixture.Stall) content.Headers.ContentLength = fixture.Body.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class ProgressStream(byte[] content, Fixture fixture, int stallAfter = -1) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stallAfter >= 0 && position >= stallAfter) await Task.Delay(Timeout.Infinite, cancellationToken);
            var count = Math.Min(Math.Min(buffer.Length, 16_384), content.Length - position);
            content.AsMemory(position, count).CopyTo(buffer);
            position += count;
            if (count > 0) lock (fixture.Progress) fixture.Progress.Add(position);
            await Task.Yield();
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingVerifier : ISandboxieInstallerVerifier
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public void Verify(string path, SandboxieInstallerRelease release)
        {
            Calls++;
            if (Fail) throw new InvalidDataException("공식 서명을 검증하지 못했습니다 (fixture)");
        }
    }

    private sealed class FakeLauncher : ISandboxieInstallerLauncher
    {
        public int Calls { get; private set; }
        public bool SawFile { get; private set; }
        public Func<string, int> Behaviour { get; set; } = _ => 0;
        public async Task<int> RunAsync(string path, CancellationToken cancellationToken)
        {
            Calls++;
            SawFile = File.Exists(path);
            await Task.Yield();
            return Behaviour(path);
        }
    }

    private sealed class InstallController(AppSnapshot initial, SetupRecoveryPlan plan) : ISidetalkController
    {
        public AppSnapshot Current { get; set; } = initial;
        public int InspectCount { get; private set; }
        public int WritableCallCount { get; private set; }
        public string RecoveryHistoryPath => plan.HistoryPath;
        public Task<AppSnapshot> InspectAsync()
        {
            InspectCount++;
            return Task.FromResult(Current with { CheckedAt = DateTimeOffset.Now });
        }
        public Task<SetupRecoveryPlan> InspectSetupRecoveryAsync() => Task.FromResult(plan);
        public Task<SetupRecoveryEvidenceInspection> InspectInterruptedEvidenceAsync() => Task.FromResult(new SetupRecoveryEvidenceInspection([], false, false, "중단 흔적 없음", "", "", ""));
        public Task<AppSnapshot> ReconcileInterruptedEvidenceAsync(SetupRecoveryEvidenceInspection approvedInspection, IProgress<string>? progress = null) => Write();
        public Task<AppSnapshot> PrepareAsync(SetupRecoveryPlan approvedPlan, bool allowAutomatic, IProgress<string>? progress = null) => Write();
        public Task<AppSnapshot> PrepareAutomaticallyAsync(IProgress<string>? progress = null) => Write();
        public Task RevokeAutomaticRecoveryAsync() => Write();
        public Task LaunchAsync() => Write();
        public Task StopAsync() => Write();
        public Task<string> ExportSetupAsync(string folder) => throw new InvalidOperationException("Not used");
        public Task<string> ExportDiagnosticsAsync(string path) => throw new InvalidOperationException("Not used");
        public void Dispose() { }
        private Task<AppSnapshot> Write()
        {
            WritableCallCount++;
            return Task.FromResult(Current);
        }
    }
}
