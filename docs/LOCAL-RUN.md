# 루트에서 로컬 실행

설치 프로그램을 만들거나 하위 폴더를 열 필요 없이 저장소 루트의 CMD 파일을 일반 사용자 권한으로 더블클릭합니다.

| 파일 | 동작 |
|---|---|
| `run-local.cmd` | 이미 빌드된 SIDETALK를 바로 엽니다. 배포 파일이 없으면 최초 한 번 빌드한 뒤 엽니다. |
| `build-local.cmd` | 현재 체크아웃을 빌드·검증하고 self-contained 배포본으로 교체한 뒤 SIDETALK를 엽니다. 소스를 업데이트한 뒤 사용합니다. |

기존 실행 경로는 `artifacts/publish/win-x64/Sidetalk.exe` 그대로입니다. 실행 파일만 루트로 복사하지 않습니다. `run-local.cmd`를 사용할 때마다 다시 빌드하지 않으며, 소스의 최신 여부를 자동으로 판단하지 않습니다.

## 빌드 전제와 실패 처리

빌드에 필요한 것은 PATH에서 찾을 수 있는 **PowerShell 7 (`pwsh.exe`) 하나뿐**입니다. **.NET SDK는 사용자가 따로 설치할 필요가 없습니다.** `global.json`이 요구하는 SDK는 첫 `build-local.cmd` 실행 때 자동으로 준비되고, 같은 실행에서 그대로 빌드가 이어집니다. winget·MSI 설치, `global.json` 수정 같은 수동 작업은 필요하지 않습니다.

`scripts/ensure-dotnet.ps1`이 다음 순서로 SDK를 고릅니다. 각 단계는 `[dotnet/bootstrap] <단계>`로 출력되며, 실패하면 `[dotnet/bootstrap] Failed during: <단계>`와 원인 한 줄을 보여 줍니다.

1. **Check installed SDK**: PATH의 `dotnet.exe`를 저장소 루트에서 `--version`으로 실행해 `global.json`(10.0.401, `latestFeature`)을 만족하면 그대로 사용합니다.
2. **Check private SDK cache**: 만족하지 않으면 `%LOCALAPPDATA%\SIDETALK-BuildTools\dotnet\<버전>`의 전용 캐시를 같은 방식으로 확인합니다. `dotnet.exe` 존재만으로는 통과하지 않습니다.
3. **Clean previous attempt**: 캐시도 없으면 설치 잠금을 잡은 뒤 중단된 이전 시도의 `.staging-*`와 같은 버전의 `<버전>.invalid-*`만 제거합니다. 남은 staging은 신뢰하거나 재사용하지 않습니다. 다른 버전 캐시와 시스템 SDK는 건드리지 않습니다.
4. **Download**: 공식 `dotnet/install-scripts` 저장소의 고정 commit에 있는 `dotnet-install.ps1`을 HTTPS로 받고, 고정 SHA-256과 일치할 때만 실행합니다.
5. **Install / Verify staged SDK / Verify signature**: 새 staging 폴더에 x64 SDK를 설치하고, 저장소 루트에서의 `--version` 결과와 `dotnet.exe`의 유효한 Microsoft Corporation Authenticode 서명을 확인합니다.
6. **Promote cache / Ready**: 검증된 staging만 캐시로 옮기고, 이 SDK의 경로를 현재 CMD의 `DOTNET_ROOT`/`PATH` 앞에 넣은 뒤 `scripts/build.ps1 -SelfContained`를 실행합니다.

관리자 권한이나 시스템 전역 SDK 변경은 없으며 기존 .NET 8/10 설치도 삭제·교체하지 않습니다(`dotnet --list-sdks` 목록은 그대로입니다). **첫 실행만** 약 300MB SDK 다운로드 때문에 수 분 이상 걸릴 수 있고 인터넷 연결이 필요합니다. 이후 같은 SDK 버전은 캐시를 재사용하므로 다시 내려받지 않습니다. 준비 중 실패하거나 창을 닫아도 다음 실행이 남은 staging을 정리하고 다시 시도합니다. SDK 준비가 실패하면 `build.ps1`, 기존 배포본 실행, 카카오톡·Sandboxie 종료 중 어느 것도 하지 않습니다.

이미 만들어진 self-contained 배포본을 실행할 때는 PowerShell이나 SDK가 필요하지 않습니다. 빌드 진입점은 Git pull·브랜치 전환을 자동 수행하지 않습니다.

`build-local.cmd`는 기존 `scripts/build.ps1 -SelfContained`를 재사용합니다. 콘솔 회귀 테스트와 시험 도구 빌드·publish·ZIP 단계를 생략하지 않고 기존 배포 교체·롤백 로직도 바꾸지 않습니다. 빌드가 실패하거나 취소되면 오류를 반환하며 이전 실행 파일을 대신 실행하지 않습니다. 필요한 .NET SDK 자동 다운로드 외에는 관리자 승격·프로세스 강제 종료를 추가하지 않습니다. SDK 준비, `dotnet --version`, `build.ps1`의 결과는 모두 실제 종료 코드를 저장해 `0`일 때만 성공으로 보므로 음수 native 종료 코드도 실패로 처리합니다.

빌드 전에 **SIDETALK 창과 배포된 IsolationProbe만 정상 종료**하세요. 카카오톡을 대신 종료하지 않습니다. 프로필·전용 박스·명령 저널·Sandboxie 설정은 이 진입점에서 삭제하거나 변경하지 않습니다. 기존 빌드 스크립트는 빌드 과정에서 필요한 패키지를 복원할 수 있습니다.

PowerShell의 `RemoteSigned` 실행 정책은 빌드 프로세스에만 적용하고 영구 설정을 바꾸지 않습니다. 조직 정책이나 다운로드 표시 때문에 실행이 차단되면 오류를 확인하세요. 스크립트는 정책을 영구 변경하거나 다운로드 표시를 자동 제거하지 않습니다.

