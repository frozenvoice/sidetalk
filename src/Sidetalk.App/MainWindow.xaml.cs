using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Sidetalk.Core;

namespace Sidetalk.App;

public partial class MainWindow : Window
{
    private const string SandboxieDownloadUrl = "https://sandboxie-plus.com/downloads/";
    private const string KakaoOfficialUrl = "https://www.kakaocorp.com/page/service/service/KakaoTalk";
    private readonly ISidetalkController _controller;
    private readonly DispatcherTimer _inspectionTimer;
    private readonly ObservableCollection<string> _checks = new();
    private AppSnapshot? _lastSnapshot;
    private bool _inspectionInFlight;
    private bool _actionInFlight;
    private readonly bool _isRenderMode;

    public MainWindow(ISidetalkController controller, bool isRenderMode = false)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _isRenderMode = isRenderMode;
        InitializeComponent();
        ChecksList.ItemsSource = _checks;

        _inspectionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _inspectionTimer.Tick += InspectionTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_isRenderMode)
        {
            _inspectionTimer.Start();
            await InspectAndApplyAsync();
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _inspectionTimer.Stop();
    }

    private async void InspectionTimer_Tick(object? sender, EventArgs e)
    {
        await InspectAndApplyAsync();
    }

    private async void InspectButton_Click(object sender, RoutedEventArgs e)
    {
        await InspectAndApplyAsync();
    }

    private async Task InspectAndApplyAsync()
    {
        if (_inspectionInFlight)
        {
            return;
        }

        _inspectionInFlight = true;
        InspectButton.IsEnabled = false;
        try
        {
            ApplySnapshot(await _controller.InspectAsync());
        }
        catch (Exception exception)
        {
            ApplyError(exception);
        }
        finally
        {
            _inspectionInFlight = false;
            InspectButton.IsEnabled = true;
            ApplyActionAvailability();
        }
    }

    public void ApplySnapshot(AppSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _lastSnapshot = snapshot;

        StatusTitleText.Text = string.IsNullOrWhiteSpace(snapshot.Title)
            ? ReadinessTitle(snapshot.State)
            : snapshot.Title;
        StatusDetailText.Text = string.IsNullOrWhiteSpace(snapshot.Detail)
            ? ReadinessDetail(snapshot.State)
            : snapshot.Detail;
        StatusBadgeText.Text = ReadinessBadge(snapshot.State);

        BoxNameText.Text = DisplayValue(snapshot.BoxName);
        StoragePathText.Text = DisplayValue(snapshot.StoragePath);
        ProcessIdsText.Text = snapshot.BoxProcessIds.Count > 0
            ? $"{snapshot.BoxProcessIds.Count}개 · PID {string.Join(", ", snapshot.BoxProcessIds)}"
            : snapshot.State == Readiness.Ready ? "없음" : "프로세스 상태 미확인";
        CheckedAtText.Text = snapshot.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        ApplyDependency(KakaoAvailabilityText, KakaoInfoText, snapshot.Kakao);
        ApplyDependency(SandboxieAvailabilityText, SandboxieInfoText, snapshot.Sandboxie);

        _checks.Clear();
        if (snapshot.Checks.Count == 0)
        {
            _checks.Add("Sandboxie 연결 및 설정 적용 후 검사합니다.");
        }
        else
        {
            foreach (var check in snapshot.Checks)
            {
                _checks.Add(check);
            }
        }

        ApplyActionAvailability();
    }

    public void PrepareRender(bool setupTab, bool compactWindow)
    {
        if (setupTab)
        {
            MainTabs.SelectedIndex = 1;
        }

        if (compactWindow)
        {
            Width = 780;
            Height = 660;
        }
    }

    public void ApplyError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _lastSnapshot = null;
        StatusTitleText.Text = "상태 확인 실패";
        StatusBadgeText.Text = "확인 실패";
        StatusDetailText.Text = ReadableException(exception);
        BoxNameText.Text = "확인할 수 없음";
        StoragePathText.Text = "확인할 수 없음";
        ProcessIdsText.Text = "확인할 수 없음";
        CheckedAtText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        KakaoAvailabilityText.Text = "확인 실패";
        KakaoInfoText.Text = "진단을 실행해 오류 내용을 저장할 수 있습니다.";
        SandboxieAvailabilityText.Text = "확인 실패";
        SandboxieInfoText.Text = "진단을 실행해 오류 내용을 저장할 수 있습니다.";
        _checks.Clear();
        _checks.Add(ReadableException(exception));
        ApplyActionAvailability();
    }

    public void RenderToPng(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("렌더링할 PNG 경로가 비어 있습니다.", nameof(path));
        }

        UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);

        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(fullPath);
        encoder.Save(stream);
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_actionInFlight || _lastSnapshot?.CanLaunch != true)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "전용 Sandboxie Plus 상자에서 공식 카카오톡을 실행합니다.\n\n처음에는 전용 INI 파일의 데이터 분리 확인 내용을 읽고 직접 수락한 뒤, 카카오톡 창에서 보조 계정으로 로그인하고 기기 인증을 진행하세요. 메인 계정의 인증·대화 데이터는 복사하지 않습니다.\n\n실행을 계속할까요?",
            "보조 카카오톡 실행 확인",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information,
            MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _actionInFlight = true;
        ApplyActionAvailability();
        try
        {
            await _controller.LaunchAsync();
            StatusDetailText.Text = "실행 요청을 보냈습니다. 프로세스 상태를 다시 확인하고 있습니다.";
            await InspectAndApplyAsync();
        }
        catch (Exception exception)
        {
            ApplyError(exception);
        }
        finally
        {
            _actionInFlight = false;
            ApplyActionAvailability();
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_actionInFlight || _lastSnapshot?.CanStop != true)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "SIDETALK 전용 Sandboxie Plus 상자 안의 프로세스만 강제 종료합니다.\n\n카카오톡에서 먼저 직접 종료하세요. 강제 종료하면 저장되지 않은 작업이 사라질 수 있습니다. 보조 환경의 설정과 로컬 데이터는 삭제하지 않으며, 메인 카카오톡이나 다른 샌드박스는 건드리지 않습니다.\n\n강제 종료를 계속할까요?",
            "보조 카카오톡 종료 확인",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _actionInFlight = true;
        ApplyActionAvailability();
        try
        {
            await _controller.StopAsync();
            StatusDetailText.Text = "전용 상자의 종료 요청을 보냈습니다. 저장 데이터는 삭제하지 않았습니다.";
            await InspectAndApplyAsync();
        }
        catch (Exception exception)
        {
            ApplyError(exception);
        }
        finally
        {
            _actionInFlight = false;
            ApplyActionAvailability();
        }
    }

    private async void ExportSetupButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "전용 INI를 저장할 새 폴더 선택",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        SetSetupResult("전용 INI 파일을 저장하는 중…");
        ExportSetupButton.IsEnabled = false;
        try
        {
            var path = await _controller.ExportSetupAsync(dialog.FolderName);
            SetSetupResult($"전용 INI 파일을 저장했습니다: {path}");
        }
        catch (Exception exception)
        {
            SetSetupResult($"전용 INI 파일을 저장하지 못했습니다: {ReadableException(exception)}");
        }
        finally
        {
            ExportSetupButton.IsEnabled = true;
        }
    }

    private async void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "진단 JSON 저장",
            FileName = $"sidetalk-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            DefaultExt = ".json",
            AddExtension = true,
            Filter = "JSON 파일 (*.json)|*.json|모든 파일 (*.*)|*.*",
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        if (File.Exists(dialog.FileName)) { SetSetupResult("기존 진단 파일은 덮어쓰지 않습니다. 새 파일 이름을 선택하세요."); return; }
        SetSetupResult("진단 JSON을 저장하는 중…");
        ExportDiagnosticsButton.IsEnabled = false;
        try
        {
            var path = await _controller.ExportDiagnosticsAsync(dialog.FileName);
            SetSetupResult($"진단 JSON을 저장했습니다: {path}");
        }
        catch (Exception exception)
        {
            SetSetupResult($"진단 JSON을 저장하지 못했습니다: {ReadableException(exception)}");
        }
        finally
        {
            ExportDiagnosticsButton.IsEnabled = true;
        }
    }

    private void SandboxieDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOfficialPage(SandboxieDownloadUrl, "Sandboxie Plus 다운로드 페이지");
    }

    private void KakaoOfficialButton_Click(object sender, RoutedEventArgs e)
    {
        OpenOfficialPage(KakaoOfficialUrl, "카카오톡 공식 안내 페이지");
    }

    private void OpenOfficialPage(string url, string description)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetSetupResult($"{description}을 열지 못했습니다: {ReadableException(exception)}");
        }
    }

    private void ApplyActionAvailability()
    {
        LaunchButton.IsEnabled = !_actionInFlight && _lastSnapshot?.ActionsPermitted == true && _lastSnapshot.CanLaunch;
        StopButton.IsEnabled = !_actionInFlight && _lastSnapshot?.ActionsPermitted == true && _lastSnapshot.CanStop;
    }

    private static void ApplyDependency(TextBlock availability, TextBlock info, DependencyInfo dependency)
    {
        availability.Text = dependency.Available
            ? "확인됨"
            : string.IsNullOrWhiteSpace(dependency.Path) ? "없음" : "확인 필요";
        info.Text = BuildDependencyInfo(dependency);
        info.ToolTip = info.Text;
    }

    private static string BuildDependencyInfo(DependencyInfo dependency)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(dependency.Version))
        {
            parts.Add($"버전 {dependency.Version}");
        }

        if (!string.IsNullOrWhiteSpace(dependency.Path))
        {
            parts.Add(dependency.Path!);
        }

        if (!string.IsNullOrWhiteSpace(dependency.Detail))
        {
            parts.Add(dependency.Detail);
        }

        return parts.Count == 0 ? "확인할 정보 없음" : string.Join(" · ", parts);
    }

    private static string DisplayValue(string value) => string.IsNullOrWhiteSpace(value) ? "지정되지 않음" : value;

    private static string ReadinessTitle(Readiness state) => state switch
    {
        Readiness.MissingDependency => "필수 프로그램이 필요합니다",
        Readiness.SetupRequired => "초기 설정이 필요합니다",
        Readiness.Blocked => "실행이 차단되었습니다",
        Readiness.Ready => "보조 환경이 실행 준비되었습니다",
        Readiness.Running => "보조 프로세스 실행 관찰됨",
        Readiness.RunningUnverified => "실행 중 · 적용 정책 미확인",
        _ => "상태를 확인할 수 없습니다"
    };

    private static string ReadinessDetail(Readiness state) => state switch
    {
        Readiness.MissingDependency => "Sandboxie Plus 설치 상태를 확인한 뒤 초기 설정을 진행하세요.",
        Readiness.SetupRequired => "전용 Sandboxie Plus 상자와 저장 공간을 확인한 뒤 실행하세요.",
        Readiness.Blocked => "안전한 격리를 확인할 수 없어 실행을 막았습니다.",
        Readiness.Ready => "실행 버튼을 누르면 전용 상자에서 공식 카카오톡을 엽니다.",
        Readiness.Running => "프로세스를 관찰했습니다. 실제 적용 규칙과 로그인 성공 여부는 별도 확인이 필요합니다.",
        Readiness.RunningUnverified => "현재 설정과 기존 프로세스의 시작 당시 적용 규칙은 별개입니다.",
        _ => "다시 확인하거나 진단 JSON을 저장해 오류를 확인하세요."
    };

    private static string ReadinessBadge(Readiness state) => state switch
    {
        Readiness.MissingDependency => "필수 항목 필요",
        Readiness.SetupRequired => "설정 필요",
        Readiness.Blocked => "실행 차단",
        Readiness.Ready => "실행 가능",
        Readiness.Running => "실행 관찰됨",
        Readiness.RunningUnverified => "정책 미확인",
        _ => "확인 실패"
    };

    private void SetSetupResult(string message)
    {
        SetupResultText.Text = message;
        SetupResultText.ToolTip = message;
    }

    private static string ReadableException(Exception exception)
    {
        var message = exception.GetBaseException().Message.Trim();
        return string.IsNullOrWhiteSpace(message) ? "자세한 오류 내용을 확인할 수 없습니다." : message;
    }
}
