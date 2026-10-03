# 중단된 명령의 Windows 근거

새 명령 저널은 schema 2다. 기존 schema 1을 그대로 읽으며 사용자·세션·부팅 근거를 추측해서 채우지 않는다. intent/started/completed의 schema와 선택적 근거는 일치해야 한다. 완료 기록은 원래 schema를 유지한다.

명령 생성·신원 기록·대기·박스 재조회가 실패하면 최초 원인과 단계를 immutable `failed.json`에 보존한다. 취소도 실패 이벤트로 구분하며 intent/started를 완료로 바꾸지 않는다. 실패 이벤트 발행까지 실패하면 원래 명령 오류를 유지하고 보조 오류를 진단에 남긴다. 실패한 임시 파일은 원본대로 남기며 기존 실패 이벤트를 덮어쓰지 않는다.

## 프로세스 신원과 권한

- [GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes)는 `PROCESS_QUERY_LIMITED_INFORMATION`으로 생성 시각을 조회한다. PID 재사용은 기존처럼 생성 시각 불일치로 확인한다.
- [QueryFullProcessImageNameW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew)는 같은 제한 조회 권한으로 실제 실행 경로를 확인한다.
- [OpenProcessToken](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-openprocesstoken)은 제한 조회 프로세스 핸들과 `TOKEN_QUERY`를 사용한다. [GetTokenInformation](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-gettokeninformation)의 `TokenSessionId`와 WindowsIdentity의 토큰 SID를 같은 핸들에서 얻어 PID 재사용 창을 만들지 않는다. 다른 계정의 토큰을 열 수 없는 경우에는 신원 미확인으로 남는다. 디버그 권한을 활성화하지 않는다.
- [대기 권한](https://learn.microsoft.com/en-us/windows/win32/sync/synchronization-object-security-and-access-rights) `SYNCHRONIZE`와 유지한 프로세스 핸들로 종료를 확인한다. 접근 거부는 종료 근거가 아니다. 종료 코드를 읽지 못한 경우 null이며 성공 코드 0을 만들지 않는다.

PID 없는 후보의 대기 권한만 거부되는 경우에는 제한 조회 권한만 다시 요청하여 이미지·토큰 범위를 확인할 수 있다. 이 경로에서 대기를 호출하지 않는다. [GetExitCodeProcess](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getexitcodeprocess)는 제한 조회 권한으로도 실제 종료 코드를 조회할 수 있지만, 259(STILL_ACTIVE)는 모호하므로 종료로 처리하지 않는다. 이미지/토큰 또는 실제 종료를 확인하지 못한 후보는 계속 차단한다.

PID가 없는 intent는 이름이 Start인 후보를 조회하되 먼저 정확한 설치 이미지 경로로 좁힌다. 해당 경로의 살아 있는 후보에 대해서만 기록된 원래 사용자·세션과 토큰 정보를 대조한다. 신원이 확인된 다른 사용자/세션은 무관한 후보로 제외할 수 있다. 같은 사용자/세션의 후보는 실제 명령과 관련 있을 가능성을 남겨 차단한다. 구버전 기록이나 이미지/토큰 조회 실패는 무관함을 추측할 수 없다. 보호된 근거가 있는데 인증에 실패한 경우에는 해당 사용자·세션 메타데이터로 후보 범위를 좁히지 않는다. 벽시계 생성 시각이나 명령줄 문자열의 일부만으로 후보를 제외하지 않는다.

## 제한적인 이전 부팅 증명

[GetTickCount64](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-gettickcount64)의 네이티브 값은 Windows 시작 후 경과한 밀리초다. 시스템 시계 조정과 별개이며 절전/최대절전 시간도 포함한다. 명령의 intent를 저장하기 전에 이 값과 작업 ID·사용자·세션을 [CryptProtectData](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata)의 `CRYPTPROTECT_LOCAL_MACHINE | CRYPTPROTECT_UI_FORBIDDEN`으로 보호한다. 머신에 묶인 근거이며 로컬 사용자의 임의 파일 수정 권한을 막는 보안 경계로 사용하지 않는다.

[CryptUnprotectData](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptunprotectdata)로 확인한 내용이 원래 작업과 정확히 일치하고 **현재 네이티브 uptime이 기록값보다 작을 때만** 이전 부팅 명령이라고 확인한다. 같은 부팅에서 이 값이 뒤로 돌아가지 않으므로 이 조건은 벽시계 추측이 아니다. 현재 값이 같거나 더 큰 경우에는 재부팅 여부를 알 수 없으며 기존 프로세스 신원 검사로 돌아간다. 따라서 재부팅 뒤 오래 기다린 모든 경우를 자동 해결한다고 주장하지 않는다. DPAPI나 근거 조회가 불가능하면 해당 근거를 사용하지 않는다. 재부팅·사용자 진술·시간 초과만으로 명령을 완료하지 않는다.

신원 미확인 핸들은 새로고침에서 다시 조회할 수 있다. 원래 살아 있는 명령 핸들은 유지한다. 종료/부재/재사용/이전 부팅을 확인했더라도 컨트롤러가 현재 소유 정보·경로·정책과 전용 박스 프로세스를 새로 조회한 뒤에만 완료 저널을 발행한다. 진단·렌더는 확인 결과를 메모리에 표시하고 완료 파일은 쓰지 않는다.

## 검증 경계

콘솔 러너의 `Windows API` 표시 검사는 실제 현재 프로세스 생성 시각·이미지·토큰 SID/세션, 실제 존재하지 않는 PID, 실제 머신 DPAPI 보호/복호화/변조 거부와 네이티브 uptime을 사용한다. 정확한 종료 상태 검사는 `cmd.exe /d /c exit 17`이라는 무해한 자식을 실행하고 원래 핸들을 유지한다. 임시 시험 폴더에 시스템 cmd.exe를 Start.exe라는 이름으로 복사한 두 번째 자식은 표준 입력을 기다리는 `set /p`만 실행하며 시험 뒤 입력을 제공해 정상 종료한다. 이 자식으로 다른 실행 경로의 동명 프로세스 제외와 동일 경로·사용자·세션의 후보 차단을 실제로 확인한다. 시스템 PID 4는 권한에 따라 접근 거부 또는 생성 시각 불일치가 가능하며 실제 관찰 상태를 INFO 로그에 표시하고 접근 거부를 종료로 오인하지 않는지 검사한다.

이전 부팅의 분기 검사는 가짜 외부 근거 경계이고 실제 재부팅 검사가 아니다. 실제 별도 Windows 환경의 재부팅·접근 불가 재사용 PID·다른 사용자/세션 명령·Sandboxie 설정 복구 통합 시험은 별도로 수행해야 한다. 실제 로그인·송수신이나 관리 UI 종료·설정 변경은 이 API 검사에 포함하지 않는다.
