using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Sidetalk.App;

internal sealed class QuickLaunchWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly TextBlock progress;
    private readonly Button cancel;
    private bool settled;

    internal CancellationToken Cancellation => cancellation.Token;

    internal QuickLaunchWindow()
    {
        Title = "보조 계정 · SIDETALK 빠른 실행";
        Width = 480;
        Height = 265;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        SetResourceReference(ForegroundProperty, "InkBrush");
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        var panel = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = "보조 계정", FontSize = 23, FontWeight = FontWeights.SemiBold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        panel.Children.Add(heading);
        progress = new TextBlock { Text = "전용 환경 확인 중…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 12) };
        panel.Children.Add(progress);
        var guidance = new TextBlock { Text = "로그인·송수신은 카카오톡에서 직접 확인하세요.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        guidance.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
        panel.Children.Add(guidance);
        cancel = new Button { Content = "요청 취소", Style = (Style)Application.Current.FindResource("ActionButton"),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        cancel.Click += (_, _) => RequestCancellation();
        panel.Children.Add(cancel);
        Content = panel;
        Closing += OnClosing;
        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
    }

    internal void Report(string message)
    {
        if (!settled && !cancellation.IsCancellationRequested) progress.Text = message;
    }

    internal void RequestCancellation()
    {
        if (settled || cancellation.IsCancellationRequested) return;
        cancellation.Cancel();
        cancel.IsEnabled = false;
        progress.Text = "취소 요청을 받았습니다. 이미 보낸 명령은 결과와 기록을 확인한 뒤 마칩니다.";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (settled) return;
        e.Cancel = true;
        RequestCancellation();
    }

    internal void Complete()
    {
        settled = true;
        Close();
        cancellation.Dispose();
    }
}
