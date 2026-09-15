# 검증 결과 — 2026-09-15

## 판정

**앱 구현과 앱 자체 사전 검증을 수행했습니다. 실계정 동시 사용 및 실제 Sandboxie 격리 호환성은 미검증입니다. 전체 수용 기준 충족으로 보고하지 않습니다.**

## 현재 PC에서 직접 확인한 사실

| 항목 | 결과 |
|---|---|
| Windows | Windows 11 Pro, x64, 10.0.26200 |
| 개발 환경 | .NET SDK 8.0.424 / Windows Desktop Runtime 8.0.30 |
| 공식 카카오톡 | 26.7.1.5263, Program Files (x86)의 공식 설치 |
| 카카오톡 서명 | Authenticode 유효, Kakao Corp. |
| 메인 카카오톡 프로세스 | 개발 시작·사전 검증 후 PID 8316 유지 |
| Sandboxie Plus | 설치 디렉터리·설치 등록·SbieSvc/SbieDrv 발견되지 않음 |
| SIDETALK 환경 변경 | 호스트 profile.json 생성·Sandboxie 설치·설정 적용 없음 |
| SIDETALK 실행 진단 | MissingDependency, CanLaunch=false, CanStop=false |
| 카카오톡 실행/종료 테스트 | 수행하지 않음 |

PID 유지는 이번 읽기 전용 조사·빌드·앱 화면 검증 동안 메인 프로세스가 유지되었다는 뜻입니다. 향후 보조 계정 실행 중의 무간섭 검증을 대체하지 않습니다.

## 자동/앱 자체 검증

- Release 빌드: 경고 0, 오류 0.
- 외부 테스트 패키지 없이 정책·프로필·명령 인자·템플릿 해시·버전 비교 콘솔 테스트 실행.
- 무작위 전용 환경, 재실행 시 프로필 유지, 손상 파일 재생성 거부, 다른 SID/저장 루트 거부.
- 소유 표식/자동 삭제/허용 목록/상속 예외/INI 입력 주입 검사.
- 임의 box·상대 경로·다른 실행 파일 거부, 명령을 셸 문자열로 연결하지 않는지 검사.
- WPF 실제 화면 렌더링 및 읽기 전용 JSON 진단 실행 성공.
- .NET 런타임을 포함하는 Windows x64 self-contained publish 성공.

테스트 목록·실제 실행 결과는 `tests/Sidetalk.Tests`와 `artifacts/verification`에 있습니다. 콘솔 테스트는 가짜 설정과 작업 폴더의 시험 프로필을 이용하며 Sandboxie 프로세스나 실제 계정을 실행하지 않습니다.

## 공식 설치 파일 사전 확인 (설치하지 않음)

- 파일: `Sandboxie-Plus-x64-v1.18.4.exe`, 24,853,640 bytes.
- 출처: [Sandboxie 공식 GitHub v1.18.4 릴리스](https://github.com/sandboxie-plus/Sandboxie/releases/tag/v1.18.4)의 동일 이름 asset.
- Authenticode: Valid, `NOVASYNC LABS PTE. LTD.`.
- SHA-256: `CFCEDA1B1A63ABCD2B3E5FBFDC4895F15DB56316F5A9F2F06AEA230E209B120E`.
- 작업 폴더 `artifacts/installers`에 보관. SIDETALK 배포 ZIP에는 포함하지 않음.

설치 파일 서명 검증은 설치 후 구성요소의 실제 서명·버전 확인을 대체하지 않습니다. 앱은 설치된 SandMan(1.18.4), Start/SbieDll/SbieSvc/SbieDrv(5.73.4)의 버전·서명을 확인합니다. 실행 중 서비스의 경로·상태와 API가 반환하는 활성 드라이버 버전도 검사합니다.

## 남은 검증과 제한

| 항목 | 상태 / 이유 |
|---|---|
| Sandboxie 서비스·드라이버/API ABI 연동 | 미검증 — 프로그램 미설치 |
| 기본 템플릿 허용 범위 | 엄격한 검사로 기본 호환 템플릿도 차단 가능. 설치 후 해당 규칙 검토 필요 |
| 호스트 인증·대화 데이터 비노출 | 공식 규칙/소스에 근거한 설계만 검토. 무해한 시험 데이터로 실제 격리 검사 필요 |
| 보조 데이터 영속성 | 설정상 자동 삭제 방지. Sandboxie 종료·재실행 실측 필요 |
| 별도 공식 로그인·기기 인증 | 미검증 — 사용자 직접 조작 필요 |
| 두 계정 동시 로그인·송수신·알림 | 미검증 — 실계정 사용 승인 및 사용자 확인 필요 |
| 메인 로그인·데이터·다른 box 무간섭 | 보조 환경 실행 중 검사 미수행 |
| 카카오 업데이트 시 sandbox 사본 | 미검증 — 보조 도움말 버전 비교 및 재검증 필요 |

다음 단계는 **Sandboxie 설치 승인 → 전용 설정 검토·적용 승인 → 무해한 격리 검사 → 실계정 검사 승인**입니다. 각 단계에서 실패하면 원인을 보고하고 중단합니다. 메인 데이터 복사, 보안 예외 추가, 패치/후킹, 핸들 강제 해제, 인증 우회는 후속 해결책으로 사용하지 않습니다.
