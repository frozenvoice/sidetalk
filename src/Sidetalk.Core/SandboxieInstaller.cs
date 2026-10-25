using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Sidetalk.Core;

public sealed record SandboxieDownloadProgress(long Received, long Total, bool Verifying);

public enum SandboxieInstallerFailure { Refused, Network, Timeout, Redirect, Size, Integrity, Signature, Storage }

public sealed class SandboxieInstallerException(SandboxieInstallerFailure kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public SandboxieInstallerFailure Kind { get; } = kind;
}

public enum SandboxieInstallerOutcome { Exited, Cancelled, ElevationDeclined, RestartRequired, Failed }

/// <summary>What the official installer process reported. It is never evidence of a successful installation.</summary>
public sealed record SandboxieInstallerRun(SandboxieInstallerOutcome Outcome, int? ExitCode, string Detail);

public interface ISandboxieInstallerVerifier
{
    void Verify(string path, SandboxieInstallerRelease release);
}

public interface ISandboxieInstallerLauncher
{
    /// <summary>Starts the installer without elevating SIDETALK and returns its exit code.</summary>
    Task<int> RunAsync(string path, CancellationToken cancellationToken);
}

/// <summary>A downloaded installer whose size, SHA-256 and signature were verified while this handle denies writes.</summary>
public sealed class SandboxieVerifiedInstaller : IDisposable
{
    private FileStream? hold;

    internal SandboxieVerifiedInstaller(string directory, string filePath, FileStream hold)
    {
        WorkDirectory = directory;
        FilePath = filePath;
        this.hold = hold;
    }

    public string WorkDirectory { get; }
    public string FilePath { get; }

    public void Dispose()
    {
        hold?.Dispose();
        hold = null;
        SandboxieInstaller.TryDeleteWorkDirectory(WorkDirectory);
    }
}

/// <summary>
/// Downloads the single reviewed Sandboxie Plus installer from its official GitHub release, verifies it
/// against the pinned size, SHA-256, publisher signature and product version, and starts it for the
/// person to approve. It only runs when no Sandboxie installation or trace exists, never elevates
/// SIDETALK, and never touches Sandboxie services, drivers, settings or boxes.
/// </summary>
public sealed class SandboxieInstaller
{
    public const string WorkDirectoryPrefix = "SIDETALK-SandboxieSetup-";
    private const int MaxRedirects = 5;
    private const int ErrorCancelled = 1223;
    private static readonly string[] RedirectHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    private readonly HttpMessageHandler handler;
    private readonly ISandboxieInstallerVerifier verifier;
    private readonly ISandboxieInstallerLauncher launcher;
    private readonly Func<DependencyInfo> detect;
    private readonly string workRoot;
    private readonly TimeSpan timeout;
    private readonly TimeSpan stallTimeout;

    public SandboxieInstaller(
        SandboxieInstallerRelease release,
        HttpMessageHandler handler,
        ISandboxieInstallerVerifier verifier,
        ISandboxieInstallerLauncher launcher,
        Func<DependencyInfo> detect,
        string workRoot,
        TimeSpan? timeout = null,
        TimeSpan? stallTimeout = null)
    {
        RequireReviewedRelease(release);
        Release = release;
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        this.verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        this.launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        this.detect = detect ?? throw new ArgumentNullException(nameof(detect));
        this.workRoot = Path.GetFullPath(workRoot);
        this.timeout = timeout ?? TimeSpan.FromMinutes(10);
        this.stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(60);
    }

