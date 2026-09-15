# SIDETALK · 사이드톡

Windows 11에서 공식 카카오톡의 보조 인스턴스를 **SIDETALK 전용 Sandboxie Plus 환경**으로 실행하는 한국어 데스크톱 앱입니다.

> **0.1.1 사전 검증 빌드. 실제 계정 호환성은 미검증입니다.** 현재 PC에는 Sandboxie Plus가 없습니다. 실행 가능한 앱, 구성 검사 및 테스트를 제공하지만 두 계정 사용 성공을 보증하거나 완료로 표시하지 않습니다. [검증 결과](docs/VERIFICATION.md)와 [실환경 검증 절차](docs/ACCEPTANCE.md)를 먼저 읽으세요.

## 실행

배포 폴더의 `Sidetalk.exe`를 **일반 사용자 권한**으로 실행합니다. 폴더 안의 파일을 함께 보관하세요. SIDETALK를 닫아도 카카오톡은 종료되지 않습니다.

- Windows 11 x64
- 공식 서명이 유효한 카카오톡 (현재 PC: 26.7.1.5263)
- Sandboxie Plus 1.18.4 x64 (소스/API 검토 기준, 실제 연동 미검증)
- framework-dependent 배포는 .NET 8 Desktop Runtime x64 필요. self-contained 배포는 런타임 포함.

ARM64, 다른 Sandboxie 버전, 포터블/사용자 지정 카카오톡 경로는 이번 빌드의 검증 범위 밖입니다. 기존 설치를 이동하거나 다른 버전을 자동 설치하지 않습니다.

## 초기 설정

