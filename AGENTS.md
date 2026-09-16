# SIDETALK

## 프로젝트 범위

- Windows 11 x64용 C#/.NET 8 WPF 앱이다. SDK 선택은 `global.json`을 따른다.
- `src/Sidetalk.App`은 한국어 UI, `src/Sidetalk.Core`는 격리 정책·설치 검증·프로필·명령 수명주기, `src/Sidetalk.IsolationProbe`는 계정 없는 격리 시험 도구다. 제품 범위는 `PRODUCT.md`, 설계는 `DESIGN.md`를 참고한다.

## 검증과 패키징

저장소 루트에서 PowerShell로 실행한다. 컴파일 경고는 오류로 처리된다(`Directory.Build.props`).

```powershell
dotnet build src/Sidetalk.App/Sidetalk.App.csproj -c Release
dotnet run --project tests/Sidetalk.Tests/Sidetalk.Tests.csproj -c Release
```

- 테스트는 자체 콘솔 러너이므로 `dotnet test`로 대체하지 않는다.
- 시험 도구 변경 시 `dotnet build src/Sidetalk.IsolationProbe/Sidetalk.IsolationProbe.csproj -c Release`도 확인한다.
- 배포 검증은 `pwsh -File scripts/build.ps1 -SelfContained`로 수행한다. 이 스크립트가 앱 빌드·콘솔 테스트·시험 도구 빌드·두 프로젝트 publish·ZIP/SHA-256 생성을 포함하므로 위 검증을 별도로 중복 실행하지 않는다. `-SelfContained`를 생략하면 .NET Desktop Runtime이 필요한 배포본을 만든다.
- `artifacts/`, `bin/`, `obj/`는 생성 결과다. 배포본에는 공식 카카오톡이나 Sandboxie를 포함하지 않는다.

## 유지할 제약

- 공식 카카오톡을 Sandboxie의 읽기 전용 API와 `Start.exe`의 명시적 전용 box 명령으로 제어한다. 카카오톡 패치·자체 주입/후킹·인증 우회·이미지 이름으로 프로세스 종료를 추가하지 않는다.
- 실행과 종료 모두 소유 표식·저장 경로·설치 서명/버전·설정과 상속 규칙 검증을 통과해야 한다. 불확실하거나 설정이 변경된 경우 차단을 유지하며, 통과시키려고 전역 설정이나 다른 box를 변경하지 않는다.
- `%LOCALAPPDATA%/SIDETALK`의 프로필·전용 저장소·명령 기록을 보존한다. 손상된 프로필, 중단된 임시 파일, 미완료 기록을 자동 삭제·덮어쓰기·초기화하지 않는다. 명령 의도는 프로세스 생성 전에 기록한다. 시간 초과를 완료로 취급하지 않으며, PID·생성 시각·실행 경로로 신원을 확인하고 명령 종료와 전용 box 재조회를 모두 확인한 뒤 새 명령을 허용한다.
- `src/Sidetalk.Core/SandboxieDefaults.json`과 `tests/Sidetalk.Tests/Fixtures/sandboxie-1.18.4.ini`는 `scripts/update-sandboxie-baseline.ps1`로 함께 재생성한다. `docs/third-party-sandboxie.md`의 원문 SHA-256·출처와 값 순서를 보존하고, 기준 변경 시 `src/Sidetalk.Core/BuiltinTemplateHashes.json`도 함께 대조한다. 검사 통과만을 위해 기준을 완화하지 않는다.
- 실환경 검증은 `docs/ACCEPTANCE.md`와 `docs/ISOLATION-PROBE.md`를 따른다. 콘솔 테스트 성공이나 box 프로세스 존재만으로 로그인·송수신·두 계정 호환성 성공을 선언하지 않는다. 검증 범위와 남은 항목은 `docs/VERIFICATION.md`에 근거한다.
