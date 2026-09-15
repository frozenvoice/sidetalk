# 공식 근거 · 2026-09-15 확인

- [KakaoTalk 공식 서비스/다운로드](https://www.kakaocorp.com/page/service/service/KakaoTalk): 공식 앱 출처. Sandboxie 동시 계정 호환성 보증 근거로 사용하지 않음.
- [Sandboxie Plus 다운로드](https://sandboxie-plus.com/downloads/): 확인 시 웹 페이지 stable 링크는 1.18.2. [GitHub 최신 릴리스 1.18.4 / 5.73.4](https://github.com/sandboxie-plus/Sandboxie/releases/tag/v1.18.4)는 2026-09-06 게시. 둘의 갱신 시차를 명시하며 구현 검토 기준은 v1.18.4.
- [Start Command Line](https://sandboxie-plus.github.io/sandboxie-docs/Content/StartCommandLine.html): 명시적 `/box:<name>` 실행과 해당 box의 `/terminate`. 로그인 성공을 나타내는 인터페이스가 아님.
- [SBIE DLL API](https://sandboxie-plus.github.io/sandboxie-docs/Content/SBIEDLLAPI.html): QueryConf, QueryProcess, QueryProcessPath, QueryBoxPath 등 읽기 전용 API.
- [WriteFilePath](https://sandboxie-plus.github.io/sandboxie-docs/Content/WriteFilePath.html), [WriteKeyPath](https://sandboxie-plus.github.io/sandboxie-docs/Content/WriteKeyPath.html): 호스트 항목을 숨기고 해당 가상 경로에 새로운 보조 데이터를 생성하는 규칙. 실제 앱 호환성은 별도 검증 필요.
- [Privacy Mode](https://sandboxie-plus.github.io/sandboxie-docs/PlusContent/privacy-mode/): 광범위한 사용자 데이터 보호 모드와 supporter certificate 요구. 이번 빌드는 해당 유료 기능을 활성화/우회하지 않고, 명시적 경로별 공식 규칙을 검사함.
- [Sandbox Hierarchy](https://sandboxie-plus.github.io/sandboxie-docs/Content/SandboxHierarchy.html): 기본 Sandboxie 동작에서 호스트 데이터가 읽힐 수 있다는 근거.
- [v1.18.4 drv/file.c](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.4/Sandboxie/core/drv/file.c): File_InitPaths의 WriteFilePath 목록 구성과 별도 privacy-mode 처리.
- [v1.18.4 dll/file.c](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.4/Sandboxie/core/dll/file.c): PATH_IS_WRITE 경로의 호스트 파일 조회 대신 격리 경로 처리.
- [v1.18.4 drv/conf.c](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.4/Sandboxie/core/drv/conf.c): Conf_GetEx 설정명 열거, 글로벌과 템플릿 플래그 의미.
- [v1.18.4 drv/process_api.c](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.4/Sandboxie/core/drv/process_api.c): EnumProcessEx count 포인터가 있으면 PID는 index 0부터; legacy null 포인터 형식과 구별.

문서와 소스 검토는 현재 PC의 런타임 격리, 계정 정책, 로그인·송수신 성공을 증명하지 않습니다. 실제 설치/API 호출 결과와 실환경 기록표가 추가로 필요합니다.

- [v1.18.4 Templates.ini](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.4/Sandboxie/install/Templates.ini): 자동 병합되는 DefaultTemplates 및 검증 기준 원본. 추출 범위·SHA-256·오프라인 재생성 방법은 [자료 안내](third-party-sandboxie.md) 참조.

- [v1.18.4 Start.exe](https://github.com/sandboxie-plus/Sandboxie/blob/487d56309fafe90e2771d4c9004333fe01b77094/Sandboxie/apps/start/start.cpp): /terminate 내부 결과가 종료 코드에 반영되지 않는 경로, /wait 없는 실행 수명. [ProcessServer.cpp](https://github.com/sandboxie-plus/Sandboxie/blob/487d56309fafe90e2771d4c9004333fe01b77094/Sandboxie/core/svc/ProcessServer.cpp): 생성 후 박스 소속 지정과 재개, KillAll 재시도 동작. 따라서 SIDETALK가 명령 종료 이후 별도로 박스 프로세스를 확인해야 합니다.

- [v1.18.4 com.c](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.4/Sandboxie/core/dll/com.c#L601-L610), [ipc.c](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.4/Sandboxie/core/dll/ipc.c#L1491-L1502): 호스트 WebCache의 ClosedClsid·ClosedIpcPath 차단 규칙 검토 근거. SIDETALK 전용 섹션에만 추가하며 실제 IPC 격리는 별도 확인 필요.
## 현재 PC에서 확인한 공식 설치본

- 공식 [v1.18.4 릴리스](https://github.com/sandboxie-plus/Sandboxie/releases/tag/v1.18.4)의 x64 설치 파일은 공식 출처와 서명을 확인하고 다운로드한 파일의 SHA-256을 기록한 뒤 설치했다. 설치 파일 SHA-256은 `CFCEDA1B1A63ABCD2B3E5FBFDC4895F15DB56316F5A9F2F06AEA230E209B120E`이다.
- 설치된 사용자 모드 구성요소는 `Start.exe`, `SbieDll.dll`, `SbieSvc.exe` 모두 파일 버전 `5.73.4`이며, 공식 서명을 확인했다.
- 설치된 `SbieDrv.sys`는 파일 버전 `5.73.2`이며 Microsoft Windows Hardware Compatibility Publisher 서명을 확인했다. 드라이버 SHA-256은 `B2BFAC3C9FD4C378400EE4821A34773CCBAF71BB1E836C7A03ACC9A9AB3CFE96`이다. 이 버전은 설치 후 실제 파일과 Sandboxie API에서 확인한 값이며, 소스의 사용자 모드 버전 `5.73.4`로 추정해 대체하지 않는다.
- [v1.18.4 공통 버전 헤더](https://github.com/sandboxie-plus/Sandboxie/blob/487d56309fafe90e2771d4c9004333fe01b77094/Sandboxie/common/my_version.h)는 소스 빌드 기준 `VERSION_REV=4`를 정의하고, [드라이버 resource.rc](https://github.com/sandboxie-plus/Sandboxie/blob/487d56309fafe90e2771d4c9004333fe01b77094/Sandboxie/core/drv/resource.rc)는 그 값을 드라이버 파일 버전에 사용한다.
- [v1.18.4 빌드 workflow](https://github.com/sandboxie-plus/Sandboxie/blob/487d56309fafe90e2771d4c9004333fe01b77094/.github/workflows/main.yml)는 x64 드라이버를 `SandboxieDrv.sln`으로 별도 빌드하고, [copy_build.cmd](https://github.com/sandboxie-plus/Sandboxie/blob/487d56309fafe90e2771d4c9004333fe01b77094/Installer/copy_build.cmd)는 `Sandboxie\Bin\x64\SbieRelease\SbieDrv.sys`를 Plus payload로 복사한다. 릴리스 자산의 실제 드라이버가 `5.73.2`인 사실과 소스의 `5.73.4` 버전 정의 사이의 불일치는 현재 확인된 패키징 상태로 기록한다.
- [v1.18.2와 v1.18.4의 드라이버 소스 비교](https://github.com/sandboxie-plus/Sandboxie/compare/v1.18.2...v1.18.4)는 `Sandboxie/core/drv` 코드와 `conf.c`의 변경을 보여주지 않는다. 확인된 범위에서는 두 드라이버 버전 사이의 동작 변경을 뒷받침하는 소스 차이가 없다. 따라서 SIDETALK는 설치 파일의 실제 버전과 SHA-256을 함께 검사한다.

## SbieSvc 실행 경로 확인 제한

- [Microsoft QueryServiceStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryservicestatusex)와 [SERVICE_STATUS_PROCESS](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_status_process)는 실행 중 서비스의 상태와 서비스 PID(`dwProcessId`)를 제공한다. 현재 PC에서 `SbieSvc`는 SCM 등록 경로가 설치 폴더의 `SbieSvc.exe`와 일치하고 상태가 `Running`이며 PID도 반환된다.
- [Microsoft process access rights](https://learn.microsoft.com/en-us/windows/win32/procthread/process-security-and-access-rights)는 `QueryFullProcessImageName`에 `PROCESS_QUERY_LIMITED_INFORMATION`이 필요하고 프로세스 보안 설명자의 DACL에 따라 접근이 거부될 수 있음을 명시한다. 현재 표준 사용자 SIDETALK 프로세스에서는 SbieSvc PID에 대한 이 핸들 획득이 `ERROR_ACCESS_DENIED`로 거부됐다.
- Sandboxie의 [공개 DLL API](https://sandboxie-plus.github.io/sandboxie-docs/Content/SBIEDLLAPI.html)는 `QueryProcess`와 `QueryProcessPath`를 샌드박스에 추적된 프로세스에 제공하며 SbieSvc 자체의 실행 이미지 경로를 반환하는 API는 제공하지 않는다. 비공개 서비스 wire protocol, `SeDebugPrivilege`, 관리자 재실행, 프로세스 DACL 변경은 사용하지 않는다.
- 따라서 실행 경로 검사는 SCM의 서비스 등록 경로, `Running` 상태와 PID, 설치 파일의 서명·해시를 확인하는 범위로 유지한다. 실제 프로세스 이미지 경로를 읽을 수 없는 경우는 설치 실패로 단정하지 않고 진단에 제한 사항으로 남긴다.

- [Microsoft QueryServiceConfigW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryserviceconfigw), [QUERY_SERVICE_CONFIGW](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-query_service_configw): SIDETALK는 서비스 유형이 단독 프로세스인지, 계정이 LocalSystem인지, 명령이 인자 없는 정확한 설치 경로인지 검사한다. SCM 조회 전후와 이미지 경로 확인 후에 Running 상태와 동일 PID를 다시 요구한다.
- 직접 이미지 검사를 생략하는 경우는 OpenProcess의 ERROR_ACCESS_DENIED뿐이다. 그 외 오류, 열린 핸들에서 QueryFullProcessImageName 실패, 읽힌 이미지 경로 불일치는 차단한다. 진단의 LiveImagePathVerified=false와 근거 문구는 직접 실행 이미지 증명이 없음을 나타낸다.
- 서비스 실행 중 관리자가 등록 구성을 바꾼 경우 QueryServiceConfig는 다음 시작 시 적용될 구성을 반환할 수 있다. 따라서 SCM과 서명만으로 확인한 상태를 현재 실행 이미지 경로의 직접 증거로 보고하지 않는다. 이 검사는 신뢰하는 Windows·관리자 환경을 전제로 하며, 실행 중 관리자에 의한 악의적 변경까지 검증하는 기능은 아니다.
- Start의 /silent는 Start 자체 오류 상자만 억제하며, 다른 관리 UI의 표시나 전역 호환성 템플릿 적용을 차단하는 옵션이 아니다. [공식 Start 소스](https://github.com/sandboxie-plus/Sandboxie/blob/487d56309fafe90e2771d4c9004333fe01b77094/Sandboxie/apps/start/start.cpp)와 [SbieIniServer 소스](https://github.com/sandboxie-plus/Sandboxie/blob/487d56309fafe90e2771d4c9004333fe01b77094/Sandboxie/core/svc/sbieiniserver.cpp)에서 관리 UI 실행 경로를 별도로 확인했다. 이번 호환성 창이 어떤 경로로 열렸는지는 관찰하지 않아 원인을 단정하지 않는다.
## 재시작 복구와 실행 중 규칙 — 0.1.3

- [Sandboxie /reload 문서](https://sandboxie-plus.github.io/sandboxie-docs/Content/StartCommandLine.html#reload-configuration)는 설정 재조회가 이미 실행 중인 샌드박스 프로그램에 적용되지 않는다고 명시한다. 따라서 현재 설정 검사와 실행 중 프로세스의 실제 적용 규칙을 구분한다.
- [GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes)의 생성 시각과 PID, [QueryFullProcessImageName](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew)의 실행 경로를 같은 보유 핸들로 확인한다. [Windows 프로세스 핸들·식별자](https://learn.microsoft.com/en-us/windows/win32/procthread/process-handles-and-identifiers)에서 설명하는 PID 수명 때문에 PID 하나만으로 재시작 후 명령을 식별하지 않는다.
- 명령 생성 전에 기록을 저장하고 작업 잠금 아래에서 생성·식별 정보 기록을 수행한다. 식별 정보 저장 전에 중단된 작업은 현재 공식 Start.exe 후보가 하나라도 있거나 후보 조회 중 상태가 바뀌면 계속 차단한다. 후보가 없고 전용 박스 새 조회까지 성공했을 때만 완료한다. 종료 코드를 얻을 수 없는 경우에는 null로 남기며 성공 코드로 바꾸지 않는다.