    public static SandboxieInstaller CreateDefault() => new(
        SandboxieSupport.Installer,
        new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(30), UseCookies = false },
        new AuthenticodeSandboxieInstallerVerifier(),
        new ShellSandboxieInstallerLauncher(),
        DependencyDetector.Sandboxie,
        Path.GetTempPath());

    public SandboxieInstallerRelease Release { get; }

    /// <summary>Only a machine with no Sandboxie installation or trace may be offered a fresh install.</summary>
    public static bool CanOffer(DependencyInfo sandboxie) =>
        sandboxie.Condition == DependencyCondition.NotInstalled && sandboxie.Path is null && !sandboxie.Available;

    public static bool IsOfficialDownload(Uri uri, SandboxieInstallerRelease release) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsolutePath.Equals($"/sandboxie-plus/Sandboxie/releases/download/v{release.Version}/{release.FileName}", StringComparison.Ordinal);

    public static bool IsTrustedRedirect(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        RedirectHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    public async Task<SandboxieVerifiedInstaller> DownloadAsync(IProgress<SandboxieDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        RequireOfferable("다운로드");
        CleanupStaleWorkDirectories();
        var directory = CreateWorkDirectory();
        var path = Path.Combine(directory, Release.FileName);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            using (var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan })
            using (var response = await GetFollowingTrustedRedirectsAsync(client, linked.Token))
            {
                if (response.Content.Headers.ContentLength is { } length && length != Release.Size)
                    throw new SandboxieInstallerException(SandboxieInstallerFailure.Size,
                        $"공식 서버가 알려 준 파일 크기({length:N0}바이트)가 검증된 설치 파일({Release.Size:N0}바이트)과 다릅니다.");
                await CopyBoundedAsync(response, path, progress, linked.Token);
            }

            progress?.Report(new SandboxieDownloadProgress(Release.Size, Release.Size, true));
            var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (hold.Length != Release.Size)
                    throw new SandboxieInstallerException(SandboxieInstallerFailure.Size, "저장된 설치 파일 크기가 검증 정보와 다릅니다.");
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(hold, linked.Token));
                if (!hash.Equals(Release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new SandboxieInstallerException(SandboxieInstallerFailure.Integrity,
                        "내려받은 파일의 SHA-256이 공식 릴리스의 검증 정보와 다릅니다. 파일을 실행하지 않고 삭제했습니다.");
                linked.Token.ThrowIfCancellationRequested();
                try { verifier.Verify(path, Release); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    throw new SandboxieInstallerException(SandboxieInstallerFailure.Signature,
                        "설치 파일의 공식 서명 또는 제품 버전을 확인하지 못했습니다. 파일을 실행하지 않고 삭제했습니다. " + error.Message, error);
                }
                return new SandboxieVerifiedInstaller(directory, path, hold);
            }
            catch
            {
                hold.Dispose();
                throw;
            }
        }
        catch (Exception error)
        {
            TryDeleteWorkDirectory(directory);
            if (error is SandboxieInstallerException) throw;
            if (error is OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException("다운로드를 취소했습니다. 임시 파일을 삭제했습니다.", error, cancellationToken);
                throw new SandboxieInstallerException(SandboxieInstallerFailure.Timeout,
                    $"다운로드가 제한 시간({timeout.TotalMinutes:0}분) 안에 끝나지 않아 중단했습니다. 네트워크 상태를 확인한 뒤 다시 시도하세요.", error);
            }
            throw new SandboxieInstallerException(SandboxieInstallerFailure.Storage,
                "설치 파일을 임시 폴더에 저장하지 못했습니다: " + error.Message, error);
        }
    }

    public async Task<SandboxieInstallerRun> RunAsync(SandboxieVerifiedInstaller installer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installer);
        using (installer)
        {
            // The download may take minutes; someone could have installed Sandboxie meanwhile.
            RequireOfferable("설치 프로그램 실행");
            int exitCode;
            try { exitCode = await launcher.RunAsync(installer.FilePath, cancellationToken); }
            catch (Win32Exception error) when (error.NativeErrorCode == ErrorCancelled)
            {
                return new SandboxieInstallerRun(SandboxieInstallerOutcome.ElevationDeclined, null,
                    "관리자 승인(UAC)이 거부되어 설치가 시작되지 않았습니다. 변경된 내용은 없습니다.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                return new SandboxieInstallerRun(SandboxieInstallerOutcome.Failed, null,
                    "공식 설치 프로그램을 시작하지 못했습니다: " + error.Message);
            }
            return Interpret(exitCode);
        }
    }

    /// <summary>Inno Setup exit codes of the official installer, as guidance only.</summary>
    public static SandboxieInstallerRun Interpret(int exitCode) => exitCode switch
    {
        0 => new(SandboxieInstallerOutcome.Exited, 0, "공식 설치 프로그램이 종료됐습니다. 설치 여부는 SIDETALK가 다시 확인합니다."),
        1 => new(SandboxieInstallerOutcome.Failed, 1, "설치 프로그램이 시작 단계에서 종료됐습니다(종료 코드 1). 관리자 승인(UAC)을 거부했거나 초기화에 실패했을 수 있습니다."),
        2 => new(SandboxieInstallerOutcome.Cancelled, 2, "설치를 시작하기 전에 취소됐습니다(종료 코드 2). 관리자 승인(UAC)을 거부한 경우도 여기에 해당할 수 있습니다."),
        5 => new(SandboxieInstallerOutcome.Cancelled, 5, "설치 도중 취소됐습니다(종료 코드 5). 공식 설치 프로그램이 변경을 되돌렸는지 SIDETALK가 다시 확인합니다."),
        8 => new(SandboxieInstallerOutcome.RestartRequired, 8, "공식 설치 프로그램이 Windows 재시작을 요구했습니다(종료 코드 8)."),
        3 or 4 or 6 or 7 => new(SandboxieInstallerOutcome.Failed, exitCode, $"공식 설치 프로그램이 오류로 종료됐습니다(종료 코드 {exitCode})."),
        _ => new(SandboxieInstallerOutcome.Failed, exitCode, $"공식 설치 프로그램이 알 수 없는 종료 코드 {exitCode}로 끝났습니다.")
    };

    private async Task<HttpResponseMessage> GetFollowingTrustedRedirectsAsync(HttpClient client, CancellationToken token)
    {
        var uri = Release.DownloadUri;
        for (var hop = 0; ; hop++)
        {
            HttpResponseMessage response;
            try { response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token); }
            catch (HttpRequestException error)
            {
                throw new SandboxieInstallerException(SandboxieInstallerFailure.Network,
                    "공식 배포 서버(GitHub)에 연결하지 못했습니다. 인터넷 연결을 확인한 뒤 다시 시도하세요.", error);
            }
            var code = (int)response.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null)
                    throw new SandboxieInstallerException(SandboxieInstallerFailure.Redirect, "공식 서버의 이동 응답에 위치가 없습니다.");
                if (hop >= MaxRedirects)
                    throw new SandboxieInstallerException(SandboxieInstallerFailure.Redirect, "공식 서버가 너무 여러 번 다른 위치로 이동시켜 다운로드를 중단했습니다.");
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!IsTrustedRedirect(next))
                    throw new SandboxieInstallerException(SandboxieInstallerFailure.Redirect,
                        $"공식 배포 출처가 아닌 위치({next.Scheme}://{next.Host})로 이동하려 해 다운로드를 중단했습니다.");
                uri = next;
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new SandboxieInstallerException(SandboxieInstallerFailure.Network,
                    $"공식 배포 서버가 HTTP {code}로 응답했습니다. 잠시 후 다시 시도하세요.");
            }
            return response;
        }
    }

    private async Task CopyBoundedAsync(HttpResponseMessage response, string path, IProgress<SandboxieDownloadProgress>? progress, CancellationToken token)
    {
        Stream source;
        try { source = await response.Content.ReadAsStreamAsync(token); }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new SandboxieInstallerException(SandboxieInstallerFailure.Network, "공식 서버에서 파일을 받는 중 연결이 끊겼습니다. 다시 시도하세요.", error);
        }
        await using (source)
        await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            var buffer = new byte[81920];
            long received = 0;
            while (true)
            {
                int read;
                using (var stall = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    stall.CancelAfter(stallTimeout);
                    try { read = await source.ReadAsync(buffer, stall.Token); }
                    catch (OperationCanceledException error) when (!token.IsCancellationRequested)
                    {
                        throw new SandboxieInstallerException(SandboxieInstallerFailure.Timeout,
                            $"다운로드가 {stallTimeout.TotalSeconds:0}초 동안 진행되지 않아 중단했습니다. 네트워크 상태를 확인한 뒤 다시 시도하세요.", error);
                    }
                    catch (Exception error) when (error is HttpRequestException or IOException)
                    {
                        throw new SandboxieInstallerException(SandboxieInstallerFailure.Network, "공식 서버에서 파일을 받는 중 연결이 끊겼습니다. 다시 시도하세요.", error);
                    }
                }
                if (read == 0) break;
                received += read;
                if (received > Release.Size)
                    throw new SandboxieInstallerException(SandboxieInstallerFailure.Size, "받은 데이터가 검증된 설치 파일 크기를 넘어 다운로드를 중단했습니다.");
                await target.WriteAsync(buffer.AsMemory(0, read), token);
                progress?.Report(new SandboxieDownloadProgress(received, Release.Size, false));
            }
            if (received != Release.Size)
                throw new SandboxieInstallerException(SandboxieInstallerFailure.Size, "다운로드가 끝나기 전에 연결이 끊겼습니다. 다시 시도하세요.");
            await target.FlushAsync(token);
        }
    }

    private void RequireOfferable(string stage)
    {
        DependencyInfo current;
        try { current = detect(); }
        catch (Exception error)
        {
            throw new SandboxieInstallerException(SandboxieInstallerFailure.Refused,
                $"현재 Sandboxie 설치 상태를 확인하지 못해 {stage}을(를) 시작하지 않았습니다.", error);
        }
        if (!CanOffer(current))
            throw new SandboxieInstallerException(SandboxieInstallerFailure.Refused,
                $"Sandboxie Plus가 이미 설치되어 있거나 설치 흔적이 있어 {stage}을(를) 시작하지 않았습니다. 기존 설치·설정·box는 그대로 둡니다.");
    }

    private string CreateWorkDirectory()
    {
        try
        {
            var directory = Path.Combine(workRoot, WorkDirectoryPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            ProfileStore.RejectReparseAncestors(directory);
            return directory;
        }
        catch (Exception error)
        {
            throw new SandboxieInstallerException(SandboxieInstallerFailure.Storage, "설치 파일을 받을 임시 폴더를 만들지 못했습니다: " + error.Message, error);
        }
    }

    private void CleanupStaleWorkDirectories()
    {
        try
        {
            if (!Directory.Exists(workRoot)) return;
            foreach (var directory in Directory.EnumerateDirectories(workRoot, WorkDirectoryPrefix + "*"))
                TryDeleteWorkDirectory(directory);
        }
        catch (Exception) { }
    }

    /// <summary>Deletes only a SIDETALK installer work folder; a link or an in-use file is left alone.</summary>
    internal static void TryDeleteWorkDirectory(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            if (!info.Exists || !info.Name.StartsWith(WorkDirectoryPrefix, StringComparison.Ordinal) ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return;
            if (info.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Any(item => (item.Attributes & FileAttributes.ReparsePoint) != 0))
                return;
            info.Delete(recursive: true);
        }
        catch (Exception) { }
    }

    private static void RequireReviewedRelease(SandboxieInstallerRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!IsOfficialDownload(release.DownloadUri, release))
            throw new ArgumentException("다운로드 주소가 공식 Sandboxie GitHub 릴리스 자산이 아닙니다.", nameof(release));
        if (release.ReleasePage.Scheme != Uri.UriSchemeHttps || !release.ReleasePage.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !release.ReleasePage.AbsolutePath.Equals($"/sandboxie-plus/Sandboxie/releases/tag/v{release.Version}", StringComparison.Ordinal))
            throw new ArgumentException("릴리스 페이지가 해당 버전의 공식 GitHub 릴리스가 아닙니다.", nameof(release));
        if (release.Size <= 0 || release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(release.Publisher))
            throw new ArgumentException("설치 파일 검증 정보가 완전하지 않습니다.", nameof(release));
    }
}

public sealed class AuthenticodeSandboxieInstallerVerifier : ISandboxieInstallerVerifier
{
    public void Verify(string path, SandboxieInstallerRelease release)
    {
        Authenticode.RequirePublisher(path, release.Publisher);
        var product = FileVersionInfo.GetVersionInfo(path).ProductVersion?.Trim();
        if (!string.Equals(product, release.Version, StringComparison.Ordinal))
            throw new InvalidDataException($"설치 파일의 제품 버전({product})이 지원 버전 {release.Version}과 다릅니다.");
    }
}

public sealed class ShellSandboxieInstallerLauncher : ISandboxieInstallerLauncher
{
    public async Task<int> RunAsync(string path, CancellationToken cancellationToken)
    {
        // No "runas" verb: SIDETALK stays unelevated and the official installer asks Windows for
        // the administrator approval it needs.
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path)!
        }) ?? throw new InvalidOperationException("설치 프로그램 프로세스를 만들지 못했습니다.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}
