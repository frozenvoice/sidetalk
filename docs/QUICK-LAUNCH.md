# 빠른 실행 · 바로가기 · 보조 창 식별

초기 설정과 격리 검사를 마친 사용자용 기능이다. 모든 실행은 기존 설치·서명·소유권·프로필·격리 정책 검증, controller gate와 CommandJournal을 통한다. 공식 카카오톡의 로그인과 기기 인증은 기존 흐름을 따른다.

## 한 번 설정한 뒤 실행

1. SIDETALK를 일반 사용자 권한으로 열고 **실행 · 식별** 탭을 선택한다.
2. 원하는 **바탕화면에 ‘보조 카카오톡 실행’** / **시작 메뉴에 ‘보조 카카오톡 실행’**을 선택하고 **바로가기 선택 저장**을 누른다.
3. 만든 **보조 카카오톡 실행**을 실행한다. 작은 진행 창에서 현재 상태를 검사하고 기존 실행 확인창을 표시한다. 메인 창을 먼저 열 필요가 없다.
4. 매번 확인창을 생략하려면 **이 전용 프로필의 빠른 실행 허용**을 명시적으로 선택하고 **빠른 실행 동의 저장**을 누른다. 기본 OFF이며 현재 프로필·설치·격리 설정의 같은 범위에서만 사용한다. 해제하려면 선택을 끄고 다시 저장한다.

동의는 설정 복구·강제 종료·데이터 삭제·권한 상승 동의가 아니다. 범위가 바뀌거나 동의 기록이 불명확하면 저장된 확인 생략 권한을 사용하지 않는다. 실제 격리 검사 실패는 기존 확인창으로도 우회할 수 없다.

