# 검증 운영 정책 — 2026-10-06

일반 push/PR에는 원격 검증을 실행하지 않는다. 일반 개발 작업은 변경 범위에 필요한 로컬 검사와 결과 보고로 완료할 수 있다. 원격 CI dispatch·watch·완료 대기·자동 rerun은 일반 완료 조건이 아니다. 아래의 과거 실행·CI 미확인 기록은 당시 검증 범위를 나타내며 현재 작업의 원격 실행 의무를 만들지 않는다.

| 목적 / 변경 범위 | 명령과 완료 기준 |
|---|---|
| 일반 코드 개발 | `pwsh -File scripts/verify.ps1`: Release 앱 빌드·자체 콘솔 안전성 테스트·IsolationProbe 빌드 성공 |
| UI 변경 | `pwsh -File scripts/verify.ps1 -UiChecks`: 기본 검증과 관련 합성 WPF 검사·이미지 확인 |
| 빌드/검증 스크립트 변경 | `pwsh -File tests/Sidetalk.BuildChecks/Program.ps1`: 임시 fixture의 실패 전파·패키징 guard·롤백 회귀 |
| 설치/업데이트 변경 | 기본 검증 후 `pwsh -File tests/Sidetalk.InstallerChecks/Program.ps1`: 고정 컴파일러와 테스트 전용 AppId/경로의 설치·업데이트·제거 회귀 |
| 명시적 전체 원격 검증 / 릴리즈 공개 전 | `Windows verification`의 `workflow_dispatch`: 아래 세 검사와 필수 단계가 모두 실행되어 성공 |

`verify.ps1`는 공통 `Invoke-SidetalkVerification`을 사용하며 기본값에서 UI·설치기 생성·설치 회귀를 실행하지 않는다. `-UiChecks`는 명시적 선택이다. 기본 검증과 배포용 `build.ps1`의 검증 의미를 축소하지 않는다. 패키징은 기본 검증을 포함하므로 직전에 같은 기본 검증을 반복할 필요는 없다. `build-local.cmd`는 SDK/컴파일러 준비·패키징·설치기 생성·대화형 설치/업데이트 경로이며 단순 개발 검사를 위한 명령이 아니다. `-NoInstall` 및 기존 설치·데이터 보존 계약은 [로컬 실행 안내](LOCAL-RUN.md)를 따른다.

수동 전체 검증은 기존 구현을 순서대로 실행한다.

```powershell
pwsh -File scripts/verify.ps1 -UiChecks
pwsh -File tests/Sidetalk.BuildChecks/Program.ps1
pwsh -File tests/Sidetalk.InstallerChecks/Program.ps1
```

각 명령의 비영 종료 코드·취소·미실행을 성공으로 취급하지 않는다. 원격 workflow도 checkout/SDK 준비와 세 검사가 모두 성공해야 통과하며 로그·합성 WPF 이미지를 보관한다. 일반 이벤트의 경량 자동 CI·정기 실행·성공만 반환하는 job을 대신 두지 않는다.

hosted runner는 Windows Server 2022에서 고정된 SDK·Inno Setup 환경의 빌드와 합성/격리 fixture 경계를 검사한다. InstallerChecks는 임시 `SIDETALKInstallerTest-*` AppId·설치/시작 메뉴/바탕 화면 경로와 합성 데이터를 사용하며 사용자용 설치본을 시험 설치하지 않는다. 이 결과는 실제 Windows 11 + Sandboxie Plus 1.18.4 통합·로그인·송수신 성공을 의미하지 않는다. 실환경 검증은 별도로 [ACCEPTANCE.md](ACCEPTANCE.md), [ISOLATION-PROBE.md](ISOLATION-PROBE.md), [WINDOWS-RECOVERY-TEST.md](WINDOWS-RECOVERY-TEST.md)를 따른다.

## 수동 릴리즈 사전 검증

현재 게시 workflow나 GitHub Release 자동화는 없다. `prepare-distributions.ps1`은 설치형과 포터블을 같은 self-contained publish에서 준비하는 로컬 경로이며 `build-local.cmd`는 사용자 로컬 설치 경로다. 준비 스크립트는 설치·앱 실행·GitHub 업로드·태그 생성을 하지 않는다. 새 게시 시스템을 만들지 않고, 설치기 전달/릴리즈 공개 담당자가 다음 조건을 공개 **전에** 확인한다.

1. 공개 대상의 전체 커밋 SHA를 고정하고 깨끗한 checkout을 준비한다. `git rev-parse HEAD`와 `git status --porcelain` 결과를 기록한다. 검증 후 소스·빌드 입력을 바꾸거나 대상 ref가 이동하면 새 SHA를 기준으로 다시 판단한다.
2. 그 SHA를 가리키는 ref에서 `Windows verification`을 수동 실행한다. 완료된 실행의 URL·run ID·attempt·`head_sha`·event를 기록하고 `head_sha`가 공개 대상 전체 SHA와 같고 event가 `workflow_dispatch`인지 확인한다. workflow 전체와 `verify` job이 성공하고 checkout, SDK 준비, `Verify current source and synthetic WPF fixtures`, `Verify build failure propagation and packaging guards`, `Verify installer compile and isolated install regression`가 모두 `success`인지 확인한다. 필수 단계의 실패·취소·skipped·누락, 과거 성공 실행이나 다른 SHA는 게시 조건을 충족하지 않는다. 로그와 이미지도 실행의 같은 attempt에서 확인한다. 공개 후 `release: published` 등의 이벤트를 사전 검증으로 사용하지 않는다.
3. 같은 SHA의 깨끗한 checkout에서 `pwsh -File scripts/prepare-distributions.ps1`을 실행하고 종료 코드 0을 확인한다. 이 명령은 `build.ps1 -SelfContained`의 기본 앱·안전성 테스트·시험 도구 검증과 앱/IsolationProbe의 `win-x64` publish를 한 번씩 수행하고, 고정된 같은 `artifacts/publish/win-x64`로 Setup과 포터블 ZIP을 만든다. 합성 UI·BuildChecks·InstallerChecks는 이 명령에 포함되지 않으며 2번의 전체 원격 검사와 별도 Windows 11 검증을 따른다. 전체 원격 성공으로 로컬 패키징 검증을 생략하지 않는다. 기존 배포 산출물이 사용 중이면 사용자가 정상 종료한 후 진행하며 사용자 프로필·계정·Sandboxie 설정을 변경하지 않는다.
4. 성공한 `artifacts/release/SIDETALK-distributions.json`의 `sourceCommit`이 공개 대상 SHA와 같고 `sourceDirty=false`, `version`이 `Directory.Build.props`와 같고 `runtimeIdentifier=win-x64`, `selfContained=true`인지 확인한다. manifest의 전체 payload 상대 경로·길이·SHA-256과 배포물 해시를 보존한다. 스크립트는 ZIP 전체 파일 집합과 publish를 비교하고 앱·IsolationProbe 버전 및 self-contained 런타임을 검사하며, Setup 컴파일 전후 publish가 바뀌면 실패한다. 실패 시 이전 배포 묶음이 남을 수 있으므로 과거 파일·manifest를 이번 성공으로 취급하지 않는다. InstallerChecks의 합성 fixture Setup은 사용자용 Setup과 바이트가 다르므로 그 검사만으로 생산 설치기의 동일성이나 실제 계정 보존을 주장하지 않는다.
5. 같은 묶음의 `SIDETALK-Setup.exe`, `SIDETALK-<version>-win-x64-portable.zip`, 개별 `.sha256`, `SHA256SUMS.txt`, `SIDETALK-distributions.json`과 해당 서명 상태 파일만 공개 준비 위치로 복사한다. Setup과 ZIP의 SHA-256이 manifest·개별 `.sha256`·`SHA256SUMS.txt` 및 공개 준비 파일과 일치해야 한다. ZIP은 전체를 압축 해제하여 모든 상대 경로·길이·파일 해시가 manifest payload와 같고 `Sidetalk.exe`, DLL·런타임·필수 리소스, `tools/IsolationProbe`가 포함됐는지 확인한다. 설치 원본·압축 해제본·격리 환경에 설치한 앱의 EXE SHA-256과 버전을 비교한다. Setup 자체 해시와 앱 EXE 해시를 혼동하지 않는다. 재빌드·재압축·서명으로 파일이 바뀌면 해시와 manifest를 다시 확정한다.
6. 아래 Windows 11 검증의 실제 수행/미수행 범위, 공개 대상 SHA, 전체 원격 실행/필수 단계 결과, payload 목록, 배포 파일 해시와 서명 상태를 함께 검토한다. 공개할 때 **GitHub Release 한 건**에 설치형과 포터블 및 위 부속 파일을 함께 업로드하고 업로드한 파일의 해시도 보존한 목록과 비교한다. 버전은 `SidetalkVersion`을 따르며 실행할 앱을 고르는 다운로드 두 개로 안내한다. 이 수동 확인이 누락되면 공개하지 않는다. 로컬 준비나 PR 병합은 태그/공개 Release 생성을 승인하거나 대신하지 않는다.

독립적인 payload 해시 목록이 필요하면 다음처럼 보관할 수 있다. 출력 경로는 payload 밖이며 해시는 사용자 데이터가 아닌 배포 입력만 대상으로 한다. 정식 동시 준비 스크립트는 같은 전체 목록을 manifest에 기록하므로 이 예제를 위해 Setup을 중복 컴파일하지 않는다.

```powershell
$payloadRoot = (Resolve-Path artifacts/publish/win-x64).Path
Get-ChildItem -LiteralPath $payloadRoot -Recurse -File | Sort-Object FullName |
    Select-Object @{ Name = 'Path'; Expression = { [IO.Path]::GetRelativePath($payloadRoot, $_.FullName) } },
        Length, @{ Name = 'SHA256'; Expression = { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } } |
    Export-Csv -LiteralPath artifacts/payload-before.csv -NoTypeInformation -Encoding utf8
# 컴파일 후 같은 명령을 artifacts/payload-after.csv로 저장한다.
Compare-Object (Import-Csv artifacts/payload-before.csv) (Import-Csv artifacts/payload-after.csv) -Property Path, Length, SHA256
Get-FileHash -LiteralPath artifacts/release/SIDETALK-Setup.exe -Algorithm SHA256
Get-Content -LiteralPath artifacts/release/SIDETALK-Setup.exe.sha256
```

`Compare-Object`에 차이가 없고 설치기 해시도 일치해야 한다. 파일 개수나 제품 버전만 같다는 이유로 파일 동일성을 판단하지 않는다. required check 설정이 이 수동 정책과 충돌하면 필요한 검사 항목만 조정하고 다른 보호를 유지한다. 권한/플랜 제한으로 적용할 수 없으면 정확한 검사명과 미적용 설정을 보고하며 보호 전체 해제·가짜 성공 check·우회 병합은 하지 않는다.

## Windows 11 설치형·포터블 검증 범위

다음 항목은 수행 절차이며 통과 기록이 아니다. 이번 검증의 실행 결과·SHA·한계는 별도 작업 기록에 남긴다. 빌드/합성 검사와 실제 Sandboxie·계정 검증을 구분한다.

- Windows 11 x64에서 `verify.ps1 -UiChecks`, BuildChecks, InstallerChecks를 순서대로 실행하고 각 종료 코드와 결과를 기록한다. InstallerChecks의 고정 Inno 컴파일 및 격리 AppId 설치·업데이트·제거는 합성 fixture의 실제 설치기 동작을 검사한다. 실제 사용자 설치본·시작 메뉴·계정 데이터를 시험 fixture로 바꾸지 않는다.
- `prepare-distributions.ps1`을 실행해 같은 publish의 두 배포물을 만든다. ZIP 전체 payload·버전·실행 파일 해시·manifest 소스 상태와 서명 표시를 확인하고 실패 시 이전 파일을 현재 성공으로 착각하지 않는지 BuildChecks로 확인한다.
- 생산 payload 설치·실행·업데이트·제거는 승인된 격리 Windows 11 환경에서 별도로 수행한다. 설치 경로·현재 사용자 앱 등록·설치형 바로가기·사용 중 중단·이전 버전 거부·데이터 보존을 확인하고 합성 fixture 결과와 분리해 기록한다. 테스트용 설치 회귀만 실행했다면 생산 payload 회귀를 미수행으로 표시한다.
- 포터블 ZIP을 고정된 시험 폴더에 전체 압축 해제하고 실제 EXE를 실행한다. GUI 시작·자체 설치/별도 .NET 설치 불필요·DLL 및 IsolationProbe 로드와 실행 위치를 확인한다. 읽기 전용 `--diagnose`/`--render`만 실행했다면 일반 GUI 실행으로 보고하지 않는다. Codex/MSIX 문맥에서 데이터 경로 보호로 차단된 진단을 일반 사용자 실행의 실패/성공으로 바꾸지 않는다.
- 설치형/포터블의 같은 사용자·세션 중복 실행이 먼저 열린 앱으로 전달되는지, 배포본을 전환하기 전 정상 종료가 필요한지 확인한다. 이동한 폴더에서 명시적으로 선택한 소유 바로가기만 갱신되는지, 수정/외부 링크와 미완료 기록이 보존되는지 격리 fixture로 확인한다. 실제 사용자 바로가기를 시험 목적으로 바꾸지 않는다.
- 검증 전후 `%LOCALAPPDATA%\SIDETALK`의 프로필·box·명령/복구 기록과 Sandboxie 설정을 읽기 전용으로 비교한다. 검증이 실제 계정 파일을 읽거나 인증/송수신을 수행하지 않았다면 관찰한 보존 범위만 기록한다. 실행 중인 앱이 독립적으로 데이터를 썼다면 원인 구분 없이 변경 없음으로 주장하지 않는다. 카카오톡 로그인·보조 계정 인증·송수신은 별도 사용자 승인하에 [ACCEPTANCE.md](ACCEPTANCE.md)를 따르며 합성 격리나 프로세스 존재로 대체하지 않는다.

