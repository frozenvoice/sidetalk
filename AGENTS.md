# SIDETALK

## GitHub Actions 수동 실행 정책

- GitHub hosted CI는 기본 개발 검증 수단이 아니다. 기존·신규 workflow의 시작 이벤트는 `workflow_dispatch`만 허용한다. `workflow_call`은 명시적 수동 root에서만 도달하는 재사용 workflow에 한정한다.
- `push`, `pull_request`, `pull_request_target`, `workflow_run`, `repository_dispatch`, `schedule` 등 자동 시작 이벤트와 우회용 경량 job·가짜 성공 job을 추가하지 않는다. Git hook·스크립트·에이전트 설정의 자동 dispatch도 금지한다.
- 실제 Actions dispatch·watch·rerun은 사용자의 해당 원격 실행에 대한 명시적 요청이 있어야 한다. 릴리스 준비·게시 요청이나 기존 릴리스 게이트만으로 실행을 승인받은 것으로 해석하지 않는다. 승인이나 정확한 SHA의 성공 근거가 없으면 미충족 게이트를 보고하며 공개하지 않는다. 아래 전체 검증·패키징·해시 계약은 유지한다.
- 코드 변경에는 관련 로컬 lint·타입 검사·단위 테스트·빌드·핵심 회귀 검증을 반드시 수행한다. 실패는 원인 분석·수정 후 관련 검사를 재실행한다. 실행하지 못한 검사는 명령과 구체적 이유를 보고하며 원격 CI로 대체하지 않는다.
- 문서·지침만 바꾸는 작업은 diff·링크·경로·명령 및 해당 정책을 로컬에서 확인하며 앱 빌드·설치 회귀는 요구하지 않는다.

## 프로젝트 범위

- Windows 11 x64용 C#/.NET 10 LTS WPF 앱이다. SDK 선택은 `global.json`을 따른다.
- `src/Sidetalk.App`은 한국어 UI, `src/Sidetalk.Core`는 격리 정책·설치 검증·프로필·명령 수명주기, `src/Sidetalk.IsolationProbe`는 계정 없는 격리 시험 도구다. 제품 범위는 `PRODUCT.md`, 설계는 `DESIGN.md`를 참고한다.

## 검증과 패키징

저장소 루트에서 PowerShell로 실행한다. 컴파일 경고는 오류로 처리된다(`Directory.Build.props`).

```powershell
# 일반 개발용 검증: 앱·콘솔·시험 도구. 실행 중인 배포 앱을 닫지 않아도 된다.
pwsh -File scripts/verify.ps1

# 복구 UI 변경 시 합성 WPF 검사도 포함한다. 출력은 기본적으로 새 임시 폴더다.
pwsh -File scripts/verify.ps1 -UiChecks

# 배포 패키징: 검증 + 두 publish + README/docs + ZIP/SHA-256 + 기존 배포본 교체
pwsh -File scripts/build.ps1 -SelfContained

# 사용자 배포물: 위 publish로 artifacts/release/SIDETALK-Setup.exe(+ .sha256/.unsigned.txt) 생성. 설치하지 않는다.
pwsh -File scripts/build-installer.ps1

# 설치형·포터블 동시 준비: 기본 검증과 self-contained publish 한 번 + Setup + 전체 ZIP 대조 + provenance. 설치·게시 없음.
pwsh -File scripts/prepare-distributions.ps1

# 개발자 로컬 설치: SDK·Inno 자동 준비 + build.ps1 -SelfContained + Setup 생성 + 대화형 설치/업데이트
build-local.cmd            # -NoInstall: Setup 생성까지, -NoLaunch, -NoPause
```

