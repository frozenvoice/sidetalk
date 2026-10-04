# .NET 10 LTS 이전 — 2026-10-05

기준은 `81477439bd735bd440446bc2b3fcab163159f503`이며 시작 시 로컬 HEAD와 원격 main이 같고 작업 트리는 깨끗했다. `codex/dotnet10-lts`에서 변경했다. 이 문서는 이전의 .NET 8 실계정/VM 결과를 .NET 10 성공으로 취급하지 않는다. 실행 결과는 [VERIFICATION.md](VERIFICATION.md)에 기록한다.

## SDK와 런타임

- 앱·Core·IsolationProbe·콘솔 테스트·WPF 합성 검사 모두 `net10.0-windows`로 이전했다.
- `global.json`: SDK `10.0.401`, `rollForward=latestFeature`, `allowPrerelease=false`. 설치된 10.0 안정 SDK에서 이 버전 이상을 선택하며 다음 major나 preview를 자동 선택하지 않는다. C# 언어 버전은 기존 12를 유지한다.
- framework-dependent 배포는 **.NET 10 Desktop Runtime x64**가 필요하다. self-contained 배포는 .NET 10 런타임을 포함한다. 기존 배포 폴더·앱 버전·설치 경로는 변경하지 않는다.
- Windows CI는 이미 `setup-dotnet`의 `global-json-file`을 사용하므로 별도 SDK 버전을 중복 지정하지 않는다. 기존 Windows Server 2022, 고정된 Actions 버전, 읽기 전용 권한과 순차 검증을 유지한다.
- 루트 CMD의 SDK 설치 안내를 갱신하고 모의 저장소도 `global.json`을 복사해 같은 SDK 선택 경로를 검사한다. 자동 SDK/런타임 설치는 추가하지 않는다.

[Microsoft 지원 정책](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)에 따르면 .NET 10은 2028-11-14까지 지원되는 LTS이며, .NET 8 지원은 2026-11-10 종료된다. 지원을 받으려면 최신 패치를 유지해야 한다. [공식 다운로드](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)에서 확인한 SDK 10.0.401에는 런타임 10.0.12가 포함된다. self-contained 배포는 런타임 패치 때 다시 publish해야 한다.

## 호환성 검토