1. 앱의 **초기 설정 · 진단**에서 감지된 설치 경로·버전과 서명 결과를 확인합니다.
2. Sandboxie가 없다면 [공식 다운로드](https://sandboxie-plus.com/downloads/)와 [1.18.4 릴리스](https://github.com/sandboxie-plus/Sandboxie/releases/tag/v1.18.4)를 확인합니다. **설치는 시스템 서비스·드라이버 변경이며 사용자의 명시적 확인 후 진행합니다.** SIDETALK는 설치 명령을 실행하지 않습니다.
3. **설정 파일 내보내기**를 누릅니다. `%LOCALAPPDATA%\SIDETALK\profile.json`에 임의의 전용 환경 ID와 소유 표식을 한 번 저장하고, 선택한 폴더에 새 INI 파일을 생성합니다. 같은 환경을 계속 사용하며 기존 INI나 데이터를 덮어쓰지 않습니다.
4. 내보낸 내용을 검토한 뒤에만 Sandboxie Plus에서 전용 섹션을 추가합니다. 자세한 순서는 [설정 안내](docs/SETUP.md)를 따릅니다. 기존 `Sandboxie.ini` 전체를 교체하거나 다른 섹션을 수정하지 않습니다.
5. SIDETALK에서 새로고침합니다. 소유 표식, 설정, 전역/템플릿 규칙, 실제 저장 경로가 맞지 않으면 실행과 종료를 차단합니다.
6. 실제 계정을 사용하기 전에 [무해한 격리 사전 검사](docs/ACCEPTANCE.md)를 진행합니다. 이후 공식 카카오톡 창에서 사용자가 직접 보조 계정 로그인과 기기 인증을 합니다.

**처음부터 메인 프로필/대화가 보이면 로그인하지 말고 검증을 중단하세요.** 계정 데이터를 복사하거나 보안 예외를 추가해 계속 진행하지 마세요.

## 실행 상태와 종료

- 상태는 Sandboxie API로 확인한 **전용 환경의 프로세스**입니다. 로그인 여부, 실제 송수신, 알림은 자동 판정하지 않습니다.
- 실행 요청 후 소속 프로세스가 확인되어야 실행 상태를 표시합니다. 명령 성공 코드만으로 판단하지 않습니다.
- 명령 시간이 초과되어도 Start.exe가 실행 중이거나 종료 여부를 알 수 없으면 중복 명령을 막습니다. 명령 종료를 확인한 뒤 새로 조회한 전용 환경이 비어 있으면 재실행, 프로세스가 남으면 보조 종료 재시도를 허용합니다. 조회 실패는 계속 상태 미확인으로 표시합니다.
- 보조 종료는 **해당 전용 환경 전체의 `/terminate`**입니다. 카카오톡과 그 환경의 보조 프로세스가 강제로 끝나므로 미저장 작업이 손실될 수 있습니다. 평소에는 카카오톡 자체 종료를 먼저 권장합니다.
- 저장된 파일/레지스트리는 유지합니다. 자동 삭제·초기화·복구/호스트 반출 기능은 제공하지 않습니다.
- 설정 드리프트가 발견되면 종료도 차단합니다. 특히 자동 삭제가 켜진 환경을 종료해 데이터가 사라지는 상황을 방지합니다. 전역 설정을 바꿔 검사기를 통과시키지 마세요.

## 저장 위치

| 항목 | 위치 |
|---|---|
| SIDETALK 소유 정보 | `%LOCALAPPDATA%\SIDETALK\profile.json` |
| 보조 환경 파일 | `%LOCALAPPDATA%\SIDETALK\Boxes\SideTalk_<고유 ID>` |
| 보조 환경 레지스트리 | Sandboxie가 전용 환경용으로 만든 별도 하이브 |
| 설정 내보내기/진단 | 사용자가 선택한 새 파일 |

프로필 파일을 삭제하면 기존 환경을 자동으로 다시 연결하지 않습니다. 기존 환경이나 데이터가 있어도 자동 삭제하지 않습니다. 사용자 데이터·레지스트리·대화 내용은 진단에 수집하지 않습니다. 진단에는 설치 경로, 사용자 폴더 경로, 버전, 전용 프로세스 PID가 포함되므로 공유 전에 확인하세요.

## 빌드와 검증

PowerShell, .NET SDK 8.0.400 이상(8.x):

```powershell
dotnet build src/Sidetalk.App/Sidetalk.App.csproj -c Release
dotnet run --project tests/Sidetalk.Tests/Sidetalk.Tests.csproj -c Release
pwsh -File scripts/build.ps1 -SelfContained
```

스크립트가 출력한 배포 폴더에서 자체 GUI 렌더링과 읽기 전용 진단(카카오톡 실행 없음):

```powershell
.\Sidetalk.exe --render C:\temp\sidetalk.png
.\Sidetalk.exe --diagnose C:\temp\sidetalk-diagnostics.json
```

출력 파일의 상위 폴더를 먼저 준비하고 진단 파일에는 새 이름을 사용하세요. 일반 실행은 로그인·시스템 설정을 변경하지 않습니다. 설정 내보내기와 실행/종료는 GUI의 명시적 조작이 필요합니다.

## 구현 범위

C# / .NET 8 / WPF, 외부 NuGet 의존성 없는 앱·콘솔 테스트. 공식 설치 파일의 Authenticode 서명을 확인하고, Sandboxie의 읽기 전용 API와 `Start.exe`의 명시적 box 명령만 사용합니다. 카카오톡 패치, 자체 주입·후킹, 뮤텍스/핸들 해제, 인증 우회, 이미지 이름으로 종료하는 코드가 없습니다.

1.18.4 공식 기본 템플릿은 출처·내용·값 순서를 고정 기준과 대조합니다. 명시적으로 추가한 전역 설정과 자동 확장된 기본 규칙을 구분하며, 사용자 재정의·추가 템플릿·알 수 없는 예외는 계속 차단합니다. 기준 데이터의 출처와 재생성 방법은 [Sandboxie 자료 안내](docs/third-party-sandboxie.md)에 있습니다. 공식 소스로 만든 설정 조회 시험을 통과했지만 실제 설치의 API 반환값과 격리 동작은 아직 미검증입니다.

공식 앱 및 Sandboxie는 배포본에 포함하지 않습니다. SIDETALK는 Kakao 또는 Sandboxie의 공식 제품이 아닙니다.