명시적 진입점은 `Sidetalk.exe --launch-secondary`다. 바로가기의 실행 대상과 고정 인자는 Unicode Shell Link의 별도 속성에 저장하므로 공백이 있는 실행 경로를 shell 명령 문자열로 합치지 않는다. Sandboxie `Start.exe`를 바로가기에서 직접 호출하지 않는다. [Microsoft IShellLinkW](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ishelllinkw), [IPersistFile](https://learn.microsoft.com/en-us/windows/win32/api/objidl/nn-objidl-ipersistfile).

## 로그인 시 실행과 제거·업데이트

**Windows 로그인 시 빠른 실행**은 확인 생략 동의와 별개이며 기본 OFF다. 선택 후 **바로가기 선택 저장**을 누르면 현재 사용자 Startup 폴더에 같은 빠른 실행 경로와 `--login-startup` 인자를 저장한다. 실행 시 저장된 선택·설치 경로·바로가기 상태와 현재 사용자의 관리자 권한 없는 대화형 세션을 다시 확인한다. SYSTEM 작업, 관리자 실행, 작업 스케줄러나 자동 로그인을 추가하지 않는다.

원하는 선택을 끄고 저장하면 해당 SIDETALK 바로가기만 제거한다. **내 바로가기 모두 제거**는 세 위치의 SIDETALK 소유 바로가기를 제거하고 로그인 실행 선택을 끈다. 빠른 실행 확인 생략 동의는 별도로 해제한다. 설치 위치를 옮겼다면 새 `Sidetalk.exe`에서 같은 탭을 열어 선택을 저장한다. 이전 소유가 확인된 링크만 현재 경로로 갱신한다.

같은 이름의 사용자 링크나 수정된 링크는 덮어쓰거나 삭제하지 않는다. 소유 manifest·고유 설명·내용 해시·실행 대상·인자가 모두 일치해야 관리한다. 링크가 사라지거나 이전 설치를 가리키면 로그인 실행을 허용하지 않으며 사용자의 선택 저장으로 재확인한다. 네트워크/재분석 경로, 손상 manifest와 미완료 변경도 차단한다.

## 진행·취소·중복·실패

- 같은 사용자/세션의 기존 SIDETALK가 있으면 요청을 그 인스턴스로 전달한다. 진행 중인 중복 요청은 나중에 재실행하지 않는다.
- **요청 취소** 또는 진행 창 닫기는 준비 중 요청을 취소한다. 이미 보낸 명령은 결과와 기록을 확인할 때까지 유지하며, 불확실한 상태에서 런처만 종료하지 않는다.
- 검증된 전용 환경에 이미 프로세스가 있으면 새 명령을 보내지 않고 SIDETALK에 현재 상태를 표시한다. 본계정이나 다른 box의 창을 조작하지 않으며 외부 창 활성화도 제공하지 않는다.
- 정상 명령 완료와 새 전용 box 조회가 함께 확인된 standalone 실행은 진행 창을 닫고 종료한다. 별도의 숨은 polling 프로세스를 남기지 않는다. 프로세스 확인은 로그인·송수신 성공이 아니다.
- 설치/서명·프로필·정책 오류, 미완료 명령, timeout과 불확실한 결과는 같은 controller를 유지한 메인 창의 이유·복구 흐름으로 이어진다. 자동 재실행이나 기록 삭제는 하지 않는다.

실패하면 **상태 새로 확인**과 **초기 설정 · 진단**의 차단 이유를 확인한다. 바로가기 설정을 저장하지 못하면 **설정 다시 확인**으로 상태를 읽는다. 복원 실패는 표시된 변경 폴더·pending·원본 백업을 보존하며, 파일 삭제로 성공 상태를 만들지 않는다. 빠른 실행 동의 이력이나 미완료 표시 변경도 자동 초기화하지 않는다.

## 보조 창 색·문구

**실행 · 식별**에서 현재 색을 확인하고 팔레트를 선택한다. 미리보기는 SIDETALK 내부 예시이며 실제 카카오톡 창이 아니다. 전용 환경이 비어 있고 표시 변경 조건이 확인된 경우 **표시 설정 저장 · 재확인**으로 저장한다. Sandboxie 관리 UI가 열려 있어 변경이 차단되면 사용자가 정상 종료한 뒤 다시 확인한다. SIDETALK가 관리 UI를 강제 종료하지 않는다.

변경은 전용 box의 `BorderColor` 색 부분만 바꾸며 기존 표시 방식·두께·검증된 나머지 필드를 보존한다. INI의 색 순서는 BGR이고 화면은 RGB다. 예를 들어 기존 `#21685B,ttl,4`는 화면에서 `#5B6821`이며 새 기본색으로 덮어쓰지 않는다. [Sandboxie 1.18.4의 색 변환·저장 코드](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.4/SandboxiePlus/SandMan/Windows/OptionsGeneral.cpp).

**Sandboxie 전용 환경 이름에 ‘보조 계정’ 표시**를 선택하면 공식 `BoxAlias`에 그 문구를 설정한다. 선택하지 않으면 기존 별칭을 보존하며 이름을 지우지 않는다. 기존 `BoxNameTitle=y`의 공식 표시를 유지한다. 전용 box 식별자와 저장 경로는 바뀌지 않는다.

테두리는 SandMan이 실행 중이어야 표시된다. 별칭 표시 정책과 사용자 정의 제목줄 때문에 문구가 보이지 않을 수도 있다. 저장 후 재확인 성공은 설정 파일/API 일치의 결과이며 실제 외부 창 표시의 증명이 아니다. 공식 지원을 벗어난 overlay·DLL 주입·카카오톡 패치·메시지 읽기를 추가하지 않는다. [BorderColor 공식 문서](https://sandboxie-plus.github.io/sandboxie-docs/Content/BorderColor/), [1.18.4 공식 제목 표시 구현](https://github.com/sandboxie-plus/Sandboxie/blob/v1.18.4/Sandboxie/core/dll/guititle.c).

표시 변경은 같은 명령 잠금 아래 의도·INI 백업·검증 기록을 보존한다. 부분 실패가 있으면 기존에 보존한 선택을 다시 확인해 명시적으로 계속하며, 다른 새 색으로 덮거나 전체 INI를 복원하지 않는다. 표시 설정 변경이 확인 생략 동의 범위를 바꿀 수 있으므로 저장 뒤 빠른 실행 동의 상태도 확인한다. 색·문구는 로그인 계정이나 격리 성공의 증명이 아니다.

## 합성 검증 재현과 남은 항목

저장소 루트에서 다음 명령은 fake controller, 임시 프로필/INI 및 주입된 링크 위치를 사용한다. 실제 사용자 Startup·바로가기·카카오톡·Sandboxie box/설정은 시험 대상이 아니다.

```powershell
pwsh -NoProfile -File scripts/verify.ps1 -UiChecks
```

이 검사는 승인 없음/범위 변경·기존 실행·경쟁·취소·timeout/실패와 기존 fail-closed 회귀, Startup 기본 OFF·공백/Unicode 경로·업데이트·소유 링크만 제거, 저장 실패/복원 실패의 근거 보존을 포함한다. Shell Link 검사는 실제 COM을 사용하되 링크 저장 위치는 격리된 fixture다. 실패/복구 재현을 위해 실제 사용자 INI나 계정을 손상시키지 않는다.

WPF Application fixture는 production Startup 이벤트를 분리하고 상태·controller factory·설정 위치를 주입한다. 새 빠른 실행/설정/broker 20개 동기화 사례에는 초기화 실패, 잘린 송신, 지연된 수신 ACK도 포함한다. broker 초기화는 한 번 시도하며 실패를 표시하고, 유효한 요청의 수신 확인을 기다린다. 실제 사용자 앱이나 계정으로 재현하지 않는다.

기본 UI 빌드가 실패했다면 성공한 Core 검사를 반복하지 않고 실패한 단계만 다음과 같이 확인할 수 있다. 실패 로그를 보존하며 기존 테스트를 skip하지 않는다.

```powershell
dotnet build tests/Sidetalk.UiChecks/Sidetalk.UiChecks.csproj -c Release --nologo --disable-build-servers -m:1 -nr:false -p:BuildInParallel=false
if ($LASTEXITCODE -ne 0) { throw 'UI fixture build failed.' }
$fixtureOutput = Join-Path ([IO.Path]::GetTempPath()) ('sidetalk-quick-fixtures-' + [Guid]::NewGuid().ToString('N'))
dotnet tests/Sidetalk.UiChecks/bin/Release/net10.0-windows/Sidetalk.UiChecks.dll $fixtureOutput
if ($LASTEXITCODE -ne 0) { throw 'UI fixture checks failed.' }
```

이미 빌드한 새 기능의 실패만 좁혀 진단하려면 아래 fixture 모드를 사용할 수 있다. 이는 기존 WPF 검사를 포함하는 최종 통합 명령을 대신하지 않는다. `CurrentUserOnly` pipe의 접근 거부는 실패 로그로 남기고 실행 도구의 권한/파일 가상화 맥락을 구분한다. ACL을 완화하거나 Startup을 관리자 실행으로 바꿔 성공 처리하지 않는다.

```powershell
$quickFixtureOutput = Join-Path ([IO.Path]::GetTempPath()) ('sidetalk-quick-only-' + [Guid]::NewGuid().ToString('N'))
dotnet tests/Sidetalk.UiChecks/bin/Release/net10.0-windows/Sidetalk.UiChecks.dll $quickFixtureOutput --quick-launch-only
if ($LASTEXITCODE -ne 0) { throw 'Quick-launch fixture checks failed.' }
```

현재 작업의 실제 수행 결과와 화면 비교는 [VERIFICATION.md](VERIFICATION.md)를 따른다. 실제 로그인 세션의 Startup 실행, 사용자 바로가기 클릭과 실제 계정 실행, Windows 11 + Sandboxie의 테두리/제목 표시, 외부 창 행동과 실기기 성능은 별도 환경에서 확인할 항목이다. 이전 버전의 계정 확인이나 hosted Windows CI를 이번 기능의 실제 통합 성공으로 바꿔 표시하지 않는다.