시험 압축 해제·스테이징·화면 출력은 기본 위치 또는 고정 폴더 하나를 재사용하고 종료 후 이번 작업의 임시 출력만 정리한다. 사용자 프로필·box·기록과 `artifacts/vm-lab`은 정리 대상으로 삼지 않는다. 근거는 로그·요약·최소 파일로 남긴다.

# 잔여 프로세스 정리와 제한 복구 — 2026-10-07

시작 기준 main/origin/main은 `ce625661e94cd232e95135f5144b552e5a55b951`이며 별도 `codex/residual-box-recovery`에서 구현했다. 최종 `pwsh -NoProfile -File scripts/verify.ps1 -UiChecks`는 **117.00초, 종료 코드 0**이다. Release 앱·IsolationProbe 빌드 경고/오류 0, 콘솔 **324/324**(신규 52개), 새 잔여 WPF **6개**, 기존 관리자 권한 WPF **6개**·상호작용 **16개**·빠른 실행 **20개**·조용한 조회 **15개**가 통과했다. production 타이머 **24초·220표본·8회 조회**에서 표시·배치·포커스·스크롤 위반은 0이었다. 기본/최소 창의 실제 WPF 합성 화면과 비렌더 버튼 가용성을 확인했다.

첫 통합 실행의 새 진단 fixture는 정상 제한 값을 넣고 차단을 기대해 실패했다. 대상인 제한 누락으로 fixture를 고치고 정상 helper-only 정책의 기존 Stop 보존 검사를 별도로 추가했다. 관리 UI가 초기·정리 후·Apply 직전에 나타나는 세 경우 모두 종료·설정 쓰기 전에 차단하는 회귀까지 포함한 최종 전체 검사 결과다. 합성 계정/파일/레지스트리·프로필·동의·기록·호스트·다른 박스 sentinel 보존과 read-only/render/타이머 정리·설정 쓰기 0을 확인했다.

현재 사용자 환경의 읽기 전용 진단은 물리 프로필 경로 보호에 걸려 실제 box 상태가 미확인이다. 사용자 제공 4개 프로세스 진단과 합성 helper 4개 재현을 실제 환경의 종료 성공으로 보고하지 않는다. 현재 workspace의 vm-lab 및 확인한 VirtualBox CLI가 없어 승인된 별도 실제 Sandboxie terminate/SbieIni 통합 시험은 미수행이다. 실제 사용자 박스·설정·프로필·설치본을 변경하지 않았으며 BuildChecks·InstallerChecks·패키징·원격 CI dispatch·main 병합은 수행하지 않는다. [현재 동작·안전 조건·검사와 화면](RESIDUAL-BOX-RECOVERY.md)에 근거와 남은 범위를 기록했다.

# 관리자 권한 제한 진단과 승인 복구 — 2026-10-07

최종 `pwsh -NoProfile -File scripts/verify.ps1 -UiChecks`는 종료 코드 0으로 완료했다(98.44초). Release 앱·IsolationProbe 빌드 경고/오류 0, 콘솔 272/272, 새 관리자 권한 WPF 6개, 기존 상호작용 16개·빠른 실행 20개·조용한 조회 15개와 production 타이머 24초 관찰이 통과했다. 초기 통합 실행은 미검증 정책 조회의 기존 빠른 실행 범위 계약에서 실패했으며, 원래 차단 계약을 유지해 수정했다. 마지막 버전 미확인 처리와 UI 문구·캡처 수정 후 전체 통합 명령을 다시 실행했다. 기본/최소 창의 실제 WPF 합성 화면을 확인했고 최소 근거만 [ADMIN-RIGHTS-RECOVERY.md](ADMIN-RIGHTS-RECOVERY.md)에 보관했다.

현재 사용자 PC는 읽기 전용 진단만 실행했다. 호스트 카카오톡 26.8.1.5315·Sandboxie 1.18.4는 확인했지만 현재 Codex 문맥의 물리 프로필 경로 검증이 차단되어 실제 전용 박스의 DropAdminRights·프로세스·박스 사본·미완료 작업은 미확인이다. 보호 경계를 우회하지 않았다. 새 복구는 정확한 기존 전용 박스의 DropAdminRights=y에 새 명시적 승인을 요구하며, 이전 자동 동의로 적용하거나 앞으로의 자동 동의를 추가하지 않는다. 다른 정책 차이·실행 중·조회 실패는 차단한다.

실제 Sandboxie 제한 변경·SbieIni 복구·업데이트·계정 로그인/송수신은 별도 승인 환경에서 미수행이다. 실제 사용자 설정·설치본·vm-lab은 변경하지 않았고, 설치/빌드 스크립트 변경이 없어 BuildChecks·InstallerChecks·패키징은 실행하지 않았다. 원격 CI dispatch와 main 병합은 수행하지 않는다. 공식 안내를 통한 사용자의 호스트 업데이트 경로를 설명하며, 계정 보존을 입증하지 못한 박스 프로그램 사본 교체·동기화는 지원하지 않는다.

# 조용한 상태 확인과 메인 UI — 2026-10-07

이번 UI 변경의 현재 소스에서는 Release 앱·IsolationProbe 빌드(경고/오류 0), 콘솔 246/246, 기존 WPF 상호작용 16개·빠른 실행 20개·조용한 조회 15개 묶음이 통과했다. `verify.ps1 -UiChecks`의 WPF 관찰 단계는 초기 포커스 기준 문제로 실패했으며, harness를 수정한 뒤 **WPF 전체 명령을 재실행하여 종료 코드 0**을 확인했다. 실패한 통합 명령을 성공으로 보고하지 않는다.

production WPF 타이머의 24초·219표본·총 8회 조회(초기/준비 포함)에서 표시·배치·포커스·스크롤 위반은 0이었다. 6상태의 기본/최소 창과 100/125/150/200% 확대 비트맵을 확인했다. 원격 CI dispatch, 실제 모니터 DPI 전환, 화면 읽기 프로그램 음성 출력, 실제 계정/환경 설정 변경은 미수행이다. [동작·전후 화면·현재 결과와 제한](QUIET-STATUS-UI.md)에 이번 실행의 근거를 기록했다. 빌드·설치 스크립트 변경이 없어 BuildChecks와 InstallerChecks는 이번 범위에서 실행하지 않았다.

# 빠른 실행 · 사용자 바로가기 · 보조 창 식별 — 2026-10-05

시작 시 로컬 HEAD와 원격 main은 `0623c14a068cc5870bd4a1e50ed66727134157ea`로 같고 작업 트리는 깨끗했다. 기존 .NET 10 이전·주기적 점검 의도 접수·명령/복구 보호를 보존하며 `codex/secondary-quick-launch`에서 진행한다. 화면 정리는 환경 상세/설치 안내를 접고 복구·중단 흔적을 계속 표시하는 변경이며, 신규 기능은 별도의 실행 · 식별 탭·빠른 실행·사용자 링크·공식 표시 설정이다.

## 구현 범위와 보존 조건

- `--launch-secondary`는 작은 진행/취소 창과 동일 controller gate·CommandJournal을 사용한다. 확인 생략 동의는 현재 프로필/설치/격리 범위의 별도 기본 OFF 선택이다. 범위 변경은 기존 확인으로 돌아가고 실제 검증 실패·미완료 명령은 GUI 차단 이유로 이어진다. 같은 사용자/세션 broker는 기존 인스턴스에 전달하며 busy/중복 요청을 늦게 재실행하지 않는다.
- 명령 의도·실제 종료·새 box 관찰의 기존 확정 조건을 유지한다. 성공이 확정된 standalone 런처만 종료하며 timeout/불확실 결과는 같은 controller를 visible GUI에 유지한다. 이미 전용 프로세스가 있으면 새로 만들지 않고 상태를 보고한다. 외부 카카오톡 창 활성화는 구현하지 않았다.
- 바탕화면/시작 메뉴 링크와 별도 기본 OFF 로그인 Startup 선택은 현재 사용자 위치·정확한 `Sidetalk.exe`·고정 인자를 사용한다. manifest/고유 설명/해시/내용 검증으로 소유를 확인하고 사용자 수정·외부 충돌을 보존한다. 미완료 설정·복원 실패는 근거를 남기며 자동 실행을 차단한다. SYSTEM·관리자·작업 스케줄러와 실제 사용자 링크 변경은 시험에 사용하지 않았다.
- 표시 설정은 전용 box의 공식 BorderColor 색 부분과 선택한 정확한 BoxAlias `보조 계정`에 제한한다. 기존 BGR `#21685B,ttl,4`는 RGB `#5B6821`로 표시하며 모드·두께·선택하지 않은 별칭을 보존한다. 의도·백업·파일/API 재검사와 명시적 같은 선택 재시도를 기존 명령 잠금 아래 수행하고 전체 INI 롤백은 하지 않는다. SandMan 테두리·공식 SbieDll 제목 기능의 지원 범위만 사용하며 자체 overlay·주입·패치·메시지 읽기는 없다.

## 현재 수행한 독립 합성 검사

서로 다른 임시 소스/출력과 주입된 데이터 위치에서 focused 검사를 수행했다. 사용자 프로필·계정·box·INI·설치본·Startup/바로가기를 시험용으로 변경하지 않았다.

| 검사 | 실제 결과 | 범위 |
|---|---|---|
| Core 빠른 실행·동의 | 19/19 통과 | 정상/승인 없음·범위 변경·기존 실행·경쟁·취소·timeout/불확실 상태와 저장 이력 |
| 사용자 링크/Startup | 14/14 통과, 빌드 경고/오류 0 | 기본 OFF·읽기 전용·독립 선택·Unicode/공백 경로·이동/업데이트·수정/외부 링크 보존·경쟁·원본 복원/실패 보존 |
| 표시 설정 | 8/8 통과 | RGB/BGR·기존 선택·전용 범위·다른 설정 보존·부분 실패·명시적 재시도·공유 잠금 |
| 신규 WPF 빠른 실행·설정·broker | 20/20 동기화 사례 통과 | 승인/취소·실패 후 GUI·경쟁·초기화 factory 실패·잘린 요청·지연된 수신 ACK와 전달 보존 |

Shell Link roundtrip은 실제 Windows `IShellLinkW`/`IPersistFile` COM을 사용했으나 모든 링크 위치는 일회용 fixture였다. 표시의 외부 경계와 빠른 실행은 합성이며 실제 계정 실행이나 Sandboxie 테두리 표시 성공을 의미하지 않는다. 공식 지원 근거와 안전한 합성 재현 명령은 [QUICK-LAUNCH.md](QUICK-LAUNCH.md)에 있다.

WPF fixture의 Application은 production Startup 이벤트를 분리하며 실제 초기 실행 경로에 들어가지 않는다. 새 factory 실패도 production controller를 만들지 않는 주입으로 재현했다. broker는 초기화를 한 번만 시도하고 비동기 준비/수신 확인을 기다리며, 초기화 실패를 표시하고 잘린·잘못된 송신자를 끊는다. 수신 ACK를 기다려 준비/전달 사이의 요청을 잃지 않는 것도 명시적 동기화로 확인했다.

최초 제한 도구 실행은 `CurrentUserOnly` named-pipe 클라이언트의 `ConnectAsync`를 접근 거부해 실패했다. ACL이나 제품 권한 조건을 완화하지 않았다. 같은 `--quick-launch-only` fixture를 정상 호스트 도구 실행 권한으로 다시 실행해 20/20과 PNG 6개 생성을 **3.49초**에 확인했다. 이 도구 실행 권한 구분을 앱의 관리자 권한 기능이나 실제 로그인 Startup 검증으로 취급하지 않는다. 기본/최소 크기 설정 탭·스크롤한 식별 영역·런처·실패 후 GUI의 합성 PNG **6개**를 직접 확인했다.

## 최종 통합 검사와 화면

