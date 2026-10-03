# 격리된 Windows 복구 시험

일반 CI는 Windows 빌드와 합성 회귀다. 이 절차는 **별도 Windows 11 x64 VM의 일반 사용자 세션과 공식 Sandboxie Plus 1.18.4**에서 실제 설정 저장·재조회·중단 복구를 확인한다. 현재 PC의 사용자 환경을 실패 fixture로 만들지 않는다. 실제 수행 결과와 미수행 항목은 [VERIFICATION.md](VERIFICATION.md)에 기록한다.

## 환경과 승인 범위

- 호스트와 클립보드·계정·사용자 폴더를 공유하지 않는 시험 VM을 사용한다. Sandboxie 서비스·드라이버 설치와 VM 재부팅은 이 시험 환경에만 적용한다.
- Sandboxie Plus 1.18.4의 서명과 사용자 구성요소 5.73.4 / 드라이버 5.73.2 및 기존 고정 해시를 그대로 검사한다. 최신 Sandboxie로 바꾸거나 검사를 완화하지 않는다.
- 별도의 로컬 시험 사용자로 일반 권한 실행을 확인한다. 설치용 관리자 권한과 SIDETALK 실행 권한을 구분한다.
- 카카오톡 로그인·송수신·인증·대화 파일 접근은 이 절차에 포함하지 않는다. 계정 없는 sentinel 파일·HKCU 키만 사용한다.
- INI·프로필·명령 기록·보존 자료의 시험 전후 해시와 승인 범위를 로컬 보고서에 남긴다. 원본 INI와 사용자 경로가 들어 있는 자료는 Git/일반 CI artifact에 넣지 않는다.

## 실제 시험표

| 항목 | 필요한 관찰 |
|---|---|
| 최초 준비 | 승인 후보와 최종 profile.json의 식별자·저장 경로 일치, 공식 SbieIni의 파일/API 결과 일치 |
| 여섯 템플릿 및 부분 집합 | 현재 있는 승인 선택만 제거, 다른 key/section 보존, 단순 종료 코드 0과 실제 결과 구분 |
| 재발 방지 옵션 | 실제 현재 사용자 섹션만 n 적용, 미설정 상태의 정상 실행 권한과 복구 권한 분리 |
| 현재 관리 UI | 승인한 경로·서명·버전·PID·생성 시각·사용자·세션의 관리 UI만 처리 |
| 다른 세션 | 다른 세션의 관리 UI 때문에 공유 INI 복구 차단, 해당 프로세스 종료 0회 |
| 설정 변경 중 중단 | 실제 SbieIni 변경 직후 작업 프로세스 중단, 새 프로세스로 조회·승인·재조정 |
| 프로필 게시 중 중단 | .profile 임시 파일과 승인 후보 보존, 같은 소유 식별자와 저장 경로로 복원 |
| 이벤트 기록 중 중단 | 완전한/잘린/충돌/출처 불명 기록 분류, 불충분한 근거의 완료 처리 없음 |
| 보존 중 중단 | 원본/보존본/메타데이터 검증 후 재시도, 원본 손실·덮어쓰기 없음 |
| 재발과 자동 동의 | 같은 범위만 재처리, 철회 후 자동 변경 없음, 정상 재조회에 추가 백업 없음 |
| 데이터 보존 | profile.json, box 파일/HKCU sentinel, 명령 기록, 다른 box와 무관한 사용자 설정 유지 |
| 일반 사용자 GUI | VM 탐색기의 run-local.cmd, 복구 안내·진행·실패·새 실행 권한 표시 |
| 읽기 전용 진단/렌더 | 설정 쓰기·관리 UI 종료·보존 이동·저널 재조정 0회 |

테스트를 위한 중단 위치를 코드에서 주입한 경우 **실제 Windows/SbieIni 외부 경계 + 주입한 중단 시점**으로 표시한다. 이것을 전원 장애나 모든 Windows 실패 조건의 실측으로 확대하지 않는다. 현재 프로세스의 TOKEN_QUERY/이미지/시각 API 검사도 전체 Sandboxie 통합 시험과 구분한다.

## 선택 실행 도구

`Sidetalk.Tests.exe --windows-recovery-integration <phase> C:\SIDETALK-Lab\reports\<새 이름>.json`은 일반 콘솔 회귀에 포함되지 않는 실제 외부 경계 검사다. `SIDETALK-LAB`이라는 VirtualBox 게스트와 정확한 승인 marker, 가상화되지 않은 일반 사용자 프로필을 확인한 뒤에만 진행한다. marker와 별도 시험 계정은 환경 설치 단계에서 준비하며 실제 사용자 PC에 만들지 않는다. 기존 보고서는 덮어쓰지 않는다.

