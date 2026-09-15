# SIDETALK 계정 없는 격리 시험

`Sidetalk.IsolationProbe`는 카카오톡이나 기존 사용자 데이터를 읽지 않고, 전용 Sandboxie box의 파일·레지스트리 숨김, 쓰기 분리, 재실행 후 유지를 확인하는 작은 콘솔 프로그램입니다. 네 단계에서 사용하는 경로와 HKCU 키는 `prepare`가 만든 매니페스트에 고정됩니다. 임의 경로, 기존 시험 파일, 기존 레지스트리 키, 재분석 지점이 발견되면 실패하고 중단합니다.

프로그램은 파일을 삭제하지 않습니다. `host-seed`와 Sandboxie 안에서 실행하는 `box-write`는 각각 승인된 시험 토큰을 만들며, 마지막 `host-verify`는 호스트 토큰이 그대로인지 확인합니다. `box-write`와 `box-recheck`는 실행 중인 프로세스가 현재 사용자의 SIDETALK 전용 box에 있고, 각 프로세스의 native 격리 경로가 일치하는지 `Sidetalk.Core.SandboxieApi.Processes`로 확인한 뒤에만 파일·HKCU 쓰기를 진행합니다.

## 배포본과 소스

배포 ZIP의 `tools\IsolationProbe\Sidetalk.IsolationProbe.exe`를 사용합니다. self-contained 배포에는 시험 도구의 .NET 런타임도 포함됩니다. 소스에서 실행하려면 아래 dotnet 명령을 사용하세요. 배포 도구에서는 `dotnet run --project ... --` 부분을 시험 실행 파일 경로로 바꿉니다.

새 매니페스트는 보관할 작업 폴더 안에 만들고, 프로필은 SIDETALK에서 생성·검증된 해당 사용자 프로필만 사용합니다. 예제의 날짜별 경로는 이번 개발 검증 경로이므로 각자 새 경로로 바꾸세요.

## 순서

아래 예시의 `manifest`는 작업 폴더 안의 새 경로여야 합니다. `prepare`는 프로필과 시스템 경로를 검증하고 매니페스트만 생성합니다. 네 가지 시험 파일과 HKCU 키는 `host-seed`를 실행할 때 처음 만들어집니다.

```powershell
dotnet run --project .\src\Sidetalk.IsolationProbe -- prepare .\artifacts\setup-proposal-20260915\profile.proposed.json .\artifacts\verification\isolation-probe\manifest.json
dotnet run --project .\src\Sidetalk.IsolationProbe -- host-seed .\artifacts\verification\isolation-probe\manifest.json
```

그 다음 SIDETALK가 검증한 전용 box에서, 같은 manifest 경로를 인자로 `box-write`를 실행합니다. `Start.exe`는 `wait` 모드로 호출해야 합니다. 실행 파일은 `Sidetalk.IsolationProbe.exe`이며, 같은 명령의 종료만으로 로그인을 추정하지 않습니다.

```text
Sidetalk.IsolationProbe.exe box-write <manifest>
```

명령이 정상 종료되면 부모가 `Sidetalk.Core.SandboxieApi.FileRoot(profile.BoxName)` 아래에서 매니페스트의 `ResultPath` 상대 경로를 확인합니다. `box-recheck`도 같은 방식으로 전용 box 안에서 실행하며, `-recheck.json` 결과를 생성합니다. 두 단계 모두 파일 4개·HKCU 값 1개·현재 프로세스 box 검사를 완료한 뒤에만 성공 결과를 씁니다. 성공 JSON의 필드는 Schema, RunId, Phase, Passed, CheckedAtUtc입니다. 세부 검사별 결과나 PID는 담지 않으므로, 이 JSON 하나만으로 결론을 내리지 않습니다.

마지막으로 호스트에서 아래를 실행합니다.

```powershell
dotnet run --project .\src\Sidetalk.IsolationProbe -- host-verify .\artifacts\verification\isolation-probe\manifest.json
```

`host-verify`가 성공하면 호스트의 시험 토큰은 유지된 것입니다. box 토큰의 영속성은 box를 종료하고 같은 전용 box에서 `box-recheck`를 실행한 뒤 결과 JSON과 표준 출력을 확인해야 합니다. 실패하면 계정 로그인 단계로 넘어가지 않습니다.

box 단계가 실패하면 표준 오류에 원인과 함께 매니페스트 폴더의 `isolation-probe-failure-<run>-<guid>.json` 경로가 출력됩니다. 추적 파일도 기존 파일을 덮어쓰지 않고 생성하며, 오류 유형·메시지·단계·시각만 담습니다.

이 프로젝트는 Sandboxie를 설치하거나 설정을 변경하지 않습니다. 전용 설정 적용과 각 시험 단계는 SIDETALK의 승인된 운영 순서를 따릅니다. 시험 후 생성된 파일·레지스트리·box 데이터는 자동으로 지우지 않습니다.

## 경로와 증거의 범위

박스 내부에는 아직 Roaming 폴더가 없을 수 있습니다. 시험 도구는 Windows GetFolderPath의 DoNotVerify 옵션으로 폴더를 만들지 않고 논리 경로를 조회합니다. 비어 있거나 상대 경로이면 중단하며, 호스트에서 준비한 네 경로와 정확히 일치해야 합니다. [Microsoft 경로 조회 옵션 문서](https://learn.microsoft.com/en-us/dotnet/api/system.environment.specialfolderoption?view=net-8.0)를 참고하세요.

부모 검증자는 먼저 SIDETALK의 실제 설치·정책·소유 표식 검사가 통과하고 박스가 비었는지 확인합니다. box-write의 종료 코드가 0인지, 결과 파일이 API에서 확인한 전용 FileRoot 아래에만 있는지, RunId·Phase·Passed가 해당 실행과 일치하는지 확인합니다. 호스트에서 같은 논리 결과 파일이 생기면 실패입니다.

호스트에서 host-verify를 실행한 뒤 박스 프로세스가 모두 끝났는지 다시 확인합니다. 이후 box-recheck를 새 프로세스로 실행하고 같은 방식으로 물리적 결과 위치·값·종료 코드를 검증한 뒤 host-verify를 다시 실행합니다. 명령 종료 코드나 Passed 하나만으로 전체 검증을 완료하지 않습니다.

이 시험은 임의 파일과 HKCU 값만 다룹니다. 실제 카카오톡 데이터·로그인·송수신·알림, 호스트 WebCache COM/IPC 연결 차단, 다른 샌드박스의 무간섭을 대신 검증하지 않습니다.