- 검증은 Release 앱 빌드·자체 콘솔 테스트·시험 도구 빌드를 수행한다. `dotnet test`로 대체하지 않으며, publish·ZIP·배포 교체·앱/시험 도구 실행은 하지 않는다. 실제 `bin/obj` 출력이 잠기면 해당 빌드 오류를 해결해야 하며 프로세스를 임의 종료하지 않는다.
- `-UiChecks`는 실제 설정·관리 UI·카카오톡에 접근하지 않는다. 일반 사용자 GUI 검증과 구분한다.
- 일반 작업은 변경 범위에 필요한 로컬 검증 후 완료할 수 있다. UI 변경에는 관련 합성 WPF 검사, 빌드 스크립트 변경에는 BuildChecks, 설치/업데이트 변경에는 InstallerChecks를 선택한다. 일반 코드 수정마다 UI·설치기 생성·설치 회귀를 자동 호출하지 않는다. 원격 CI 시작·watch·완료 대기·자동 rerun은 일반 작업의 완료 조건이 아니다.
- `verify.ps1`와 `Invoke-SidetalkVerification`의 기본 검증 및 `build.ps1`의 배포 검증은 생략하거나 축소하지 않는다. `build-local.cmd`는 설치/업데이트를 포함하는 사용자 진입점이며 개발용 검증 명령으로 대체하거나 설치 동작·데이터 보존 계약을 바꾸지 않는다.
- `.github/workflows/windows-verify.yml`은 `workflow_dispatch`로만 전체 원격 검증을 수행한다. 일반 push/PR·정기 실행·성공만 반환하는 대체 job은 두지 않는다. 전체 검증은 `verify.ps1 -UiChecks`, BuildChecks, InstallerChecks를 모두 유지하며 필수 검사의 실패·취소·미실행을 성공으로 보고하지 않는다.
- 설치기 전달/릴리즈 공개에는 개발용 검사보다 강한 기준을 적용한다. 현재 게시 자동화는 없으므로 `docs/VERIFICATION.md`의 수동 사전 검증 절차를 따른다. 공개할 정확한 커밋 SHA의 전체 원격 검증 성공과 필수 단계 실행 결과, 동일 SHA의 깨끗한 소스에서 만든 payload·설치기·공개 파일의 SHA-256 동일성을 확인한다. 과거 성공 실행·다른 SHA·공개 후 이벤트는 공개 전 검증을 대신하지 않는다.
- 패키징은 같은 검증을 한 번 포함하므로 그 직전에 검증 명령을 중복 실행하지 않는다. `-SelfContained`를 생략하면 .NET Desktop Runtime이 필요한 배포본을 만든다. 배포본 교체 전에는 해당 SIDETALK와 배포된 IsolationProbe를 정상 종료해야 한다.
- 스크립트 변경 시 `pwsh -File tests/Sidetalk.BuildChecks/Program.ps1`로 임시 fixture의 실패 전파·패키징 보호·롤백 회귀를 확인한다. 실제 publish나 앱 실행을 하지 않는 모의 검사다.
- 설치 프로그램(`installer/SIDETALK.iss`, `installer/SetupTheme.iss`, `assets/installer/`, `scripts/generate-setup-art.ps1`, `scripts/build-installer.ps1`, `scripts/install-local.ps1`, `scripts/prepare-inno.ps1`) 변경 시 `verify.ps1` 뒤에 `pwsh -File tests/Sidetalk.InstallerChecks/Program.ps1`로 고정 Inno Setup 컴파일과 격리 설치/업데이트/제거 회귀를 확인한다. 테스트 전용 `SIDETALKInstallerTest-*` AppId·설치/시작 메뉴/바탕 화면 위치만 쓰며 실제 설치·바로가기·`%LOCALAPPDATA%\SIDETALK`는 읽기만 한다.
- `SIDETALK.iss`는 설치 정책·엔진, `SetupTheme.iss`는 WebPier와 공유하는 표현 계층(600×340 다크 창)이다. 테마는 Inno의 버튼·태스크·진행률·PrepareToInstall 결과만 표시하며 프로세스·파일·정책을 다루지 않는다. 설치 UI 이미지는 `pwsh -File scripts/generate-setup-art.ps1`로만 재생성한다. 설치 화면 변경 시 대화형 데스크톱에서 `pwsh -File tests/Sidetalk.InstallerChecks/SetupUi.ps1`로 확인·진행·완료·실패·업데이트·키보드·`/SILENT`·`/NOSTYLE`(고대비 대체)·확대 배율을 같은 격리 fixture로 검사한다. 캡처와 보고서는 `artifacts/verification/setup-ui`에 덮어쓴다.
- 최종 사용자 실행 위치는 `%LOCALAPPDATA%\Programs\SIDETALK`(설치본, 고정 AppId `AF6EF9DE-E012-4945-BBD1-39ED6572282D`)이다. `artifacts/publish/win-x64`는 설치 원본이며 사용자 실행 위치로 문서화하지 않는다. 정식 포터블 ZIP은 `artifacts/release/SIDETALK-<version>-win-x64-portable.zip`로 별도 생성하며, EXE·DLL·런타임·필수 리소스·`tools/IsolationProbe` 전체를 압축 해제한 폴더에서 실행한다. 설치형과 동일한 `%LOCALAPPDATA%\SIDETALK` 프로필·명령/복구 기록과 같은 소유·격리 검사를 사용하므로 서로 동시에 실행하지 않는다. 이동한 실행 경로는 사용자가 선택한 소유 바로가기만 명시적 저장으로 갱신한다. 포터블은 설치된 앱 등록·자동 설치 업데이트를 제공하지 않는다. 설치 프로그램은 `{app}` 밖을 지우지 않고(`[UninstallDelete]` 금지), 프로세스를 종료하지 않으며(`CloseApplications=no`), 설치 위치의 SIDETALK 파일이 사용 중이면 복사 전에 멈춘다.
- `prepare-distributions.ps1` 성공 시 `artifacts/release/SIDETALK-distributions.json`의 소스 SHA·dirty 상태·버전·전체 payload/배포물 SHA-256을 확인한다. 실패 시 이전 배포 묶음이 남을 수 있으므로 파일 존재나 과거 manifest를 현재 성공으로 간주하지 않는다. 두 배포물은 같은 self-contained publish에서 만들고 릴리스 한 건에 함께 제공한다. 공개 전에 정확한 SHA의 깨끗한 소스·전체 원격 검증·공개 파일 해시를 별도로 확인한다.
- 제품 버전은 `Directory.Build.props`의 `SidetalkVersion` 하나다. 앱·Core·시험 도구·진단·Setup 버전이 모두 이를 따른다. 영속 schema 버전과 혼동하지 않는다.
- `artifacts/`, `bin/`, `obj/`는 생성 결과다. 배포본에는 공식 카카오톡이나 Sandboxie를 포함하지 않는다.

