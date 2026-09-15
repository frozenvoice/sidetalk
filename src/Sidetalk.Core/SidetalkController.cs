using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sidetalk.Core;

public sealed class SidetalkController : ISidetalkController
{
    private readonly ProfileStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? pendingAction;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public SidetalkController() : this(new ProfileStore()) { }
    public SidetalkController(ProfileStore store) => this.store = store;

    public async Task<AppSnapshot> InspectAsync()
    {
        await gate.WaitAsync();
        try { return await Task.Run(Inspect); }
        finally { gate.Release(); }
    }
    private AppSnapshot Inspect()
    {
        var kakao = DependencyDetector.Kakao();
        var sandboxie = DependencyDetector.Sandboxie();
        BoxProfile? profile = null;
        try
        {
            profile = store.Load();
            if (!Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem || Environment.OSVersion.Version.Build < 22000)
                return Snapshot(Readiness.Blocked, "Windows 11 x64가 필요합니다", "지원 운영체제를 확인하세요.");
            using (var identity = WindowsIdentity.GetCurrent())
                if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    return Snapshot(Readiness.Blocked, "일반 권한으로 다시 열어주세요", "SIDETALK와 보조 카카오톡은 관리자 권한으로 실행하지 않습니다.");
            if (!kakao.Available || !sandboxie.Available)
                return Snapshot(Readiness.MissingDependency, "실행 준비가 필요해요", !sandboxie.Available ? "Sandboxie Plus를 확인한 뒤 전용 환경을 설정하세요." : "공식 카카오톡의 설치 경로와 서명을 확인하세요.");
            if (!RuntimeVerifier.MatchesVersion(sandboxie.Version, RuntimeVerifier.PlusVersion))
                return Snapshot(Readiness.Blocked, "Sandboxie 버전 검토가 필요해요", "이 빌드는 Sandboxie Plus 1.18.4의 API·설정을 기준으로 작성되었습니다. 설치 버전은 아직 검토되지 않았습니다.");
            if (profile is null)
                return Snapshot(Readiness.SetupRequired, "전용 환경을 준비하세요", "초기 설정에서 설정 파일을 내보내고 내용을 확인하세요. 다른 샌드박스는 가져오지 않습니다.");
            using var api = OpenApi(sandboxie);
            BuiltinTemplates.Verify(api);
            var config = api.ReadSection(profile.BoxName);
            if (config.Count == 0)
                return Snapshot(Readiness.SetupRequired, "설정 적용을 기다리고 있어요", "내보낸 전용 환경 설정을 Sandboxie Plus에 추가한 뒤 새로고침하세요.");
            var policy = IsolationPolicy.Validate(profile, config, api.ReadSection("GlobalSettings"));
            if (!policy.Safe)
                return Snapshot(Readiness.Blocked, "격리 설정을 확인해주세요", "안전을 확인할 수 없어 실행과 종료를 차단했습니다. 다른 샌드박스나 전역 설정은 변경하지 마세요.", policy.Issues);
            if (!SandboxieApi.SameNativePath(api.FileRoot(profile.BoxName), profile.StoragePath))
                return Snapshot(Readiness.Blocked, "저장 경로가 일치하지 않아요", "Sandboxie가 실제 사용하는 저장 경로와 전용 환경 경로가 다릅니다.");
            var processes = api.Processes(profile);
            if (pendingAction == "launch" && processes.Any(p => p.ImageName.Equals("KakaoTalk.exe", StringComparison.OrdinalIgnoreCase))) pendingAction = null;
            if (pendingAction == "stop" && processes.Count == 0) pendingAction = null;
            if (pendingAction is not null)
                return Snapshot(Readiness.Unknown, "명령 결과를 확인하고 있어요", "응답을 확정하지 못했습니다. 새로고침으로 상태를 확인하세요. 명령을 중복 실행하지 않습니다.", [], processes);
            var checks = new[] { "전용 환경 소유 정보와 실제 저장 경로 일치", "사용자 파일·레지스트리 숨김 규칙 확인", "자동 삭제·복구 비활성 설정 확인", "전역 설정·템플릿의 미검토 예외 없음", "실제 데이터 분리·로그인·송수신·알림은 사용자 검증 필요" };
            if (processes.Count > 0)
                return Snapshot(Readiness.Running, processes.Any(p => p.ImageName.Equals("KakaoTalk.exe", StringComparison.OrdinalIgnoreCase)) ? "보조 프로세스 실행 중" : "전용 환경 프로세스 실행 중", "프로세스 실행이 확인되었습니다. 로그인 상태는 공식 카카오톡 창에서 확인하세요.", checks, processes, canStop: true);
            return Snapshot(Readiness.Ready, "보조 카카오톡 실행 준비", "구성 검사가 통과했습니다. 첫 로그인 전에 데이터 분리 검증 절차를 진행하세요.", checks, [], canLaunch: true);
        }
        catch (Exception ex) { return Snapshot(Readiness.Unknown, "상태를 확인할 수 없어요", ex.Message); }

        AppSnapshot Snapshot(Readiness state, string title, string detail, IReadOnlyList<string>? checks = null, IReadOnlyList<BoxProcess>? processes = null, bool canLaunch = false, bool canStop = false) =>
            new(state, title, detail, profile?.BoxName ?? "아직 생성하지 않음", profile?.StoragePath ?? Path.Combine(store.Root, "Boxes"), kakao, sandboxie,
                processes?.Select(p => p.Id).ToArray() ?? [], checks ?? [], canLaunch, canStop, DateTimeOffset.Now);
    }
    private static SandboxieApi OpenApi(DependencyInfo sandboxie)
    {
        if (!sandboxie.Available || sandboxie.Path is null) throw new InvalidOperationException("Sandboxie Plus 설치와 서명을 확인하세요.");
        var folder = Path.GetDirectoryName(sandboxie.Path)!;
        RuntimeVerifier.RequireInstallation(folder);
        return new(folder);
    }