오류가 나면 대화형 콘솔에서만 키 입력을 기다립니다. 성공 시 콘솔은 종료됩니다. 자동화에서는 `-NoPause`를 사용합니다.

```bat
build-local.cmd -NoLaunch -NoPause
run-local.cmd -NoPause
```

두 명령 모두 **SIDETALK 런처만** 엽니다. 보조 카카오톡 실행·종료는 기존 GUI의 사용자 확인과 격리 검사를 그대로 거칩니다. 저장소 경로를 기준으로 실행하므로 다른 작업 디렉터리에서 호출해도 하위 폴더를 직접 찾을 필요가 없습니다.

## 루트 진입점 최초 추가 당시의 검증 범위

두 CMD의 분기·경로 인용·실패 코드 전달을 정적으로 점검했습니다. 이 변경을 작성한 Linux 환경에는 Windows `cmd.exe`와 .NET SDK가 없어 실제 더블클릭, Windows 빌드, 앱 실행, 기존 콘솔 테스트는 재실행하지 않았습니다. 기존 앱 검증 결과는 `VERIFICATION.md`와 구분합니다.

Windows에서 확인할 항목: 기존 배포본 즉시 실행, 배포본 없는 첫 실행의 빌드 연결, `-NoLaunch`, 빌드 실패/취소 시 실행 차단, 도구 미설치 안내, 공백·한글이 있는 저장소 경로와 다른 작업 디렉터리에서의 실행입니다.

## 2026-09-22 Windows 배포 검증

`build-local.cmd -NoLaunch -NoPause`가 기존 self-contained 배포 스크립트의 빌드·자체 콘솔 검사·두 프로젝트 publish·ZIP 생성까지 실행됨을 확인했다. 실패 시 기존 배포본 보존도 확인했고, 통과한 0.1.5를 같은 배포 경로로 반영했다. `run-local.cmd`는 기존 배포본이 있으면 재빌드하지 않는 동작을 유지한다.

실제 탐색기 더블클릭과 일반 사용자 GUI의 승인 복구 시험은 아직 수행하지 못했다. Codex/MSIX 자식 실행에서 발견한 프로필 가상화를 일반 사용자 성공으로 대체하지 않는다. 이번 작업의 정확한 검사 범위는 [검증 결과](VERIFICATION.md)에 기록한다.

## 2026-10-06 SDK 자동 준비 실패 수정과 Windows 검증

실사용 오류: SDK 8.0.424만 있는 PC에서 `build-local.cmd`가 SDK 10.0.401을 내려받아 압축까지 푼 뒤 `ensure-dotnet.ps1: null 값 식에서 메서드를 호출할 수 없습니다.`로 중단되었다.

원인(실제 `pwsh` 7.6.6에서 재현):

1. `finally`의 `$lock?.Dispose()`는 PowerShell에서 `?`를 변수 이름에 포함해 `${lock?}.Dispose()`로 해석된다(AST `NullConditional=False`, 변수 `lock?`). `$lock`이 null이든 실제 FileStream이든 항상 이 오류를 냈고, 원래 오류를 가렸으며 뒤의 staging 정리도 건너뛰었다.
2. 그 앞에서 실제 실패가 먼저 났다. 시스템 SDK 8의 `dotnet --version`이 저장소 루트에서 `-2147450725`(0x8000809B)로 끝난 값이 `$LASTEXITCODE`에 남았고, 정상 완료 시 `exit`하지 않는 `dotnet-install.ps1` 뒤에 이 값을 설치 실패 코드로 읽었다. 남은 `.staging-*`에 `dotnet-install.ps1`이 그대로 있던 것이 이 경로와 일치한다.

같은 경로의 잠재 문제: 결과 파일을 UTF-8로 썼으나 `set /p`는 콘솔 코드 페이지(949)로 읽어 한글 사용자 폴더 경로가 깨졌다. 이제 콘솔 코드 페이지로 쓰고 표현할 수 없는 경로는 명확히 거부한다.

검증:

- `pwsh -File tests/Sidetalk.BuildChecks/Program.ps1`: 16개 검사 그룹 / 35개 임시 저장소 성공. 실제 native `dotnet.exe` 대역(.NET Framework `csc.exe`로 생성)과 다운로드·Authenticode 경계만 대체한 실제 `ensure-dotnet.ps1` 실행으로 시스템 SDK 선택, 오래된 시스템 SDK + 전용 캐시(다운로드 0회), 설치 후 정리·승격·재실행 설치 0회, 다운로드·SHA-256·설치 예외·설치 종료 코드·SDK 버전·서명 상태·인증서 없음·발급자 실패 각각의 단계 표시와 다음 실행 복구, 잠금 미획득(null)/잠금 중 성공 `exit` 경로, 음수 종료 코드에서 `build.ps1` 0회, 한글 경로의 `DOTNET_ROOT` 전달을 확인했다.
- 실제 PC(시스템 SDK 8.0.424만 존재, 이전 실패의 `.staging-*` 잔존)에서 `build-local.cmd -NoLaunch -NoPause` 1회차: 남은 staging 정리 → 공식 설치 스크립트 SHA-256 확인 → SDK zip 300,608,304바이트 다운로드·설치 → 버전·Microsoft 서명 확인 → 캐시 승격 → 같은 실행에서 `build.ps1 -SelfContained`의 검증·publish·ZIP·교체까지 성공, 전체 2분 19초. 2회차: 캐시 재사용(다운로드·설치 0회), 1분 5초 성공. 두 실행 후에도 `dotnet --list-sdks`는 `8.0.424`만 표시했다.