## 검증 산출물 정리

- 검증·실험용 빌드, 스테이징, 캡처 출력을 작업이나 재시도마다 새 이름의 폴더로 만들지 않는다(예: `artifacts/<작업명>`). 기본 출력 위치를 재사용하고, 격리가 꼭 필요하면 고정된 폴더 하나를 덮어쓴다.
- 이번 작업에서 만든 임시 빌드·스테이징·캡처 폴더는 완료 보고 전에 삭제한다. 다른 작업이나 실행 중인 프로세스의 출력은 건드리지 않는다.
- 근거로 남길 것은 로그·요약·최소 파일로 줄인다. 1GB 이상이 남으면 경로와 크기를 완료 보고에 적는다.
- `artifacts/vm-lab`의 VM 디스크와 설치 이미지는 다시 만들기 어려운 시험 환경이다. 명시 요청 없이 지우지 않고, 같은 환경을 새로 만들지 말고 재사용한다.

## 유지할 제약

- 공식 카카오톡을 Sandboxie의 읽기 전용 API와 `Start.exe`의 명시적 전용 box 명령으로 제어한다. 카카오톡 패치·자체 주입/후킹·인증 우회·이미지 이름으로 프로세스 종료를 추가하지 않는다.
- 실행과 종료 모두 소유 표식·저장 경로·설치 서명/버전·설정과 상속 규칙 검증을 통과해야 한다. 불확실하거나 설정이 변경된 경우 차단을 유지한다. 전역 변경은 SetupRecovery의 고정된 공식 기준·영향 범위·최초 승인 또는 동일 범위 자동 복구 동의가 모두 확인된 여섯 Template 선택 제거에만 허용한다. 검사기를 통과시키려고 범위를 늘리거나 다른 box를 변경하지 않는다.
- `%LOCALAPPDATA%/SIDETALK`의 프로필·전용 저장소·명령 기록을 보존한다. 손상된 프로필, 중단된 임시 파일, 미완료 기록을 자동 삭제·덮어쓰기·초기화하지 않는다. 명령 의도는 프로세스 생성 전에 기록한다. 시간 초과를 완료로 취급하지 않으며, PID·생성 시각·실행 경로로 신원을 확인하고 명령 종료와 전용 box 재조회를 모두 확인한 뒤 새 명령을 허용한다.
- `src/Sidetalk.Core/SandboxieDefaults.json`과 `tests/Sidetalk.Tests/Fixtures/sandboxie-1.18.4.ini`는 `scripts/update-sandboxie-baseline.ps1`로 함께 재생성한다. `docs/third-party-sandboxie.md`의 원문 SHA-256·출처와 값 순서를 보존하고, 기준 변경 시 `src/Sidetalk.Core/BuiltinTemplateHashes.json`도 함께 대조한다. 검사 통과만을 위해 기준을 완화하지 않는다.
- 실환경 검증은 `docs/ACCEPTANCE.md`와 `docs/ISOLATION-PROBE.md`를 따른다. 콘솔 테스트 성공이나 box 프로세스 존재만으로 로그인·송수신·두 계정 호환성 성공을 선언하지 않는다. 검증 범위와 남은 항목은 `docs/VERIFICATION.md`에 근거한다.

## 승인된 초기 준비와 설정 복구

- 정책 검사는 읽기 전용이다. 준비 계획·사용자 동의·백업·공식 SbieIni 실행·파일/API 재검사를 별도 복구 경로에 둔다. SbieIni의 경로·서명·버전도 확인한다.
- 재발 방지는 실제 현재 사용자 섹션의 SbieCtrl_AutoRunSoftCompat=n으로 제한한다. UserSettings_*는 box가 아니다. SandMan의 별도 UI 설정을 같은 옵션으로 취급하지 않는다.
- 열린 관리 UI의 오래된 선택 상태는 승인 범위 내에서 검증한 실행 경로·PID·생성 시각·사용자·세션으로만 처리한다. 호환성 창에 CloseMainWindow/WM_CLOSE를 보내지 않는다. 서비스·드라이버·Start.exe·카카오톡은 복구 목적으로 종료하지 않는다.
- 복구와 실행·종료는 같은 CommandJournal 잠금을 사용한다. 미완료 복구는 정상 준비 상태로 숨기지 않는다. 전체 INI 복원 롤백·프로필 재생성·데이터 초기화는 금지한다.
- --diagnose와 모든 --render 모드는 동의가 있어도 읽기 전용이다. 일반 사용자 실행과 Codex/MSIX 파일 가상화 실행의 결과를 구분한다.