    public async Task<string> ExportSetupAsync(string folder)
    {
        await gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                ProfileStore.RejectReparseAncestors(folder);
                var profile = store.Create();
                var destination = Path.Combine(Path.GetFullPath(folder), $"{profile.BoxName}-{DateTime.Now:yyyyMMdd-HHmmss}.ini");
                Directory.CreateDirectory(folder);
                using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, new UTF8Encoding(true));
                writer.Write(IsolationPolicy.ToIni(profile));
                return destination;
            });
        }
        finally { gate.Release(); }
    }
    public Task LaunchAsync() => ExecuteAsync(true);
    public Task StopAsync() => ExecuteAsync(false);
    private async Task ExecuteAsync(bool launch)
    {
        await gate.WaitAsync();
        try
        {
            var snapshot = await Task.Run(Inspect);
            if (!(launch ? snapshot.CanLaunch : snapshot.CanStop)) throw new InvalidOperationException(snapshot.Detail);
            var profile = store.Load() ?? throw new InvalidOperationException("전용 환경 정보가 없습니다.");
            var directory = Path.GetDirectoryName(snapshot.Sandboxie.Path!)!;
            var startExe = Path.Combine(directory, "Start.exe");
            RuntimeVerifier.RequireComponent(startExe);
            Authenticode.RequirePublisher(snapshot.Kakao.Path!, "Kakao Corp.");
            var verified = await Task.Run(Inspect);
            if (!(launch ? verified.CanLaunch : verified.CanStop)) throw new InvalidOperationException(verified.Detail);
            var command = launch ? SandboxieCommands.Launch(startExe, profile.BoxName, snapshot.Kakao.Path!) : SandboxieCommands.Stop(startExe, profile.BoxName);
            pendingAction = launch ? "launch" : "stop";
            Process? process;
            try { process = Process.Start(command); }
            catch { pendingAction = null; throw; }
            if (process is null) { pendingAction = null; throw new InvalidOperationException("Sandboxie 명령을 시작하지 못했습니다."); }
            using (process)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { throw new InvalidOperationException("Sandboxie 명령 응답 시간이 초과되었습니다. 상태를 다시 확인하세요. 프로세스를 강제로 종료하지 않았습니다."); }
                if (process.ExitCode != 0) { pendingAction = null; throw new InvalidOperationException($"Sandboxie 명령이 오류 {process.ExitCode}를 반환했습니다. 상태를 다시 확인하세요."); }
            }
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var observed = await Task.Run(Inspect);
                if (pendingAction is null) return;
                if (observed.State is Readiness.Blocked or Readiness.MissingDependency) throw new InvalidOperationException(observed.Detail);
                await Task.Delay(500);
            }
            throw new InvalidOperationException("명령은 전달했지만 프로세스 상태를 확정하지 못했습니다. 로그인 성공으로 판단하지 마세요.");
        }
        finally { gate.Release(); }
    }
    public async Task<string> ExportDiagnosticsAsync(string path)
    {
        var snapshot = await InspectAsync();
        ProfileStore.RejectReparseAncestors(path);
        var report = new { SidetalkVersion = "0.1.0", PolicyRevision = IsolationPolicy.Revision, Windows = Environment.OSVersion.Version.ToString(), snapshot,
            Verification = new { ProcessStateIsLogin = false, AccountLogin = "미검증", SimultaneousAccounts = "미검증", Messaging = "미검증", Notifications = "미검증", Persistence = "미검증", MainAccountNonInterference = "미검증", RuntimeDataSeparation = "미검증" } };
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        await JsonSerializer.SerializeAsync(stream, report, Json);
        return path;
    }
    public void Dispose() { /* Closing SIDETALK never terminates any process or deletes any data. */ }
}