`pwsh -NoProfile -File scripts/verify.ps1 -UiChecks`는 정상 호스트 도구 실행에서 **종료 코드 0, 54.68초**로 완료했다. Release 앱·IsolationProbe 빌드는 경고/오류 0, 자체 콘솔 **241/241**, 기존 fail-closed 검사와 기존 16개+신규 20개인 **36개 동기화 WPF 시나리오**가 모두 통과했다. 합성 PNG **22개**를 새 임시 폴더에 생성했다. 좁은 focused 결과로 기존 검사를 대체하거나 테스트를 skip하지 않았다.

수정 전 합성 화면은 Git에서 제외되는 `artifacts/verification/quick-launch-20261005/before/`에 보존했다. 최초 통합 검사의 PNG 출력은 `C:\Users\froze\AppData\Local\Temp\sidetalk-ui-checks-cb38693b398b4c6b82626f2e80f7da61`이며 수정 후 PNG/JSON의 보존 위치는 `artifacts/verification/quick-launch-20261005/after/`다. 같은 기준 fixture의 `before/ui-recovery-healthy.png`와 `after/ui-recovery-healthy-fixture.png`를 직접 비교해 환경 상세가 접힌 상태에서도 현재 정책 상태가 계속 보이는 것을 확인했다.

최종 화면 비교에서 어두운 native Expander 제목을 발견해 XAML 전경색만 `InkBrush`로 보정했다. 이후 UiChecks를 좁게 다시 빌드해 경고/오류 0을 확인하고 전체 WPF 검사 **16+20개, PNG 22개**를 다시 생성·직접 확인했다. 이 화면 변경으로 Core 콘솔 검사를 중복 실행하지 않았다. 해당 빌드/검사 로그는 `ui-contrast-build.log`·`ui-contrast-checks.log`, 최종 원래 출력 위치는 `ui-final-output.txt`에 기록했으며 `after/` 이미지는 이 결과로 갱신했다. 로컬 화면 비교는 완료했고 현재 커밋의 Windows CI 결과는 아직 확인 전이다. 과거 WPF/CI 통과 기록을 이번 결과로 재사용하지 않는다.

빌드/검증/패키징 스크립트는 이번 작업에서 변경하지 않아 로컬 BuildChecks 전체를 중복 실행하지 않았다. 기존 Windows CI의 19개 모의 packaging fixture는 유지하며 현재 PR 실행 결과는 아직 확인 전이다. 실제 publish나 사용자 배포 교체는 하지 않았다.

실제 Windows 로그인 세션 Startup, 일반 사용자 링크 클릭에서 계정 실행, Sandboxie 1.18.4 테두리·별칭/사용자 정의 제목줄, 실계정 로그인·송수신·외부 창 행동 및 실기기 성능은 미검증이다. 운영 릴리스·publish·사용자 배포 교체·유료 러너/구독 설정 변경은 수행하지 않는다. 아래의 점검 UX 및 .NET 10 결과는 각각 이전 작업의 역사적 기록이다.

# 주기적 상태 점검 중 요청 접수 UX — 2026-10-05

시작 시 로컬 HEAD와 원격 main은 검토 기준 `4e79d557b3c5bc6df829f6777c353ae9ad800db5`로 같고 작업 트리는 깨끗했다. 실행의 라임색 Primary와 강제 종료의 Secondary를 유지하며 상태 점검·명시적 요청 접수·최소화/복원의 상호작용만 변경한다. 프로필·전용 저장소·명령/복구 기록의 위치와 형식 및 Core의 검증·잠금 조건은 유지한다.

정상 읽기 점검 중 마지막 성공 관찰은 버튼의 요청 접수 가능 여부에만 쓰인다. 접수 즉시 안내·취소를 제공하고 진행 중인 점검 → 기존 확인창 → 새 상태 검사 → 기존 Core gate와 CommandJournal 잠금 아래 실행 직전 검증을 거친다. 실패·오래된 관찰은 실행 권한이 아니며 이중 클릭·취소·최소화·닫힘 뒤 과거 요청을 재실행하지 않는다. 이미 보낸 명령·복구 동안에는 닫힘을 미룬다. 늦은 응답은 세대가 바뀐 화면 상태를 덮어쓰지 않는다.

최소화 중에도 미완료 명령 재확인과 승인된 자동 복구를 위한 4초 Core 점검을 유지하고 정상 검사로 받은 상태·복구 계획·중단 흔적의 시각 갱신을 미룬다. 오류·복구 진행 안내는 계속 갱신한다. 복원 시 새 검사 전에는 요청을 받지 않는다. 이 변경은 Core 호출 감소나 실기기 성능 개선의 실측 결과가 아니다.

## 수정 전 실측과 이번 검증 범위

검토 HEAD의 독립 임시 소스에서 지연 fake controller와 TaskCompletionSource로 점검 응답을 명시적으로 보류·해제해 측정했다. sleep이나 실제 KakaoTalk/Sandboxie 사용자 환경은 사용하지 않았다.

| 합성 관찰 | 수정 전 `4e79d55` 실측 | 수정 후 |
|---|---|---|
| 정상 점검 1회의 실행 버튼 IsEnabled 전환 | 2회 | 0회 |
| 정상 점검 1회의 종료 버튼 IsEnabled 전환 | 2회 | 0회 |
| 보류 중인 정상 점검에서 클릭한 실행의 명령 접수 | 0회 | 점검·확인창 1회·새 검사 후 1회 |
| 최소화 중 점검 1회의 CheckedAt 시각 갱신 | 1회 | 0회 |

`pwsh -NoProfile -File scripts/verify.ps1 -UiChecks`에서 Release 앱·IsolationProbe 빌드(경고/오류 0개)와 기존 콘솔 안전성 **200/200**은 통과했다. 마지막 UiChecks의 기본 `dotnet run` 빌드가 컴파일 진단 없이 종료 코드 1을 반환해 스크립트는 단계 코드 **13**으로 실패했다. 해당 단계만 `dotnet build tests/Sidetalk.UiChecks/Sidetalk.UiChecks.csproj -c Release --nologo --disable-build-servers -m:1 -nr:false -p:BuildInParallel=false`로 빌드해 경고/오류 0개를 확인하고, `dotnet run --project tests/Sidetalk.UiChecks/Sidetalk.UiChecks.csproj -c Release --no-build -- <새 임시 출력 폴더>`로 전체 WPF 검사를 실행했다. 기존 fail-closed 검사와 **16개 동기화 시나리오, 합성 PNG 16개**가 통과했다. 기본·최소 창 크기의 요청 안내·취소 버튼 PNG도 직접 확인했다. 통과한 콘솔 검사는 반복하지 않았다.

상호작용 검사는 점검 중 클릭과 실제 routed 이중 클릭, 반환/예외 형태의 점검 실패, 확인 후 재검증 실패·차단, 취소·닫힘·늦은 응답, 명령 실패 후 재실행 방지, 수동/자동 복구 경쟁, 최소화/복원, 파일 선택창 내부의 중첩 호출과 취소를 포함한다. 파일 선택창도 열기 전에 작업을 예약하므로 중첩 메시지 처리 중 자동 복구나 다른 요청이 끼어들 수 없다. 순서는 TaskCompletionSource로 고정하며 30초 watchdog은 교착 검출 용도로만 사용한다.

`pwsh -NoProfile -File tests/Sidetalk.BuildChecks/Program.ps1`도 **19개 임시 저장소의 모의 검사 전체 통과**다. 이 검사는 실제 publish·배포 교체·앱 실행을 하지 않는다.

수정 전 로그와 임시 harness는 Git에서 제외되는 `artifacts/interaction-baseline-4e79d55/`, 수정 후 로그·측정 JSON은 `artifacts/verification/inspection-ux-20261005/`에 있다. Windows CI의 최종 커밋 결과는 PR의 검증 기록을 따른다. 기존 fail-closed 회귀를 삭제·완화하지 않았다. 이번에는 실제 사용자 데이터·KakaoTalk·Sandboxie 환경을 시험하지 않았으며, Windows 11 + Sandboxie 통합·계정 로그인/송수신·실기기 성능은 미검증이다. 새 릴리스·publish·배포 교체는 수행하지 않았다. 아래 .NET 10 이전 결과는 해당 작업의 역사적 기록이다.

# .NET 10 LTS 이전 — 2026-10-05

시작 시 로컬 HEAD와 원격 main은 `81477439bd735bd440446bc2b3fcab163159f503`으로 같고 작업 트리는 깨끗했다. `codex/dotnet10-lts`에서 5개 프로젝트를 `net10.0-windows`, SDK를 안정 버전 `10.0.401`로 이전했다. 실제 로컬 .NET/WindowsDesktop 런타임은 `10.0.12`다. 공식 호환성 근거와 재현 명령은 [NET10-MIGRATION.md](NET10-MIGRATION.md)에 있다. 기존 CI는 이미 `global.json` 기반이므로 workflow를 중복 변경하지 않았다.

## 데이터·명령·복구 호환성

- 이전 HEAD의 실제 Core 모델을 SDK 8.0.424 / 런타임 8.0.30의 독립 임시 빌드에서 직렬화한 고정 fixture를 .NET 10으로 읽었다. 프로필 schema 1, 명령 저널 schema 1/2와 복구 이벤트가 호환되며, 원본 바이트·소유 식별자·저장 경로·합성 계정 표식·중단 파일을 보존했다. 기존 JSON/경로/enum 및 AppState 초기화 변경은 없다. 별도 영속 AppState 파일은 구현에 없으며 검사 결과로 상태를 계산한다.
- 명령 중복·동시 실행 차단, timeout 미완료, 실패 원인 보존, PID/생성 시각/경로 검증, 실제 종료 후 새 box 조회, Completed 재조정과 실패 후 차단 회귀를 모두 유지했다. 기존 Completed 결함 수정은 다시 변경하지 않았다.
- 제품 소스의 동작 변경은 Authenticode 인증서 추출의 .NET 9/10 폐기 API 대응뿐이다. `WinVerifyTrust`와 배포자 검증을 유지하고 Authenticode 형식 제한 후 해당 API 한 줄의 경고만 억제한다. 서명된 현재 런타임의 임시 복사본 허용, 다른 배포자·변조본·인증서/PFX 위장 거부를 실제 Windows API로 확인했다. 설치된 원본은 읽기만 했다.
- 실제 카카오톡 계정·Sandboxie box·사용자 INI·설치본·프로필 데이터는 시험용으로 읽거나 변경하지 않았다. 프로세스 강제 종료, 설치, 운영 배포 교체나 릴리스는 수행하지 않았다.

## 수행한 로컬 검사

- `pwsh -NoProfile -File scripts/verify.ps1 -UiChecks`: 앱·IsolationProbe Release 빌드 경고/오류 0, 콘솔 **200/200** 통과. WPF 단계는 로컬 출력 디렉터리 접근 거부로 종료 코드 13이었다. 제한 실행과 일반 실행 권한의 두 로그를 보존했다. 전체 검사를 성공으로 바꿔 표시하거나 테스트를 삭제/skip하지 않았다.
- 출력 경로를 확인·생성하고 아래 **실패한 UI 단계만** 빌드 서버 재사용 없이 단일 MSBuild로 재실행했다. 빌드 경고/오류 0, 합성 WPF **14개 fixture**와 권한·상태·아이콘·진단 검사 성공(종료 코드 0). 승인/작은 창 PNG도 육안 확인했다. 기존 프로세스 종료나 ACL 완화는 없었다.

```powershell
dotnet build tests/Sidetalk.UiChecks/Sidetalk.UiChecks.csproj -c Release --nologo --disable-build-servers -m:1 -nr:false -p:BuildInParallel=false
$uiOutput = Join-Path ([IO.Path]::GetTempPath()) ('sidetalk-ui-checks-' + [Guid]::NewGuid().ToString('N'))
dotnet tests/Sidetalk.UiChecks/bin/Release/net10.0-windows/Sidetalk.UiChecks.dll $uiOutput
```

- `pwsh -NoProfile -File tests/Sidetalk.BuildChecks/Program.ps1`: **9개 검사 그룹 / 19개 임시 저장소 fixture 성공**(83.79초). 두 배포 모드, 실패 전파, 실행 중 출력/잠금/reparse 보호, 교체 실패 롤백과 실패 시 원본 보존을 검사했다. CMD preflight도 복사한 `global.json`으로 SDK 10.0.401을 선택했다.
- 공백·한글을 포함한 새 임시 소스 복사본에서 앱과 Probe를 각각 framework-dependent/self-contained `win-x64`로 실제 publish했다. 두 모드의 x64 PE, `net10.0` runtimeconfig, 런타임 포함/비포함과 WPF 네이티브 DLL, README/docs 및 금지 파일 부재를 검사하고 ZIP·SHA-256을 생성했다. framework-dependent ZIP은 **25개 항목**, self-contained ZIP은 **607개 항목**이며 번들 런타임은 **10.0.12**다. 실제 `build.ps1` 배포 승격은 실행하지 않고 그 보호/롤백은 위 모의 회귀로 검증했다. 패키지 앱과 Probe를 실제 사용자 설정에 연결해 실행하지 않았다.
- ZIP SHA-256: framework-dependent `4574D81E1BB7D71830E74FBFEE8647FB288935ADA0FB8AFEBC6939D039FE25EF`, self-contained `AC6642A10D63F7CED2909CCFB43A1A15AB6B5DD4E0FEC6A9DE651F823AAE166F`. 패키지는 결과 문서 갱신 전 소스 복사본에서 생성했으며 운영 릴리스가 아니다.
- 근거 로그/임시 경로는 Git에서 제외되는 `artifacts/verification/net10-20261005/`에 보존한다. 기존 .NET 8 결과는 아래에 역사적 기록으로 남긴다.