| 경계 | 적용 결과 |
|---|---|
| Windows/WPF | [공식 OS 목록](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)의 Windows 11 x64와 Windows Server 2022가 해당한다. WPF는 기존 테마를 유지한다. 빈 Grid 정의, 잘못된 DynamicResource, WinForms와의 이름 충돌, BinaryFormatter/clipboard 사용은 없다. |
| 네이티브 호출 | 기존 구조체·호출 규약·Win32 토큰/프로세스/DPAPI API를 유지한다. Sandboxie DLL은 검증한 절대 경로에 `LoadLibraryEx`와 제한된 탐색 플래그를 사용한다. 일반 디렉터리 publish이며 single-file·trim·AOT를 도입하지 않는다. |
| Authenticode | .NET 9부터 폐기된 인증서 추출은 [Microsoft runtime 안내](https://github.com/dotnet/runtime/discussions/108740)에 따라 Authenticode 형식을 확인한 후 한 줄에만 SYSLIB0057을 억제한다. `WinVerifyTrust`, reparse 거부와 허용 배포자 비교는 그대로다. 서명된 런타임의 임시 복사본, 변조본, 잘못된 배포자, 인증서/PFX 위장 파일을 검사한다. |
| 명령 실행 | `UseShellExecute=false`·`ArgumentList`, 명시적 전용 box, 실행 파일·절대 경로·설치 서명/버전 검사를 유지한다. 범용 shell 문자열을 도입하지 않는다. 기존 UI의 폴더/도움말 열기는 격리 명령과 별개다. |
| 명령·복구 상태 | 사용자/세션 단일 실행 mutex, 동일 CommandJournal 잠금, 프로세스 생성 전 intent 기록, PID/시각/경로 확인, timeout 차단과 명령 종료 후 box 재조회 조건을 유지한다. 과거 Completed 원본 보존만으로 완료하지 않으며 새 검증과 유효한 완료 작업 참조가 필요하다. |
| Sandboxie | 1.18.4와 기존 서명·혼합 드라이버 버전 기준 및 템플릿 해시를 그대로 사용한다. 실제 DLL/API/서비스/드라이버 통합은 합성 검사와 구별한다. |

[.NET 9 변경 목록](https://learn.microsoft.com/en-us/dotnet/core/compatibility/9.0)과 [.NET 10 변경 목록](https://learn.microsoft.com/en-us/dotnet/core/compatibility/10.0)의 WPF·interop·cryptography·serialization·SDK 항목을 검토했다. 현재 영속 모델에는 .NET 10 System.Text.Json의 속성 이름 충돌 검사에 해당하는 중복 이름이 없다.

## 데이터와 복구

`%LOCALAPPDATA%\SIDETALK` 아래 `profile.json`(schema 1), `Boxes`, `command-journal`(schema 1/2), `setup-recovery`, `recovery-evidence`의 경로·형식·소유 식별자를 변경하지 않는다. 별도로 저장되는 `AppState` 파일/타입은 현재 구현에 없으며 UI 상태는 프로필·설치·설정·명령/복구 근거를 재조회해 계산한다. 상태 초기화나 별도 마이그레이션 쓰기를 추가하지 않는다.

기존 .NET 8 Core의 실제 직렬화기로 만든 합성 JSON을 현재 런타임에서 읽는다. 생성 기준과 재현은 [fixture 생성 스크립트](../tests/Sidetalk.Tests/Fixtures/generate-net8-persistence.ps1)를 따른다. 테스트는 실행마다 새 저장소를 쓰고 경로/SID만 시험 환경으로 치환한다. 실제 사용자 프로필, 계정, box, INI, 설치본을 fixture로 쓰지 않는다. 중단·손상 파일 보존, schema 1 호환성, 중복 명령 차단, 실패/timeout, Completed 재조정은 기존 회귀도 계속 실행한다.

## 재현

Windows x64, PowerShell 7, `global.json`에 맞는 SDK를 준비하고 저장소 루트에서 순차 실행한다.

```powershell
pwsh -NoProfile -File scripts/verify.ps1 -UiChecks
pwsh -NoProfile -File tests/Sidetalk.BuildChecks/Program.ps1
```

운영 배포본 교체 없는 실제 두 모드 publish/ZIP 검사는 다음처럼 새 임시 소스 복사본에서 실행한다. 위 검증과 아래 publish를 동시에 실행하지 않는다. 이 경로에서는 전체 회귀를 다시 실행하거나 검사 생략 옵션을 추가하지 않는다.

```powershell
$ErrorActionPreference = 'Stop'
$source = (Get-Location).Path
$packageRoot = Join-Path ([IO.Path]::GetTempPath()) ('sidetalk-net10-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $packageRoot | Out-Null
$files = & git -c core.quotepath=false ls-files --cached --others --exclude-standard
if ($LASTEXITCODE -ne 0) { throw 'Source inventory failed.' }
foreach ($relative in $files) {
    if ($relative -match '(^|/)(artifacts|bin|obj)/') { continue }
    $destination = [IO.Path]::GetFullPath((Join-Path $packageRoot $relative))
    if (-not $destination.StartsWith($packageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid copy path.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $source $relative) -Destination $destination
}
Push-Location -LiteralPath $packageRoot
try {
    foreach ($bundled in @('false', 'true')) {
        $output = Join-Path $packageRoot ('artifacts/package-' + $bundled)
        & dotnet publish src/Sidetalk.App/Sidetalk.App.csproj -c Release -r win-x64 --self-contained $bundled -o $output --nologo
        if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
        & dotnet publish src/Sidetalk.IsolationProbe/Sidetalk.IsolationProbe.csproj -c Release -r win-x64 --self-contained $bundled -o (Join-Path $output 'tools/IsolationProbe') --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Probe publish failed.' }
        Copy-Item -LiteralPath README.md -Destination $output
        Copy-Item -LiteralPath docs -Destination $output -Recurse
        $zip = Join-Path $packageRoot ('artifacts/package-' + $bundled + '.zip')
        Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip
        Get-FileHash -LiteralPath $zip -Algorithm SHA256
    }
} finally { Pop-Location }
$packageRoot
```

두 모드의 앱/Probe `runtimeconfig.json`에서 `tfm=net10.0`을 확인한다. framework-dependent는 `frameworks`/`framework`의 10.0 최소 버전과 번들 `coreclr.dll` 없음, self-contained는 `includedFrameworks`의 실제 10.0 패치 및 `coreclr.dll`·WPF `PresentationNative_cor3.dll` 포함을 확인한다. ZIP 안 앱·Probe·README/docs를 확인하고 Sandboxie/KakaoTalk·사용자 프로필/계정/INI가 포함되지 않아야 한다.

## 별도 실환경 확인

일반 Windows 11 사용자 세션과 일회용 VM에서 [WINDOWS-RECOVERY-TEST.md](WINDOWS-RECOVERY-TEST.md), [ACCEPTANCE.md](ACCEPTANCE.md), [ISOLATION-PROBE.md](ISOLATION-PROBE.md)를 따른다. 기존 .NET 8 합성 프로필·명령/복구 기록과 sentinel을 보존한 상태에서 .NET 10 배포를 별도 폴더로 복사하고, 실제 설치/API 조회·일반 GUI 단일 실행·실패 후 재시작·Completed 재조정을 확인한다. framework-dependent 시험 VM에는 Desktop Runtime 10 x64가 필요하며 self-contained는 별도 설치가 필요 없다. 기존 VM의 설정/계정/box를 새 실패 fixture로 재사용하지 않는다.

계정 없는 검사와 로그인·송수신·알림·계정 데이터 영속성은 별개다. 기존 .NET 8 실계정 결과를 승계해 성공으로 보고하지 않는다. 새 승인 없이 운영 배포 교체·릴리스·유료 기능 변경을 진행하지 않는다.
