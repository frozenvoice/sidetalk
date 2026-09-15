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