## 실제 Windows 11 + Sandboxie 통합의 남은 항목

기존 `SIDETALK-LAB-20261004`는 등록되어 있으나 읽기 전용 `list runningvms`는 빈 결과였고 `showvminfo`는 공유 세션 잠금 오류 `E_FAIL / VBOX_E_VM_ERROR`로 실패했다. 이전 문서에서 확인되는 게스트 런타임은 8.0.30이며 현재 .NET 10 통합 시험에 사용할 수 있는 상태는 확인하지 못했다. VM 시작/reset·게스트 설정 변경을 하지 않았다.

일회용 Windows 11 환경을 준비한 뒤 .NET 10 self-contained 패키지 또는 Desktop Runtime 10 x64로 [실제 복구 시험](WINDOWS-RECOVERY-TEST.md)을 수행해야 한다. 실제 서명된 Sandboxie 1.18.4 DLL/API·SbieIni 파일/API 재조회·실패 후 재시작·Completed 재조정·일반 사용자 GUI의 단일 실행과 [계정 없는 격리 시험](ISOLATION-PROBE.md)이 남아 있다. 실제 계정 로그인·동시 송수신·알림·데이터 영속성은 별도 승인과 [수용 검사](ACCEPTANCE.md)가 필요하다. 로컬 합성 WPF와 hosted Windows Server CI를 이 결과로 대체하지 않는다.

# 보존된 Completed의 재조정 후 반복 조회 수정 — 2026-10-04

실제 원격 `origin/main`, 로컬 HEAD와 리뷰 기준은 모두 `ee19cd4ae6587f8db0cb7b92e265bfde88f42d2d`였고 작업 트리는 깨끗했다. 별도 `codex/completed-evidence-reconciliation`에서 이번 결함과 직접 관련된 생산 코드·회귀·opt-in 시험 도구만 수정했다. main 병합·릴리스·호스트 배포본 교체는 하지 않는다. 이전 검증 결과와 이번 결과를 구분한다.

## 수정 전 재현과 완료 근거

`Setup preserved completed event without failure survives fresh reconciliation`은 정상 프로필과 가짜 외부 경계에서 실제 생산 코드의 설정 변경·검증·파일 저널을 사용한다. 작업 A의 정상 Started·BackupCreated·ChangeIntent·ChangeVerified 뒤 **완전한 Completed JSON 임시 파일만 남기며 최종 Completed와 Failed는 없다**. 원본 보존 후 새 작업 B의 실제 재검증·Completed와 A의 B 참조 Reconciled까지 완료한 뒤 다시 조회한다.

수정 전 자체 콘솔 실행은 **167/168 통과**였고 이 테스트만 `보존 이벤트의 현재 체인과 원래 위치를 확인할 수 없습니다.`로 실패했다. 보존된 Completed를 현재 A의 Reconciled 뒤 검사를 위한 체인에 합치면서 기존 순서 검사에 걸린 것이다. 재현 로그는 Git에서 제외되는 `artifacts/verification/completed-evidence-20261004/before-reproduction-actual.log`에 보존했다. Started 임시 기록·잘린 변경 기록이나 가짜 Failed로 대체하지 않았다.

과거 Completed는 원래 파일 이름·작업 ID·순서·승인 PlanId/ScopeFingerprint와 그 시점까지의 연속적인 검증 기록으로 원본 유효성을 확인한다. 보존 원본의 바이트·SHA-256·길이·메타데이터·경로 검사는 유지한다. 이어서 별도로 **실제 게시된 모든 작업 체인과 Reconciled의 다른 유효한 완료 작업 참조**를 기존 `ReadInterruptedOperation`/`ValidateOperation`으로 검사한다. 과거 Completed를 현재 체인에 삽입하거나 최종 파일로 재게시하지 않으며, 원본 보존만으로 완료되지 않는다. 순서 검사를 완화하거나 오류를 삼켜 Ready로 처리하지 않는다. 올바른 B의 Completed→ConsentFailed와 기존 Failed가 있는 A의 재조정도 유지한다.

## 이번 소스의 회귀와 읽기 전용 결과

- `pwsh -NoProfile -File scripts/verify.ps1 -UiChecks`: **성공**, 자체 콘솔 **193/193**, Release 앱·IsolationProbe 빌드 경고/오류 0, 합성 WPF **14개 fixture** 검사 통과(20.08초). 이전 167/167 결과를 재사용하지 않았다. 빌드 스크립트는 변경하지 않았으며 기존 CI의 모의 패키징 회귀는 유지한다.
- 원래 실패 재현과 새 컨트롤러 회귀가 통과했다. `InspectAsync`·`InspectInterruptedEvidenceAsync`·진단 내보내기를 새 읽기/쓰기 컨트롤러 각각 세 번 수행해 **`CanLaunch=true`**, 원본·메타데이터·기존 저널 불변, 과거 Completed 재게시 0, 반복 조회에 따른 보존본·작업·백업 증가 0을 확인했다. 진단 출력은 검사 대상 폴더 밖에 저장했다.
- 원본 보존 뒤, B 완료 전, B 완료 뒤 A Reconciled 게시 전의 재시작에서는 차단을 유지하고 새 승인 경로로 이어졌다. 원본/해시/길이·작업 ID·순서·파일 이름·경로·승인 정보 변조, 없는/자기/미완료/실패 작업 참조, PlanId/ScopeFingerprint 불일치, 고아 Completed·중복 순서·미검증 변경을 독립 사례로 거부했다. 올바른 B의 선택적 ConsentFailed와 실제 게시 실패로 Failed가 있는 A도 통과했다.
- 보존 전과 재조정 후 읽기 전용 컨트롤러의 설정 쓰기·관리 프로세스 종료·원본 이동·이벤트 게시·추가 완료 기록 생성은 **0회**였다. 바이트·파일 목록과 재시작 fixture의 수정 시각을 비교했다. 합성 렌더 권한 검사와 아래 실제 VM의 앱 utility 모드를 구분한다.

추가 테스트의 가짜 환경은 같은 저장 경로의 새 ProfileStore 인스턴스를 허용하도록 바로잡았고, 보존된 항목 자체가 실행을 막지 않아도 미완료 A가 남으면 전체 검사가 차단한다는 기대값을 수정했다. 제한 실행 환경에서는 콘솔 193/193 뒤 WPF 단계가 컴파일 진단 없이 실패했다. 의존성을 확인한 후 정상 실행 권한으로 위 최종 통합 명령을 완료했다. 제품 검사를 완화하지 않았다. 최종 로그는 Git에서 제외되는 `artifacts/verification/completed-evidence-20261004/final-verify-normal-permission.log`에 있다.

## 기존 승인 Windows VM의 이번 실제 중단 시험

기존의 같은 전용 Windows 11 **10.0.26300.9457** VM과 공식 Sandboxie Plus **1.18.4**를 사용했다. 새 설치·VM 생성·호스트 재부팅·호스트 배포 교체는 하지 않았다. 최초 게스트 연결/heartbeat 장애와 정상 ACPI 종료 무응답 때문에 시험 시작 전에 해당 시험 VM만 reset했고, 이후 정확한 머신·VirtualBox·승인 표식·표준 `sidetest` 사용자·서비스와 보존 기준을 다시 확인했다. 이 환경 복구는 제품 시험 성공으로 계산하지 않는다.

`seed-optional → crash-completed → reconcile-completed → inspect-completed`를 수행했다. 현재 사용자 재발 방지 옵션의 실제 공식 SbieIni 변경·파일/API 검증 뒤 생산 코드의 Completed JSON 직렬화와 **Flush(true)**가 끝나고 덮어쓰기 없는 최종 게시 직전에 시험 프로세스만 **예상 코드 77**로 중단했다. **완전한 Completed 임시 JSON, 최종 Completed 없음, Failed 0개**와 연속적인 정상 A 기록을 확인했다. 중단 시점은 시험 hook의 주입이며 자연 장애·전원 장애 실측은 아니다. 이 hook는 일반 앱 기능에 노출하지 않는다.

새 프로세스에서 정확한 original.bin·메타데이터를 보존하고, 보존만으로 완료되지 않는 것을 확인한 뒤 새 인스턴스의 재검증 B를 완료하여 A→B Reconciled 참조를 확인했다. **새 작업·백업은 각 1개**, 과거 Completed 재게시·가짜 Failed 추가는 **0개**다. 이어 별도 `inspect-completed` 프로세스 **3회**, 매번 새 읽기 전용 컨트롤러 **3개**로 진단·상태·보존 근거 조회를 수행했다. 모두 **CanLaunch=true**, 원본·메타데이터·기존 정상 기록·프로필·소유 정보·파일/HKCU sentinel·무관한 Sandboxie 설정 보존, 반복 작업·백업 증가 0을 확인했다.

현재 소스로 빌드한 실제 앱의 `--diagnose`, `--render`, `--render-setup`, `--render-small`도 **보존 전과 재조정 후 각각 실행**했다. 모든 utility 모드가 종료 코드 0으로 출력했으며 검사 대상 파일 목록·원본·프로필·저널·보존본·INI 해시가 변하지 않았다. 탐색기 재실행/GUI 승인 클릭은 이번에 수행하지 않았다. 카카오톡 실행·로그인·송수신·계정 데이터 접근은 0회이며 실제 box 레지스트리 hive·다른 box 데이터·전원 장애 시험으로 확대하지 않는다. VM 보고서·중단/재조정 불변 witness와 실제 앱 출력은 Git에서 제외되는 `artifacts/vm-lab/collected-completed`에 보존하고 CI artifact에 포함하지 않는다.

## 이번 Windows CI

