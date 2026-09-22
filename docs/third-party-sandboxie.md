# Sandboxie Plus 기준 데이터

SIDETALK는 Sandboxie Plus를 설치하지 않는다. 승인된 초기 준비와 동일 범위 복구에만 공식 설정 도구를 사용한다. 아래
파일은 실행 시 읽어 온 설정을 검토할 때 사용하는, 공식 원문 기반의 검사 기준이다.

- 기준 버전: Sandboxie Plus v1.18.4 (`Templates.ini`)
- 원문: <https://raw.githubusercontent.com/sandboxie-plus/Sandboxie/v1.18.4/Sandboxie/install/Templates.ini>
- 원문 SHA-256: `9194D31B8986D17B8698C1394C0722DACE698838DADDF41612F1083A8A4E52D7`
- 라이선스/출처: Sandboxie Plus upstream, GPL-3.0-or-later. 기준 데이터는 필요한
  설정 섹션만 추출한 파생 데이터이며 원문 출처를 유지한다.

## 포함 범위

`src/Sidetalk.Core/SandboxieDefaults.json`은 다음 18개 섹션을
`섹션 → 설정 이름 → 순서가 보존된 값 배열`로 표현한다. 같은 설정 이름이 여러 번
나오면 배열의 순서와 중복을 그대로 유지한다. `Tmpl.*` 메타데이터도 포함한다.

- `[DefaultTemplates]`와 이 섹션이 참조하는 10개 `Template_*` 섹션
- `BuiltinTemplateHashes.json`에서 검증하는 7개 특수 섹션

`tests/Sidetalk.Tests/Fixtures/sandboxie-1.18.4.ini`에는 위 섹션의 원문 줄과
주석을 그대로 담아 설정 파서 회귀 테스트에 사용한다. 이 fixture는 설치 파일이나
Sandboxie 설정으로 사용하지 않는다.

## 갱신 절차

원문 파일을 별도로 내려받거나 준비한 뒤 다음 명령을 실행한다.

```powershell
.\scripts\update-sandboxie-baseline.ps1 `
  -SourcePath .\artifacts\research\Templates-v1.18.4.ini
```

스크립트는 입력 파일의 SHA-256을 먼저 확인한다. 해시가 다르면 중단하며, 성공할
때만 저장소 안의 JSON과 fixture 두 파일을 갱신한다. 스크립트는 Sandboxie 설치,
서비스·드라이버 변경, 전역 설정 적용을 수행하지 않는다.

새 upstream 원문을 기준으로 삼으려면 먼저 코드의 지원 버전과 기대 SHA, 테스트
fixture 이름을 함께 검토하고 변경해야 한다. 원문 변경을 숨긴 채 해시 검사를
완화하거나 모든 템플릿을 신뢰 범위에 넣는 방식은 사용하지 않는다.

## 복구 판별용 여섯 섹션

`src/Sidetalk.Core/RecoveryTemplates.json`은 같은 해시로 고정한 원문에서 WindowsRasMan, OnScreenKeyboard, WindowsLive, Edge_Fix, OfficeLicensing, OfficeClickToRun의 Template_* 섹션만 추출한다. 메타데이터·중복·값 순서를 보존하며 중첩 Template 참조가 생기면 재생성을 중단한다.

이 자료는 전역 허용 목록을 늘리지 않는다. 공식 출처와 실제 상속 결과가 일치하는 충돌의 제거 계획을 판별하는 데만 사용한다. 기존 SandboxieDefaults.json, fixture, BuiltinTemplateHashes.json과 IsolationPolicy의 허용 기준은 유지한다.