새 시험 사용자에서는 `crash-profile`이 실제 임시 프로필 게시 직후 종료 코드 77로 중단된다. 새 프로세스의 `reconcile`이 원본을 보존하고 처음 승인한 후보를 다시 사용한다. 그 뒤 `seed-six → repair-auto`, `seed-subset → repair-auto`, `seed-six → crash-change → reconcile`로 승인 범위와 실제 설정 변경 중 중단을 확인한다. `crash-change`의 77은 의도한 중단이며 복구 성공 결과가 아니다. 정상 상태에서 `repair-auto`를 다시 실행하면 추가 백업이 없어야 한다.

`optional-setting`은 실제 정책을 유지한 채 현재 사용자 재발 방지 옵션만 미설정으로 만들고 새 컨트롤러의 실행 권한을 확인한다. `management-ui`는 별도로 검증한 현재 사용자 SandMan을 시작하여 승인된 복구에서만 정리한다. `inspect-foreign`은 외부에서 별도 시험 사용자/세션의 관리 UI를 띄운 상태를 읽어 공유 INI 복구 차단을 확인하며 다른 사용자의 프로세스를 종료하지 않는다. `inspect`는 조회만 수행한다. 최초 설치의 다른 box나 알 수 없는 설정 때문에 차단되면 원본을 보존하고 fixture 준비와 제품 복구 결과를 구분한다.

`seed-six → crash-record → crash-preserve → reconcile`은 실제 SbieIni 검증 직후, 현재 작업의 확인된 ChangeIntent 다음 이벤트 위치에 잘린 JSON을 저장하여 중단한 뒤 보존 메타데이터 게시와 원본 이동 사이에도 중단한다. 원본/메타데이터/부분 파일은 실제 NTFS 파일이며 실패 시점과 부분 이벤트 바이트는 시험 도구가 주입한다. `command-intent → VM 정상 재부팅 → command-reconcile`은 PID 없는 실제 intent와 네이티브 DPAPI/uptime 근거를 저장하여 이전 부팅 분기를 확인한다. Start.exe를 실행하지 않은 intent라는 점과 종료 코드 미확인을 보고서에 남긴다. 재부팅 후 uptime이 이미 기록값 이상이면 이 시험은 실패하며 성공으로 보정하지 않는다.

이 도구의 보존 sentinel은 프로필 바이트, 전용 box의 계정 없는 시험 파일, 무관한 HKCU 값과 Sandboxie 설정이다. 실제 계정 데이터·box 레지스트리 하이브·다른 box 데이터의 실측 보존 결과로 확대하지 않는다. 그 항목은 해당 fixture를 준비한 별도 검사 또는 합성 회귀 여부를 명시한다.

관리 UI의 정상 종료 등 외부 동작이 다른 설정을 새로 저장하면 기존 보존 기준 비교는 실패해야 한다. 원래 기준이나 새 box를 지워 시험을 계속하지 않는다. `checkpoint-external-settings <새 보고서> <새 baseline.json>`은 기존 프로필·소유 정보·파일/HKCU sentinel이 그대로인지 확인하고, 현재 정책·빈 전용 box·INI/API 일치를 검증한 뒤 외부 설정 차이만 별도 불변 시험 기준에 기록한다. 원래 기준의 바이트/해시와 관찰한 설정을 보고서에 남기며 설정 쓰기나 복구 성공으로 계산하지 않는다. 이후 각 phase의 선택적 네 번째 인자로 그 기준을 명시한다. 다른 box가 생긴 구간에서는 `seed-optional → crash-record → crash-preserve → reconcile`로 현재 사용자 옵션만 시험한다. 전역 템플릿 재발 주입은 다른 box가 있으면 계속 거부한다.

원본 보존의 파일 교체 경합은 실제 Windows API로 별도 검사한다. [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)의 배타적 읽기/DELETE 핸들을 유지하고 [FILE_RENAME_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info)로 동일 파일만 덮어쓰기 없이 이동한다. 원본 경로에 새 파일이 끼어들거나 기존 보존본이 덮어써지지 않는지 확인한다.

## CI 범위

[Windows verification](../.github/workflows/windows-verify.yml)은 PR과 main push에서 `global.json` SDK 및 PowerShell 7로 `scripts/verify.ps1 -UiChecks`, `tests/Sidetalk.BuildChecks/Program.ps1`를 순서대로 실행한다. 빌드 상태를 공유하는 병렬 verify/publish는 없다. 배포본 교체·릴리스·Sandboxie 설치는 하지 않는다. `contents: read`와 고정된 공식 Actions 커밋을 사용하고 checkout 자격 증명을 남기지 않는다.

실패 로그와 합성 PNG만 7일간 보관한다. 실제 프로필/계정/INI 백업이나 fixture 전체 폴더는 업로드하지 않는다. Windows Server 기반 hosted runner 성공은 Windows 11 일반 사용자 GUI나 Sandboxie 승인 복구 성공의 증거가 아니다.

근거: [GitHub PowerShell 워크플로](https://docs.github.com/en/actions/tutorials/build-and-test-code/powershell), [setup-dotnet](https://github.com/actions/setup-dotnet), [artifact 보관](https://docs.github.com/en/actions/tutorials/store-and-share-data).