기존 Windows workflow를 그대로 유지하며 현재 PR 커밋의 실행 상태와 로그를 확인한다. [이번 작업 브랜치의 Actions](https://github.com/frozenvoice/sidetalk/actions?query=branch%3Acodex%2Fcompleted-evidence-reconciliation)는 이번 회귀·합성 WPF·모의 빌드 보호 검사를 실행한다. hosted Windows CI와 위 실제 Sandboxie VM 결과는 별도 근거다.

# 중단 복구·실행 권한 분리 검증 — 2026-10-04

시작 시 실제 원격 `origin/main`, 로컬 main과 리뷰 기준은 모두 `e906cfe78f3ebc8601f4769434aae10f15ed8007`이었다. 작업 트리는 깨끗했으며 별도 `codex/recovery-reconciliation`에서 수정했다. main 병합·릴리스·기존 배포본 교체는 하지 않았다. 아래 결과는 이번 소스로 실행한 결과이며 과거 사용자 성공 보고를 재사용하지 않는다.

## 변경 동작과 보존 경계

현재 정책·공식 설치·일반 사용자 컨텍스트·전용 소유 정보/경로·명령 상태를 새로 확인하여 실행 권한을 계산한다. 선택적 재발 방지 옵션, 자동 복구 동의, 다른 세션 관리 UI와 복구 전용 조회 실패는 그 자체로 정상 실행을 차단하지 않는다. 실제 정책 충돌·소유 불일치·미완료 명령·결과가 불명확한 설정 변경은 계속 차단한다. 공유 INI 복구에는 다른 사용자/세션 및 조회 불가 관리 프로세스 검사를 유지한다.

앱의 **원본을 보존하고 다시 확인**은 설정 복구 권한과 별도로 조건을 확인한다. 원본 바이트와 SHA-256, 원래/보존 위치, 관련 작업을 `recovery-evidence`에 덮어쓰지 않고 저장한다. 보존 파일도 경로·해시·구조를 검증하며 unknown/충돌 근거는 계속 차단한다. 중단한 최초 프로필은 같은 승인 후보를 유지한다. 검증 완료 후 동의 저장만 실패하면 실행을 막지 않고 안내하며, 철회 저장 흔적을 보존해도 과거 자동 동의가 되살아나지 않는다.

명령 저널 schema 2는 사용자·세션 및 머신 DPAPI로 보호한 네이티브 uptime 근거를 선택적으로 기록한다. schema 1은 값을 추측해 채우지 않고 호환한다. PID 부재·재사용·실제 종료·신원 미확인을 구분하며 접근 거부와 종료 코드 미확인을 성공으로 만들지 않는다. 이전 부팅 증명은 현재 uptime이 인증된 기록값보다 작은 경우에 한정된다. [Windows API 의미와 제한](windows-command-recovery-api.md)을 참고한다.

## 이번 로컬 검증

- `pwsh -NoProfile -File scripts/verify.ps1 -UiChecks`: **성공**, Release 앱·IsolationProbe 빌드 경고/오류 0, 자체 콘솔 회귀 **167/167**, 합성 WPF 검사와 **14개 fixture** 생성. 최종 소요 17.69초.
- `pwsh -NoProfile -File tests/Sidetalk.BuildChecks/Program.ps1`: **9개 검사 그룹 / 19개 임시 저장소 fixture 성공**. 실제 publish나 앱 실행 없이 실패 전파·패키징 보호·롤백을 검사했다.
- 콘솔의 실제 Windows API 검사: 현재 토큰/세션·생성 시각·이미지·네이티브 uptime·머신 DPAPI와 변조 거부, 부재 PID, PID 4 접근 거부, 무해한 실제 자식의 종료 코드 17, 다른 경로의 동명 Start 및 관련 후보 구분. 로컬 이전 부팅 분기는 합성 검사다. 다른 사용자/세션의 실제 관리 UI와 실제 VM 재부팅에 따른 명령 복구는 아래 VM에서 별도로 확인했다. PID 재사용 접근 거부의 실제 실측으로 확대하지 않는다.
- 합성 회귀: 선택적 준비와 실행 권한 분리, 다른 세션 복구 차단/다른 사용자 종료 0회, 실제 정책/소유 불일치 차단, 잘린·완전한·충돌·출처 불명 기록, 같은 프로필 후보, 보존 중 재시작·경로 이탈 거부·변경 경합, 실패/취소 원인 보존, 읽기 전용 무쓰기, 새 결과로만 UI 권한 복원과 선택적 동의 실패 안내 유지.
- 최종 독립 리뷰에서 원본 해시 확인과 경로 기반 이동 사이의 외부 파일 교체 경합을 발견했다. 배타적 읽기/DELETE 핸들로 동일 파일을 이름 변경하며 기존 보존본을 덮어쓰지 않도록 수정했다. 실제 Windows 파일 API 경합·중단·보존본 충돌 검사 3개를 추가한 후 위 통합 검사를 다시 수행했다.

초기 WPF restore는 제한 실행 환경에서 진단 없는 MSBuild 실패를 반환했다. 일반 실행 권한으로 restore 후 위 최종 통합 검사는 통과했다. 이 환경 오류를 성공으로 계산하거나 제품 검사를 완화하지 않았다. 로컬 근거는 `artifacts/verification/recovery-20261004/race-fixed-verify.log`, `build-checks.log`와 로그에 표시된 새 임시 WPF 폴더다. 개인 환경·시험 VM 자료는 Git/CI artifact에 포함하지 않는다.

## 별도 Windows 시험 환경

사용자의 추가 설치 승인으로 VirtualBox 7.2.20과 공식 Windows 11 Enterprise 평가판 VM을 설치했다. 게스트 실제 버전은 **10.0.26300.9457 x64**이며 호스트와 사용자 폴더·클립보드를 공유하지 않는다. 공식 Sandboxie Plus **1.18.4**의 사용자 구성 요소 **5.73.4**, 드라이버 **5.73.2**, .NET Desktop Runtime 8.0.30과 공식 카카오톡을 설치하고 서명·검토 기준을 확인했다. 복구 시험은 `sidetest` 표준 권한으로 수행했다. 호스트의 정상 Sandboxie 설정이나 카카오톡 계정은 실패 fixture로 사용하지 않았다. 실행 절차와 opt-in 도구는 [WINDOWS-RECOVERY-TEST.md](WINDOWS-RECOVERY-TEST.md)에 있다.

다음은 합성 외부 경계가 아닌 실제 Windows·Sandboxie 파일/API·공식 SbieIni 경로에서 확인한 결과다. 중단 시점 주입은 각 항목에 구분했다.

- 최초 준비에서 `crash-profile`은 실제 승인 후보의 프로필 임시 파일 저장 뒤 게시 직전에 예상 종료 코드 **77**로 중단했다. 이후 읽기 전용 `--diagnose`, `--render`, `--render-setup`, `--render-small`을 수행하는 동안 원본·프로필·INI 해시가 변하지 않았다. `reconcile`은 원본 **1개**를 보존하고 **같은 승인 후보**로 최초 설정을 완료했다.
- `seed-six`와 `seed-subset`으로 재발시킨 여섯 템플릿 및 부분 집합을 승인 범위의 `repair-auto`로 복구했다. 실제 공식 SbieIni 변경 뒤 파일과 API의 목표 값을 대조했다. 이미 정상인 상태의 자동 재조회에서는 추가 백업이 **0개**였다.
- 현재 사용자 `SbieCtrl_AutoRunSoftCompat=y`만 남은 상태에서 실제 실행 판단은 **`CanLaunch=true`**였다. 이후 승인 복구로 **`n`** 적용을 확인했다.
- `management-ui`는 승인한 **같은 사용자·세션의 SandMan**만 정확한 신원으로 종료하고 복구했다. `seed-six → crash-change → reconcile`도 성공했다. `crash-change`는 실제 SbieIni 변경과 검증 직후에 종료 코드 **77**을 주입한 시험이며, 자연 발생한 앱 장애로 취급하지 않는다.
- 다른 사용자 `sidelab`, 세션 **0**의 SandMan **PID 8012**는 처음에 표준 `sidetest`에서 신원을 조회할 수 없었다. 복구는 **Blocked**였고 실제 앱의 `--diagnose`는 **Ready / `CanLaunch=true`**였다. 조회 실패를 다른 사용자 신원 확인 성공으로 계산하지 않았다.
- 대상 소유자가 정확한 PID·실행 경로·생성 시각·SID·세션을 확인한 뒤 시험 fixture의 DACL에서 **`PROCESS_QUERY_LIMITED_INFORMATION` / `TOKEN_QUERY` 읽기 권한만** 임시로 부여했다. 실제 `inspect-foreign`은 **Foreign 1 / Unknown 0 / Blocked**였으며 원래 PID가 살아 있음을 확인했다. 원래 ACL 복원도 성공했다. 제품의 신원 검사나 종료 권한을 완화한 결과가 아니다.
- 처음 Task Scheduler의 다른 세션 fixture 실행 실패와 이후 신원 조회 불가로 인한 `inspect-foreign` 실패 보고서는 별도로 보존했으며 성공 결과에 포함하지 않았다.
- 다른 사용자 SandMan의 정상 VM 재부팅 종료 뒤 `DefaultBox`와 그 사용자의 `AutoStartAgent`/`BoxGrouping`이 INI에 추가됐다. 기존 불변 기준의 기타 설정 해시 비교는 실패했고 `post-foreign-repair`는 변경 전에 중단됐다. 해당 실패·원래 기준·새 설정을 보존한 채 별도 외부 변경 checkpoint로 후속 시험 구간을 나눴다. 이 checkpoint는 복구 성공으로 계산하지 않으며, 이후에는 현재 사용자 옵션만 변경하고 새로 생긴 다른 box와 사용자 설정의 보존도 비교한다.
- 표준 사용자 컨텍스트에서 `run-local.cmd`를 **CLI로 실행**하여 실제 GUI의 **Ready 상태와 버튼 활성화**를 관찰했다. 탐색기 더블클릭과 GUI 승인 버튼 클릭은 미수행이다. 키 입력 전달 문제 때문에 GUI 승인 흐름을 시험한 것으로 계산하지 않는다.

보존 sentinel의 실제 범위는 **profile.json 바이트·소유 정보, 전용 box의 일반 파일, 무관한 HKCU 시험 값, 무관한 Sandboxie 설정**이다. 실제 box 레지스트리 hive, 다른 box의 데이터, 계정 데이터 보존 검증으로 확대하지 않는다. 카카오톡 계정 로그인·송수신·계정 데이터 접근은 **0회**다. 개인 환경 정보가 있는 최종 원본 보고서와 두 불변 기준은 Git에서 제외되는 `artifacts/vm-lab/collected-final`에 보존하며 CI artifact에 넣지 않는다.

`seed-optional → crash-record → crash-preserve → reconcile`도 실제 VM에서 성공했다. 실제 SbieIni 변경을 검증한 뒤 잘린 이벤트 바이트를 주입해 종료 코드 77로 중단했고, 다음 프로세스는 불변 보존 메타데이터 게시와 원본의 동일 핸들 이동 사이에서 다시 77로 중단했다. 최종 재조정은 누적 원본 **2개**, **같은 승인 후보**, 실제 INI/API 일치와 sentinel 보존을 확인했다. 부분 바이트와 중단 위치는 시험 도구가 주입했으며 실제 전원 장애 전체 조건의 검증으로 확대하지 않는다. VM 연결 서비스가 멈춰 정상 종료에 반응하지 않은 별도 환경 장애는 보고서를 보존한 뒤 VM reset으로 복구했으며 제품 복구 성공에 포함하지 않았다.

실제 PID 없는 명령 intent 저장 뒤 정상 VM 재부팅을 수행했고 `command-reconcile`도 성공했다. 전후 보고서의 operationId와 인증된 기록값을 대조했다. 저장한 native uptime **126421ms**보다 재부팅 후 **51234ms**가 작다는 근거로 **PreviousBoot**를 확인했고, 제품 컨트롤러가 새로 조회한 빈 전용 box·정상 정책·동일 프로필을 확인한 뒤 명령 기록을 재조정하여 **CanLaunch=true**가 됐다. 종료 코드는 **null(미확인)**을 유지했다. Start.exe를 만들기 전 intent 단계에서 중단한 시험이며 카카오톡 실행이나 실제 명령의 성공 종료를 주장하지 않는다.

## Windows CI

PR과 main push에서 PowerShell 7과 `global.json`으로 검증·WPF·빌드 스크립트 회귀를 순차 실행하는 workflow를 추가했다. 기본 `contents: read`, 공식 Actions 커밋 고정, checkout 자격 증명 미보존, 로그/합성 PNG만 7일간 업로드한다. 실제 hosted 실행 상태와 로그는 [작업 브랜치의 Actions 결과](https://github.com/frozenvoice/sidetalk/actions?query=branch%3Acodex%2Frecovery-reconciliation)에서 확인한다. Windows Server 2022 CI 성공은 Sandboxie 복구 통합 시험 결과가 아니다.

제품·회귀 소스 `21edcfa`의 [첫 PR CI](https://github.com/frozenvoice/sidetalk/actions/runs/37146806599)는 모든 단계가 **성공(1분 33초)**했다. 이 실행에서 발생한 Node 20 폐기 경고를 반영하여 공식 Node 24 Actions의 고정 커밋으로 갱신했다(checkout 7.0.1, setup-dotnet 6.0.0, upload-artifact 7.0.1). 최종 브랜치의 재검증 결과는 위 Actions 링크에 별도 실행으로 남는다.

# 검증 결과 — 0.1.6 / 2026-09-22

## 토큰 핸들·조회 오류 구분·앱 아이콘 후속 수정

`codex/approved-setup-recovery`의 `607d93dfecade9489de37d64e9a14f53ae6b32f4`에서 이어서 수정했다. 시작 당시 로컬/원격 HEAD가 같고 작업 트리는 깨끗했다. 실행 중인 배포본은 0.1.5였고 EXE/DLL의 ProductVersion에는 이전 빌드 기준 `46dd793`이 표시됐다. 파일 해시와 시작 전 파일 시각을 기록했으며 바이너리가 검토 커밋과 같다고 추정하지 않았다.

기존 배포 DLL에서 실제 Windows 호출을 실행해 **System.MissingMethodException: .ctor**를 재현했다. 공식 설치의 실행 중 SbieCtrl을 읽는 `CaptureManagementProcesses → CaptureProcess → QueryProcessSid(Int32) → QueryProcessSid(IntPtr) → OpenProcessToken` 경로이며 기존 소스 449행에서 발생했다. 사용자 정의 `SafeTokenHandle`의 out 마샬링에 필요한 인자 없는 생성자가 없었다. 프레임워크 `SafeAccessTokenHandle`로 교체했고 성공·실패·예외 경로에서 토큰을 해제한다. 실행 경로·서명·버전·사용자·세션·PID·생성 시각 검사는 유지했다.

상태 조회와 복구 조회 오류를 분리했다. 복구 조회만 실패하면 같은 주기에 확인한 프로세스·설치 정보·확인 시각은 유지하고 복구 단계에 실패를 표시한다. 상태 조회 자체가 실패하면 남은 관찰을 마지막 확인으로 표시한다. 필요한 검사 실패 중에는 실행·강제 종료·복구 권한을 해제하며 재시도 성공 후 새 결과로만 복원한다. 내부 메시지 .ctor를 기본 화면에 단독 표시하지 않는다. 진단에는 발생 단계·예외 타입·메시지·내부 예외·스택을 남기며 사용자 경로·SID·소유 토큰 표식은 가린다. 로그 저장 실패가 UI 상태를 다시 실패시키지 않게 한다.

기존 헤더의 라임색 이중 말풍선으로 투명 ICO의 16/24/32/48/64/128/256 프레임을 생성했다. EXE ApplicationIcon과 WPF의 명시적 assembly pack URI를 연결한다. CMD 파일의 아이콘을 바꾸는 작업이 아니다.

### 이번 검증

- 실제 Windows TOKEN_QUERY 현재 프로세스 조회·SID 일치·해제 및 잘못된 프로세스 핸들 실패 검사를 포함한 콘솔 회귀 **123/123 통과**. 토큰이나 SID 값은 출력하지 않는다.
- 수정된 DLL로 현재 실행 중인 공식 **SbieCtrl** 신원 조회에 성공했다. 현재 사용자/세션, 서명·버전·경로 및 생성 시각을 확인했고 조회 중 종료·설정 쓰기는 하지 않았다. SandMan은 실행 중이지 않아 실제 프로세스 조회를 수행하지 않았다.
- WPF 회귀에서 정상 관찰 뒤 복구만 실패, 상태 조회 실패와 반복 실패, 재시도 후 정상 회복, 실행·종료·복구 권한 차단, 로그 저장 실패를 확인했다. 기존 14개 fixture와 읽기 전용 검사도 통과했다.
- ICO의 7개 크기와 투명도, WPF pack 리소스와 원본의 바이트 일치, EXE의 실제 RT_GROUP_ICON/RT_ICON 내용 일치를 검사했다. 작업 디렉터리를 리소스 경로로 사용하지 않는다.
- 기존 루트 `build-local.cmd -NoLaunch -NoPause`의 self-contained 배포 검증으로 앱·시험 도구 빌드, 콘솔 검사, 두 프로젝트 publish와 ZIP 생성을 완료했다. 앱·시험 도구 빌드는 경고·오류 0이다. 최종 self-contained EXE의 아이콘 7개도 원본과 일치했다. 일반 사용자 화면 관찰 결과는 아래 후속 기록을 참고한다.

초기 관찰 뒤 사용자가 카카오톡과 관리 UI를 모두 닫았다가 다시 열었다고 알려왔다. 그 재실행 이후의 PID·생성 시각을 별도 보존 기준으로 기록했다. 에이전트는 카카오톡·SbieCtrl·SandMan을 종료하거나 새로 실행하지 않았다. 사용자에게 SIDETALK 창만 정상 종료하도록 요청하고 프로세스가 없는 것을 확인한 뒤 기존 보호 절차로 전체 배포 폴더를 교체했다. 교체 후에도 재실행 기준의 카카오톡 2개와 SbieCtrl의 PID·생성 시각·세션이 유지됐다. 프로필·전용 box·계정 데이터·명령 저널·복구 이력은 변경하거나 초기화하지 않았다.

Windows 화면 검증 도구는 초기화와 재시도 모두 `helper_unknown_error: apply deny-read ACLs`로 실패했다. WPF fixture, 실제 Windows 네이티브 호출, EXE 리소스 검사는 화면에서 본 일반 사용자 실행 결과와 구분한다. 제목 표시줄·작업표시줄·Alt+Tab·탐색기의 실제 표시 및 일반 사용자 run-local.cmd 최종 실행의 확인 여부는 최종 결과에 별도로 기록한다. 카카오톡 로그인·송수신은 이번 후속 수정에서 시험하지 않는다.

### 일반 사용자 최종 확인

사용자는 탐색기에서 루트 `run-local.cmd`를 실행한 뒤 **상태 표시와 제목 표시줄·작업표시줄·Alt+Tab·탐색기 EXE의 라임색 말풍선 아이콘이 모두 정상**이라고 확인했다. 이는 사용자 화면 관찰에 근거한 결과다. 실행 프로세스의 경로는 최종 배포 폴더의 `Sidetalk.exe`이고 버전은 0.1.6이며, 배포 파일의 SHA-256을 별도로 기록했다. 화면 도구로 직접 관찰한 결과로 취급하지 않는다.

최종 배포는 수정된 소스로 커밋 직전에 빌드되어 ProductVersion의 소스 접미사는 부모 커밋 `607d93d`이다. 이 접미사만으로 바이너리 동일성을 주장하지 않으며 EXE·App DLL·Core DLL 및 ZIP 해시는 `token-fix-package.json`에 기록했다. 이후 변경은 검증 문서뿐이다. 실제 실행 중 SandMan 조회, 이번 버전의 카카오톡 로그인·송수신, 실제 승인 복구에 따른 관리 UI 종료·설정 쓰기는 이번 후속 검증 범위에 포함하지 않았다.
로컬 근거: `artifacts/verification/token-fix-before-*.txt`, `token-fix-baseline.json`, `token-fix-user-reopened-baseline.json`, `token-fix-build*.txt`, `token-fix-native-after.json`, `token-fix-package.json`, `token-fix-ui-checks/`. 생성물과 개인 환경 자료는 커밋하지 않는다.

# 검증 결과 — 0.1.5 / 2026-09-22

## 초기 설정·승인된 복구·재발 방지

기준 브랜치는 실제 원격 main과 같았던 `46dd793`이며 별도 `codex/approved-setup-recovery`에서 작업했다. 기존 격리 정책은 완화하지 않았다. 최초 승인 계획, 실제 INI 백업, 현재 사용자 `SbieCtrl_AutoRunSoftCompat=n`, 공식 기준과 일치하는 여섯 전역 템플릿 선택 제거, 필요한 전용 환경 생성, 파일/API 재검사를 추가했다. 동일 범위 자동 복구는 별도 선택 동의를 요구하고 철회할 수 있다. 열린 관리 UI는 승인 화면에 표시된 공식 설치·서명·버전·현재 사용자/세션·PID·생성 시각이 일치할 때만 보유 핸들로 정리한다. 실제 PC의 관리 UI를 종료하는 시험은 하지 않았다.

### 자동 검사와 배포

- Windows에서 루트 `build-local.cmd -NoLaunch -NoPause`로 기존 `scripts/build.ps1 -SelfContained`를 실행했다. Release 앱·IsolationProbe 빌드, 자체 콘솔 테스트, 두 프로젝트 publish와 ZIP/SHA-256 생성을 수행했다. 최종 검사 수는 아래 최종 결과에 기록한다. 컴파일 경고는 오류로 취급한다.
- 정상 무쓰기, 최초 승인, 같은 범위 재발, 여섯 템플릿의 63가지 비어 있지 않은 부분 집합, 사용자 UI 섹션, 다른 box·알 수 없는 규칙·공식 기준 변조, 오래된 UI/PID 교체, 종료 코드 0 무변경, 백업 실패·취소·외부 변경 경합·부분 생성 후 재시작, 컨트롤러 동시 요청·미완료 Start, 읽기 전용 모드, 데이터 보존을 가짜 외부 경계로 확인한다.
- 복구 기록은 시작부터 완료까지 연속적인 상태 전이를 검증하며, 손상·중복·고아 완료 기록을 준비 완료로 취급하지 않는다. 자동 복구 동의의 선후관계는 벽시계에 의존하지 않아 시계가 뒤로 바뀌어도 철회가 우선한다.
- WPF에 예시 상태만 주입한 `tests/Sidetalk.UiChecks`의 fixture 검사로 승인·중단·차단·정상·실패·읽기 전용 화면과 기존 실행 상태를 렌더링했다. 승인 범위 변경 시 체크 해제, 복구 실패 뒤 실행 비활성화, 읽기 전용 동작 금지를 확인했다. 실제 Sandboxie나 계정에 연결한 GUI 시험과 구분한다.
- 개발 중 컴파일/테스트 실패에서는 기존 0.1.4 배포본이 유지됨을 확인했다. 통과 후에만 0.1.5 self-contained 배포본을 기존 `artifacts/publish/win-x64` 경로로 반영했다. 루트 실행·빌드 파일과 배포 교체·롤백 스크립트는 변경하지 않았다.

### 이 PC에서 수행한 읽기 전용 검사

공식 설치의 SbieDll을 통해 서비스를 조회한 실제 INI는 `C:\WINDOWS\Sandboxie.ini`였다. 경로를 상수로 가정하지 않았고 사용자 섹션 역시 서비스 응답으로 얻었다. 전체 명시적 섹션은 3개였고 실제 파일과 API가 일치했다. 공식 여섯 템플릿의 내용·순서 검사도 통과했다. 현재 전역 Template 선택은 **0개**, 현재 사용자 `SbieCtrl_AutoRunSoftCompat` 값은 **n**이었다. 실제 전용 box API 조회를 실행했고 프로세스는 **0개**였다. 앞서 정책 검사에서 반환된 빈 배열을 재사용하지 않았다.

이 API 검사는 Codex 자식 프로세스에서 수행했다. 물리적 프로필 경로 확인은 Codex/MSIX의 파일 가상화를 감지해 차단했다. Shell을 통해 배포 앱의 읽기 전용 진단을 요청한 시도도 같은 경로 차단을 반환했다. 따라서 이를 일반 사용자 GUI의 Ready로 보고하지 않는다. 네이티브 UI 도구는 실행 도우미의 ACL 초기화 오류로 사용할 수 없었다.

검사 전후 실제 INI의 SHA-256, Codex에서 보이는 프로필의 SHA-256, 기존 본계정 카카오톡의 PID·생성 시각·세션이 모두 같았다. 일반 사용자 프로필과 전체 계정 데이터 무결성까지 확인한 결과는 아니다.

현재 설정이 이미 정상이라 일부러 템플릿을 추가하거나 자동 검사 옵션을 바꾸지 않았다. 실제 SbieIni 쓰기, 실제 관리 UI 종료, 실제 일반 사용자 GUI에서 승인 버튼 클릭과 자동 재발 복구는 **미검증**이다. 카카오톡 실행·로그인·송수신은 이번 작업에서 수행하지 않았다. 아래의 과거 실계정 기록은 이번 변경의 검증 근거로 재사용하지 않는다.

로컬 근거는 `artifacts/verification/setup-recovery-build-*.txt`, `setup-recovery-native-readonly.json`, `setup-recovery-preservation.json`, `setup-recovery-ui-fixtures-*.txt`, `ui-checks/ui-recovery-*-fixture.png`에 둔다. 개인 경로가 포함된 원본·백업·이미지는 Git에서 제외한다.

WPF 회귀 검사는 `dotnet run --project tests/Sidetalk.UiChecks/Sidetalk.UiChecks.csproj -c Release -- artifacts/verification/ui-checks`로 재현한다. 실제 창 조작 없이 WPF 객체에 예시 상태를 주입해 14개 fixture를 생성한다.

### 최종 결과

Release 빌드 경고·오류 0, 자체 콘솔 회귀 **118/118 통과**, WPF 회귀 검사와 **14개 fixture 생성 통과**. 기본 창의 실제 스크롤 가시 영역에 승인 버튼이 완전히 들어오는지도 확인했다. 최종 self-contained ZIP은 `artifacts/SIDETALK-0.1.5-win-x64-self-contained.zip`이며 같은 버전의 배포 폴더를 루트 진입점에서 사용한다. 일반 사용자 GUI의 실제 설정 쓰기와 실계정 검증은 위에 적은 대로 남아 있다.

# 과거 검증 기록

## 2026-09-16 — 실행 화면과 상태 설명 정리

차콜·라임 테마로 실행 화면을 재구성하고 경로·PID·상세 검사 결과를 초기 설정·진단 탭으로 옮겼다. 상태 배지는 실행 프로세스 관찰을 뜻하는 `실행 중`으로 표시하고, `현재 격리 설정 확인됨 · 시작 당시 적용 규칙은 확인 불가`를 본문에 남긴다. 실행 실패와 잔여 프로세스 안내는 기본 화면에 계속 표시하며, 상세 정책 문구는 진단 탭에서 확인할 수 있다. 정상 종료는 카카오톡 트레이를 우선 안내하고, SIDETALK의 버튼은 전용 환경 전체를 종료하는 `보조 카카오톡 강제 종료`로 표시한다.

격리 정책과 Sandboxie 명령은 변경하지 않았다. 실행·종료 버튼과 이벤트 처리 모두 `ActionsPermitted`, `CanLaunch`/`CanStop` 조건을 확인한다. 창은 열릴 때와 이후 4초마다 상태를 자동 확인한다. UI 작업 중 실제 카카오톡을 실행·종료하거나 전역 설정·프로필·로그인 데이터를 변경하지 않았다.

검증은 self-contained 배포 스크립트의 Release 빌드·콘솔 검사 **76/76 통과**를 기준으로 한다. 별도 렌더러는 실제 계정에 접근하지 않는 예시 snapshot으로 준비·실행·차단·오류·진단·최소 창을 그리며, 실행 실패와 종료 후 잔여 프로세스 안내가 기본 화면에 남는지도 확인한다. 실제 사용자 컨텍스트의 읽기 전용 렌더링은 이 예시 이미지와 구분한다. 화면 이미지는 실제 계정 호환성 또는 정책 적용 증명의 근거가 아니다.

근거: `artifacts/verification/ui-redesign-build.txt`, `ui-preview-render.txt`, `ui-*-fixture.png`, `ui-native-running.png`. 예시 이미지 파일명에는 fixture를 붙이며 생성 산출물은 Git에서 제외한다.

## 2026-09-16 — 보조 계정 사용 보고와 트레이 종료 후 실행 차단 복구

사용자는 보조 카카오톡에 새 로그인 화면이 표시됐고, 보조 계정으로 로그인한 뒤 기존 본계정 PC 카카오톡과 동시에 대화가 가능했다고 보고했다. 이는 사용자 관찰에 근거한 실행·로그인·동시 송수신 확인이며, 아래의 과거 미검증 기록 이후 추가된 결과다. 자동 진단이나 프로세스 존재만으로 계정 성공을 판정한 것이 아니다. 알림, 재실행 후 로그인·대화 유지, 전체 계정 데이터 분리와 본계정 무간섭은 별도 검증이 남아 있다.

이후 사용자가 보조 카카오톡의 트레이 메뉴로 종료한 뒤 SIDETALK가 실행을 차단했다. 전용 box 프로세스는 0개였고, 일반 사용자 컨텍스트의 읽기 전용 진단은 전역 `Template` 및 공식 기본 규칙과 다른 템플릿 확장을 차단 이유로 반환했다. `WindowsRasMan`, `OnScreenKeyboard`, `WindowsLive`, `Edge_Fix`, `OfficeLicensing`, `OfficeClickToRun` 6개가 전역에 추가돼 있었다. 트레이 종료가 이 규칙을 추가했다는 증거는 없다.

표시됐던 창은 SbieCtrl 자동 호환성 창이었다. 공식 v1.18.4 소스에서 자동 표시 상태의 X도 확인과 동일하게 템플릿을 적용함을 확인했고, 앞서 X로 닫으라고 한 안내를 수정했다. 실제 클릭 경로를 직접 관찰하지는 않았다.

현재 설정 백업과 6개 제거안을 준비한 뒤 사용자가 “6개만 원복해줘”라고 승인했다. 공식 `SbieIni.exe delete GlobalSettings Template <값>`으로 해당 값만 제거했고, 결과가 준비한 복구안과 일치함을 빈 줄을 제외한 전체 설정 비교로 확인했다. 전용 box 설정과 사용자 UI 설정은 유지했으며 프로필·저장 데이터·명령 기록은 수정하지 않았다. `/drv` 경로의 종료 코드 0은 실제 제거를 뜻하지 않아, 파일 및 API 재조회에서 변화가 없음을 확인한 뒤 기본 설정 도구 경로로 적용하고 결과를 다시 검증했다.

복구 후 일반 사용자 컨텍스트의 진단은 `Ready`, `CanLaunch=true`, `CurrentConfigurationVerified=true`, box 프로세스 0개였다. 진단 모드의 `ActionsPermitted=false`는 읽기 전용 모드에 따른 값이다. 기존 본계정 프로세스의 PID와 시작 시각도 유지됐다. 이후 사용자가 SIDETALK에서 상태를 새로 확인하고 보조 카카오톡을 다시 실행할 수 있다고 확인했다. 재실행 후 로그인·대화 기록 유지 여부는 아직 별도로 확인하지 않았다. 앱 코드나 검사 정책은 변경하지 않았다.

근거: `artifacts/verification/relaunch-blocked-20260916-164100.json`, `relaunch-restore-proposal.json`, `relaunch-restore-results.json`, `relaunch-restored-native.json`과 같은 폴더의 설정 백업. 개인 경로와 소유 표식이 포함된 원본은 Git에서 제외한다.

## 2026-09-16 — 일반 GUI 프로필 누락 복구

탐색기에서 실행한 GUI는 `SetupRequired`였지만 Codex에서 실행한 진단은 `Ready`였다. 파일 핸들의 실제 경로를 확인하니 기존 프로필은 Codex MSIX 패키지의 `LocalCache/Local/SIDETALK/profile.json`에 있었고, 일반 사용자의 `%LOCALAPPDATA%/SIDETALK/profile.json`에는 없었다. 앞선 도구 진단의 `Ready`는 일반 GUI의 실행 준비 완료를 증명하지 못했다.

Codex의 파일 가상화를 상속하지 않는 일반 사용자 프로세스에서도 복구 전 `SetupRequired`를 재현했다. 기존 프로필의 사용자·소유 표식·전용 box 정책·실제 저장 경로와 빈 box를 검증하고, 실제 사용자 위치에 프로필이 없는 경우에만 기존 프로필을 바이트 그대로 복구했다. 원본 프로필은 보존했으며 기존 파일을 덮어쓰거나 새 box를 만들지 않았다. 명령 기록이 있는 경우에는 이 프로필 단독 복구를 수행하지 않도록 제한했다.

복구 후 파일 핸들의 실제 경로가 일반 사용자의 `SIDETALK/profile.json`임을 확인했고, 같은 일반 사용자 컨텍스트의 읽기 전용 조회에서 `Ready`, `CanLaunch=true`, `CurrentConfigurationVerified=true`, box 프로세스 0개를 확인했다. 원본과 복구된 프로필의 SHA-256이 같고 Sandboxie.ini도 변경되지 않았다. 사용자가 탐색기에서 다시 실행한 실제 GUI의 보조 카카오톡 실행 버튼 활성화도 확인했다. 카카오톡 실행·로그인·송수신은 이번 복구 검증에 포함하지 않는다.

원인 조사 중 발견한 별도 오인 경로도 보완했다. `ProfileStore.Load()`는 읽기 오류를 숨길 수 있는 `File.Exists` 검사 대신 기존 파일을 직접 읽고, 실제 파일/상위 폴더 부재만 미설정으로 취급한다. 접근 오류·잘못된 파일 형태는 차단 상태로 전달한다. 기존 경로에 디렉터리가 있는 경우 Load/Create 모두 실패하고 그 안의 데이터가 보존되는 회귀 등을 추가했다. Release 앱·시험 도구 빌드 경고 0 / 오류 0, 콘솔 검사 **76/76 통과**, self-contained 배포 교체를 확인했다.

근거: `artifacts/verification/gui-live-profile.json`, `native-user-diagnostics-20260916-162738.json`, `real-profile-recovery.json`, `profile-read-fix-build.txt`. 개인 경로가 포함된 원본은 Git에서 제외한다.

## 0.1.4 — 저널 최초 초기화 중단 복구

6f04933에서 저널 폴더 생성 후 command.lock 생성 전 중단되면 일반 GUI에서 계속 Unknown에 머무르는 경로를 보완했습니다. 일반 GUI 검사는 이미 존재하는 저널 폴더의 잠금을 생성·획득한 뒤 기존 기록 조회와 명령 종료·새 박스 조회 절차를 이어갑니다. 저널 폴더 자체가 없으면 검사만으로 만들지 않습니다.

잠금 파일 생성은 완료 판정이 아닙니다. 기존 intent·started·completed와 실패한 임시 파일을 보존하고, 미완료·손상·고아 기록을 기존 규칙대로 검사합니다. 잠금 획득에 실패하면 조작을 차단합니다. 읽기 전용 진단은 파일을 만들거나 완료 기록을 추가하지 않습니다.

로컬 Windows에서 수정 전 회귀는 71/74였습니다. 새 네 검사 중 일반 GUI의 빈 폴더 복구, 기존 미완료 기록 복구, 손상 기록 검사 진입이 잠금 부재 때문에 실패하고 읽기 전용 검사는 통과했습니다. 수정 후 **74/74 통과**를 확인했습니다.

새 회귀 네 개는 실제 컨트롤러·파일 저널을 사용합니다. 빈 폴더 복구 후 Start 호출이 0회인지, 읽기 전용 검사에서 파일을 생성하지 않는지, 살아 있는 미완료 명령을 계속 차단하다 종료와 새 상자 조회 후에만 완료하는지, 기존 intent·started 바이트와 손상 기록이 보존되는지 검사합니다. 외부 런타임·명령 핸들은 가짜이며, 실제 앱을 강제 종료한 시험 대신 중단 직후의 파일 상태를 재현했습니다.

근거: artifacts/verification/review-6f04933-before-tests.txt와 review-6f04933-after-tests.txt. 이 결과는 로컬 검사이며 GitHub Actions의 CI 통과를 의미하지 않습니다. 실제 카카오톡 실행·로그인과 기존 미검증 항목은 이번 수정으로 완료 처리하지 않습니다.

Release 앱·시험 도구 빌드는 경고 0 / 오류 0이며, Windows x64 self-contained ZIP을 생성했습니다. 배포된 Sidetalk.exe의 읽기 전용 진단은 0.1.4, Ready, CurrentConfigurationVerified=true, ActionsPermitted=false로 종료 코드 0을 반환했습니다. 전후 Sandboxie.ini·profile.json 해시와 메인 카카오톡 PID·생성 시각이 같았고, 실제 프로필에 저널 폴더를 만들지 않았습니다.

배포 근거: artifacts/verification/release-0.1.4-build.txt, release-0.1.4-diagnostics.json, release-0.1.4-environment-checks.json, release-0.1.4-checks.json.

## 0.1.3 리뷰 보완 범위

기준 커밋 9a2025b의 재시작 시 명령 추적 유실, 현재 설정과 기존 프로세스 적용 규칙의 혼동, 최초 프로필 저장 중단 문제를 보완했습니다.

- 명령 생성 전에 intent를 완전히 기록합니다. 이후 PID·생성 시각·실행 경로를 기록하고, 종료 확인과 새 전용 박스 조회가 모두 끝난 뒤에만 completed를 추가합니다. 앱 종료는 기록을 완료 처리하지 않습니다.
- PID가 재사용되면 대체 프로세스를 채택하지 않습니다. 조회가 불확실하면 차단하고, 이전 종료 코드를 얻을 수 없으면 null로 남깁니다.
- PID 기록 전 중단은 같은 설치의 Start.exe 후보 검사로 복구합니다. 후보가 관찰되거나 조회 중 사라지는 등 불확실하면 다음 새로고침까지 차단합니다. 다른 샌드박스의 명령이 이 복구를 지연시킬 수 있지만 종료하거나 수정하지 않습니다.
- 작업 잠금은 같은 프로필의 컨트롤러 간 명령·복구를 직렬화합니다. 진단·렌더링 모드는 기록을 생성·완료하지 않고, ActionsPermitted=false를 별도 표시합니다.
- CurrentConfigurationVerified는 현재 설정에만 해당합니다. 모든 살아 있는 프로세스는 EffectiveRulesUnverified이며, SIDETALK에서 새로 실행한 프로세스도 실제 적용 규칙을 인증하지 않습니다.
- 최초 profile.json은 같은 폴더의 임시 파일에 완전히 기록·Flush한 뒤 덮어쓰기 없는 이름 변경으로 반영합니다. 손상된 기존 파일과 실패한 임시 파일은 보존합니다.

## 0.1.3에서 직접 수행한 검증

- Release 앱 빌드: 경고 0 / 오류 0. 통합 회귀 검사 **70/70 통과**.
- 컨트롤러 회귀 12개는 가짜 런타임·명령 핸들을 주입하되 실제 컨트롤러와 파일 저널을 통과합니다. 실행 → 시간 초과 → Dispose → 새 컨트롤러 → 실행 중 차단 → 종료 확인 → 새 상자 조회를 검사합니다. 명령 대기 도중 Dispose, 동시 컨트롤러, 조회 실패, PID 기록 전 중단, 읽기 전용 진단도 포함합니다.
- 현재 테스트 프로세스의 실제 Windows 핸들로 PID·생성 시각·이미지 경로를 확인했습니다. 생성 시각이 다른 동일 PID와 이미지 불일치를 구분하고, 시험 프로세스를 종료하지 않습니다.
- 기존 프로세스가 있는 상태에서 현재 설정을 변경·복원하는 컨트롤러 회귀를 통과했습니다. 시작 당시 적용 규칙은 자동으로 검증 완료 처리되지 않습니다. 실제 Sandboxie 설정을 완화하는 시험은 수행하지 않았습니다.
- 최초 프로필 저장의 부분 쓰기 실패·동시 생성·손상된 경쟁 파일 보존 회귀를 통과했습니다.
- 설치된 Sandboxie Plus 1.18.4의 읽기 전용 API 검사 재수행: Success=true, InstallationVerified=true, 공식 기본 템플릿 일치, 명시적 전역 설정 0개 / 유효 전역 설정 키 15개.
- 수정판의 읽기 전용 진단과 WPF 소형 화면 렌더링 통과. 현재 구성 검증 및 빈 전용 상자 확인: Ready, CurrentConfigurationVerified=true, ProcessPolicy=NoProcesses. 진단 모드는 ActionsPermitted=false이며 기록을 생성·완료하지 않습니다.

- Windows x64 self-contained 배포본 생성 및 배포본의 읽기 전용 진단 실행 통과. 시험 도구도 함께 publish했습니다.
- 배포 시험 전후 Sandboxie.ini·기존 profile.json의 SHA-256이 같고, 메인 카카오톡 PID·생성 시각이 유지됐습니다. 읽기 전용 진단은 실제 프로필에 command-journal을 만들지 않았습니다. 검사 시 실행 중인 Start.exe는 없었습니다.
- 배포된 시험 도구의 host-verify로 이전의 호스트 시험 파일 4개·HKCU 값 1개가 그대로임을 재확인했습니다. box 내부 쓰기·재실행 시험이나 실제 계정 시험을 새로 수행한 결과는 아닙니다.

로컬 근거: artifacts/verification/review-9a2025b-tests.txt, release-0.1.3-native-config.json, release-0.1.3-diagnostics.json, release-0.1.3-ui.png, release-0.1.3-packaged-diagnostics.json, release-0.1.3-environment-checks.json, release-0.1.3-host-sentinels.json, release-0.1.3-checks.json. 개인 경로가 포함된 원본은 Git에서 제외합니다.

실제 지연 중인 Sandboxie Start.exe를 둔 채 WPF 앱 프로세스를 종료·재시작하는 통합 시험은 아직 하지 않았습니다. 컨트롤러 흐름과 Windows 프로세스 식별 API는 각각 위 검사로 확인한 범위입니다. 아래의 실제 Sandboxie 파일/HKCU 시험은 0.1.2에서 수행한 기록이며, 이번 코드 변경으로 새로운 실계정 검증이 완료됐다고 간주하지 않습니다.

명령 기록은 0.1.3에서 새로 요청한 작업부터 적용됩니다. 0.1.2가 이미 시작한 기록 없는 명령을 소급해 복구하지는 못하므로, 이전 버전의 명령이 완료된 뒤 교체해야 합니다. 저널은 현재 사용자 소유의 로컬 메타데이터이며, 동일 사용자가 모든 기록을 직접 제거하거나 변조하는 상황까지 방어하는 보안 경계는 아닙니다.

## 0.1.2에서 수행한 실제 격리 시험 기록
### 판정

앱 자체 검증, 실제 Sandboxie Plus 1.18.4 연동, 전용 box 설정 검증, 계정 없는 파일·레지스트리 격리 시험을 완료했습니다. 무해한 시험 토큰의 숨김·쓰기 분리·재실행 후 유지는 통과했습니다. WebCache COM/IPC와 같은 이름의 호스트 프로세스 보호는 아직 시험하지 않았습니다.

공식 카카오톡을 Sandboxie 안에서 실행하거나 보조 계정으로 로그인하지 않았습니다. 따라서 두 계정 동시 로그인·송수신·알림·카카오톡 로컬 데이터 유지와 메인 계정 무간섭은 아직 검증하지 않았으며, 전체 수용 기준 충족으로 보고하지 않습니다.

### 현재 PC에서 직접 확인한 사실

| 항목 | 결과 |
|---|---|
| Windows | Windows 11 Pro, x64, 10.0.26200 |
| 개발 환경 | .NET SDK 8.0.424 / Windows Desktop Runtime 8.0.30 |
| 공식 카카오톡 | 26.7.1.5263, Program Files (x86)의 공식 설치 |
| 카카오톡 서명 | Authenticode 유효, Kakao Corp. |
| 기존 메인 카카오톡 | 검증 전후 실행 상태가 유지됨 |
| Sandboxie Plus | 공식 1.18.4 x64 설치 완료 |
| SIDETALK 변경 범위 | SIDETALK 전용 box 설정과 시험 데이터만 사용. 카카오톡 설치 파일은 서명·버전 확인에만 사용했고, 인증·대화 데이터는 읽거나 복사하지 않음 |
| 카카오톡 실행·로그인 | 수행하지 않음 |

기존 메인 프로세스가 검증 전후 유지된 것은 관찰 기간의 사실입니다. 보조 카카오톡을 실행한 뒤의 계정·대화·메인 무간섭 검증을 대신하지 않습니다.

### 빌드·자체 회귀 검증

- 0.1.2 Release 빌드와 회귀 테스트 48/48 통과.
- 외부 테스트 패키지 없이 정책·프로필·명령 인자·템플릿 출처·버전 비교·명령 수명 회귀를 실행.
- 무작위 전용 환경, 재실행 시 프로필 유지, 손상 프로필 재생성 거부, 다른 SID·저장 루트 거부를 확인.
- 자동 삭제 금지, 소유 표식, 허용 목록, 상속 예외, INI 입력 주입을 검사.
- 임의 box·상대 경로·다른 실행 파일을 거부하고 명령을 셸 문자열로 연결하지 않음을 확인.
- WPF 화면 렌더링, 읽기 전용 진단, Windows x64 self-contained publish를 확인.

### Sandboxie 설치와 API 연동

공식 Sandboxie Plus 1.18.4 x64 설치 파일의 서명과 해시를 사전에 확인한 뒤, 설치 실행을 사용자 확인 후 진행했습니다. 설치 결과는 다음과 같습니다.

| 구성요소 | 검증 |
|---|---|
| SandMan | 1.18.4 |
| Start.exe / SbieDll.dll / SbieSvc.exe | 5.73.4 |
| SbieDrv.sys | 5.73.2, 검토한 공식 파일의 SHA-256과 일치 |
| 활성 드라이버 API 버전 | 5.73.2 |
| SbieSvc | SCM 구성·서명된 구성요소·안정적인 실행 상태 확인 |
| 실행 중 서비스 이미지 경로 | 일반 사용자 권한에서 ERROR_ACCESS_DENIED로 조회 불가. 보안 기능을 낮추거나 권한을 우회하지 않았고, SCM 등록 경로와 구성요소 서명으로 확인함 |

드라이버 해시와 설치·구성 진단의 원본 산출물은 작업 폴더의 artifacts/verification 아래에 보관하며, 공유 전 사용자 경로·프로세스 정보를 확인합니다. 문서에는 SID, box 소유 토큰, 실행 식별자를 기록하지 않습니다.

### 전용 설정 검증

전용 설정 적용은 사용자 확인 후 진행했습니다. 호환성 창이 표시된 뒤 전역 설정 6개가 추가된 상태를 확인했습니다. 해당 변경을 별도 백업한 뒤 두 번째 사용자 확인을 받고 그 6개만 제거하고 설정을 다시 불러왔습니다. 마지막 상태를 다시 조회해 전역 설정을 임의로 남기지 않았음을 확인했습니다.

native-config-after-setup.json과 native-config-after-recovery.json의 실제 Sandboxie API 조회 결과는 다음과 같습니다.

- InstallationVerified=true, Success=true
- 공식 기본 18개 섹션의 출처·내용 검증 통과
- 명시적 전역 설정 0개
- 공식 기본 병합을 포함한 유효 설정 키 15개
- 전용 box 소유 표식·저장 경로·현재 사용자 SID 검증 통과
- 설정 조회 중 사라짐·잘림·오류가 발생하면 제어를 차단

검토 기준 4bb5a4e의 두 결함도 다음처럼 보완했습니다.

- 설정 이름과 값을 명시적 조회(0x70000000)와 공식 기본 병합 조회(0x60000000)로 구분합니다. DefaultTemplates와 연결된 공식 1.18.4 기본 템플릿, 드라이버가 직접 참조하는 특수 섹션을 고정 자료와 대조합니다.
- 명시적 전역 규칙은 기존 허용 목록으로만 검사합니다. 자동으로 병합되는 공식 기본 규칙은 출처·내용·중복·순서를 확인한 유효 결과와 비교합니다. 모든 템플릿을 무조건 신뢰하거나 전역 설정을 지우지 않습니다.
- 기본 WebCache COM/IPC 접근은 전용 box에서 ClosedClsid·ClosedIpcPath로 차단하도록 구성했습니다. 실제 차단 효과는 아직 확인하지 않았습니다.
- 실행 명령의 수명과 box 프로세스 관찰을 분리했습니다. 명령 실행 중·종료 불명·조회 실패 상태에서는 중복 명령을 허용하지 않고, 명령이 종료된 뒤 새로 검증한 box 상태로 실패·잔여 프로세스·종료를 구분합니다.
- 실행 실패·즉시 종료·시간 초과·나중 종료·조회 경합·종료 후 잔여 프로세스에 대한 회귀를 포함합니다. 0.1.2 당시 대기 명령 추적은 메모리에만 보관했습니다. 이 제한은 위 0.1.3의 영속 명령 기록으로 보완했습니다.

### 계정 없는 실제 격리 시험

무해한 SIDETALK 시험 토큰을 사용해 사용자 확인 후 전용 box에서 실행했습니다. 시험 프로그램은 카카오 인증·대화 파일을 읽지 않고, UserProfile·Roaming·Local·ProgramData/Kakao 아래의 SIDETALK-IsolationProbe 경로 4개와 HKCU의 SIDETALK 시험 키 1개만 다룹니다.

처음 실행은 시험 프로그램의 Roaming known-folder 조회가 빈 값을 반환해 실행 전 단계에서 실패했습니다. 시험 경로의 정확한 호스트 논리 경로 검증은 유지한 채 DoNotVerify 방식으로 known-folder 경로를 조회하도록 수정했고, 수정 후 시험을 다시 수행했습니다. 첫 실패와 수정 과정은 격리 약화나 보안 예외 추가로 해결하지 않았습니다.

최종 시험 근거는 다음과 같습니다.

- host-between-runs.json: 호스트 파일 4개와 HKCU 값 1개가 box 쓰기 전후 그대로 유지됨.
- host-after-recheck.json: 호스트 파일 4개와 HKCU 값 1개가 재실행 후에도 그대로 유지됨.
- box-write의 결과 파일은 호스트에 나타나지 않고 전용 box의 물리 저장소에서만 확인됨.
- box-write 종료와 box-recheck 시작 사이에 전용 box 프로세스가 남지 않음을 확인함.
- box-recheck에서 전용 box 안의 파일 4개와 HKCU 값 1개가 유지됨.
- box-write·box-recheck 모두 현재 프로세스와 열거된 모든 box 프로세스의 box 이름·사용자 SID·native 파일/레지스트리/IPC 격리 경로를 Sidetalk.Core API로 확인함.
- WebCache COM/IPC 차단은 정책에 구성했지만 실제 무해한 연결 시험은 아직 하지 않음.
- 같은 이름의 무해한 호스트 시험 프로세스와 전용 box 종료의 무간섭 시험은 아직 하지 않음.
- 다른 box는 현재 PC에 없어 교차 box 무간섭은 시험하지 않음.
- 시험 파일·레지스트리·box 데이터는 삭제하지 않음.

세부 원본은 다음의 로컬 산출물에서 확인할 수 있습니다.

- artifacts/verification/native-config-after-setup.json
- artifacts/verification/native-config-after-recovery.json
- artifacts/verification/isolation-probe/host-between-runs.json
- artifacts/verification/isolation-probe/host-after-recheck.json
- artifacts/verification/isolation-probe/manifest.json

산출물 JSON에는 시험 토큰과 실행 식별자가 들어 있으므로 저장소에 추적하지 않습니다.

### 남은 검증과 제한

| 항목 | 상태 / 이유 |
|---|---|
| Sandboxie 서비스·드라이버·SbieDll API ABI | 통과. 일반 사용자 권한에서 서비스 live image 조회만 ERROR_ACCESS_DENIED 제한 |
| 공식 기본 템플릿과 전역 병합 | 실제 API 조회 18개 기준 섹션·15개 유효 키 통과 |
| 호스트 시험 파일·HKCU 숨김 | 계정 없는 시험 통과 |
| 쓰기 분리와 재실행 후 유지 | 계정 없는 시험 통과 |
| WebCache COM/IPC 차단 | 정책 구성은 확인했지만 실제 무해한 연결 시험은 미수행 |
| 메인 카카오톡과 별도 box 무간섭 | 보조 카카오톡 실행 전후의 무간섭은 미검증. 기존 메인 프로세스 유지 관찰만 수행. 다른 box가 없어 교차 시험도 미수행 |
| 공식 카카오톡 보조 실행 | 미검증 |
| 보조 계정 로그인·기기 인증 | 미검증. 사용자가 공식 창에서 직접 진행해야 함 |
| 두 계정 동시 로그인·송수신·알림 | 미검증 |
| 카카오톡 인증·대화 데이터 영속성 | 미검증 |
| 카카오톡 업데이트 후 호환성 | 미검증 |

다음 단계는 남은 계정 없는 시험(WebCache COM/IPC와 같은 이름의 호스트 프로세스 보호)을 먼저 완료하는 것입니다. 그 후 실계정 실행·로그인·송수신·알림은 별도의 사용자 확인을 받고 진행합니다. 로그인 성공은 프로세스 존재나 명령 종료로 판정하지 않습니다.
