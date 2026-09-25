# 타이밍 재생 기록·건강 요약 인계 문서

> **상태:** S1~S3 구현(2026-09-26). 하네스로 검증했고, 실게임 기록은 아직 수집하지 않았다(11절 "남은 일").
> **표시 정책은 이 작업에서 바꾸지 않았다.** 구현에서 정한 세부와 사용법은 11절.
> **인계 범위 고정:** 표시 정책을 유지하면서 재생 기록과 건강 요약을 추가하고, 진단 옵션에서만 후보 버퍼를 수집한다.
> **대상:** 이 문서만 읽고 구현할 수 있는 후속 담당(Claude Code 등).
> 배경 문서: [SIGNAL-GATING.md](SIGNAL-GATING.md), [OVERLAY-TIMING.md](OVERLAY-TIMING.md).

## 1. 왜 하는가

- `OverlaySchedule`의 출력 시점(게이트 해제 사유, 내부 시계 기반 due/release)은 현재 로그로 **완전히 재현할 수 없다.**
  - `[timing]`의 `segitem`은 모델 보정용(kind·units·상대 시각·speed)이고, `show`는 출력뿐이다. 입력 `Item`의
    `Group`/`Detail`(눈·반격)/`Generation`/`ReceivedUs`/`Speed`를 모두 보존하는 기록은 없다.
  - `Tick`의 내부 시계(`_nowUs`)는 `maxDelta`로 자른 delta를 누적해 벽시계 `[timing]`과 다른 축이고, 프레임(펌프) 경계가 기록되지 않는다.
  - 화면 신호의 인스턴스 식별자·도착 순서, `Reset`/`Discard`/`FallBackToModel`/`ForgetBanner`, 후킹 상태 변화가 없다.
- 일부 기록 재생과 수동 비교는 해 왔지만, 전체 입력을 보존한 결정적 비교는 할 수 없었다.
- **"먼저 뜸 0"은 보장 조건이 아니다.** 60초 상한(`OverlaySchedule.Gate.cs` `GateCapUs`), 큐 4096 초과 시 강제 해제
  (`MaxWaiting`), 후킹 실패 시 모델 복귀(`Plugin.cs` 165~173행) 때문이다. 검증 문구는
  **"관측한 표본에서 조기 표시 0건"**으로 쓴다.

## 2. 목표와 완료 기준

| 단계 | 내용 | 완료 기준 |
| --- | --- | --- |
| **S1** | 스케줄러 경계 재생 기록 | 표시 정책 변경 없음. 지원하는 완전 기록에서 결정을 재현하고 유실·중간 시작·지원하지 않는 경합은 엄격 모드에서 거부한다. 기록 off에서는 레코드용 문자열 생성·큐 삽입·파일 쓰기를 하지 않는다 |
| **S2** | 상시 건강 카운터 + 진단 전용 후보 링 버퍼 | 게임당 건강 요약 1줄이 `TraceTiming`과 무관하게 남는다. 후보 버퍼는 `Diagnostics.ScreenProbe=true`에서만 동작하고, 예산·마스킹·덮어쓰기 수가 기록된다 |
| **S3** | 오프라인 재생기 (`tools/harness` 확장) | **완전한 기록 하나에서 출력 순서·내부 시각·해제 사유가 실기록과 일치.** 화면 정확도 판정은 독립 증거가 있는 사건에만 |

**비범위 (이 작업에서 하지 않는다):**

- 표시 규칙·게이트·장벽·상한·장부 판단 변경.
- S0(신호 유실 정책) 결정. 아래 "열린 결정" 참고.
- P3 폴백 신호(주사위 잠금·몬스터 이동 등) 연결.
- P4 런타임 모델 보정.
- 후보 신호의 자동 채택. 후보 수집·보고까지가 끝이다.

## 3. 제약 (반드시 지킬 것)

1. **표시 정책 불변.** 이 작업은 관측만 추가한다.
2. **포인터 지연 해석 금지.** 전환 시점에 예산 안에서 마스킹한 이름·경로를 **문자열로 복사**하고, 버퍼에는 그 문자열의
   내부 번호·시각·상태만 저장한다. 나중에 원시 포인터를 역참조하지 않는다. FairyGUI는 부모 연결을 바꾸고
   (`ScreenRecorder.PathOf`가 당시 시점에 경로를 읽는 이유), 객체 파괴 후 포인터가 재사용될 수 있다(`ScreenProbe` 주석).
3. **프레임 예산 유지.** `MaxPathsPerFrame`/`MaxLinesPerFrame`(ScreenRecorder), `MaxNewNamesPerFrame`(ScreenProbe)을
   유지하고 초과 수를 센다.
4. **개인정보.** 마스킹은 저장 전에. 후보 목록·재생 기록은 로컬 파일에만 두고 커밋·공유하지 않는다. `Mask`는
   비ASCII·6자리 이상 숫자·알려진 카메라 접미사만 가리는 휴리스틱이라 잔여 위험이 있음을 문서화한다.
5. **IL2CPP 금지 목록 준수.** 새 델리게이트/타입 등록, 게임 코드 Harmony 금지 ([ARCHITECTURE.md](ARCHITECTURE.md)).
   기록 writer는 소켓·메인 스레드를 막지 않는다. `Log/LogFileWriter.cs`의 유한 큐·전용 스레드 구조만 참고하고,
   **쓰기 예외를 삼키는 동작은 복제하지 않는다.**
6. **F10은 참고 표식.** 실측 0.30~0.53초는 화면 신호와 F10 입력 사이 간격이다(OVERLAY-TIMING.md "둘째 판 결과").
   신호가 실제 화면보다 그만큼 빠르다는 뜻이 아니다. `mark`를 조기 판정의 독립 증거로 쓰지 않는다.
7. **해제 근거와 화면 정확도 분리.** 같은 신호로 해제하고 그 신호로 정확도를 판정하지 않는다(짝짓기 오류를 놓친다).
   `stray`·`weak-orphan`은 출력 줄 판정이 아니라 **별도 진단 사건**으로 집계한다.

## 4. S1 — 재생 기록 형식

### 4.1 기록 위치와 시점

기록 경계는 **`OverlaySchedule` API 호출과 `Pump`**다. 소켓 계층(`BattleLogger`)의 프레임 로그가 아니다.

- 생산 시점: `Push`에서 실제 생성된 Item, `Reset`/`Discard`의 세대 변경, `Screen`, `Init`, `FallBackToModel`, `ForgetBanner`, `Mark`.
  Item의 필드는 기록 때문에 다시 읽지 않고 실제 생성된 값을 복사한다.
- 소비 시점: Pump의 시작·종료·모드, 드레인한 모든 입력 순서와 검사한 모든 신호를 남긴다. 낡은 세대로
  폐기할 입력도 포함한다. 게이트 경로의 세대 확인→드레인→신호→상한→해제와 모델 경로의 드레인→해제를 구분한다.
- `Diagnostics.Replay`(신규, 기본 false)는 TraceTiming·ScreenProbe와 독립이다. v1은 실행 중 켜기를 지원하지 않는다.
  플러그인 실행마다 고유한 로컬 파일 하나에 여러 판을 연속 기록하고, 판 전환 때 덮어쓰거나 잘라내지 않는다.
  파일명에 uid·방 번호를 쓰지 않고 출력 경로를 gitignore 대상으로 둔다.
- 파일 용량 상한에 도달하면 게임을 막거나 앞부분을 덮어쓰지 않고 계측을 종료한다. 정상 경계에서 닫지 못한
  기록은 불완전으로 표시한다. 기존 진단 파일을 자동 삭제하지 않는다.

### 4.2 스키마 (필수 필드)

버전 헤더가 없으면 엄격 재생은 거부한다. 실제 파일은 UTF-8 JSONL이며, 아래는 논리 필드 목록이다.
모든 레코드에 `type`과 파일 전체에서 1부터 연속하는 `recordSeq`를 둔다. 선택 필드는 null로 명시한다.

```text
header: format=1, schedulerVersion, start(clean|mid)
init: nowUs, speedBits, enabled, maxLagUs, gating, gen
tick: nowUs, speedBits, clock(unity|fallback)
item: itemId, signal(line|advance|page|sync|clear), kind, group, units,
      detail, round, receivedUs, speedBits, gen, parentControlId
control: controlId, op(reset|discard), genBefore, genAfter, nowUs
signal: signalId, atUs, gen, kind, pip, on, instanceId
env: op(hook-success|hookfail|hook-disabled|fallback|forget-banner), nowUs
pump-begin: pumpId, nowUs, mode(gated|model), observedGen
take: pumpId, itemRef
signal-take: pumpId, signalRef
decision: pumpId, itemRef, causeRef, leaderItemRef, action, reason,
          dueUs, floorUs, resolvedUs, releasedUs, output(none|line|page|clear)
diagnostic: kind, itemRef, signalRef
pump-end: pumpId, observedGen, overlap
mark: nowUs
end: reason(normal|unload|incomplete), dropped, ioFailed, overlap,
     previousCount, previousBytes, sha256
```

- 번호 공간은 recordSeq/itemId/signalId/controlId/pumpId별로 독립이다. 스케줄러 내부 Seq와 혼동하지 않는다.
- 모든 입력 유형에 Item 필드를 빠짐없이 기록한다. Sync의 opcode·길이는 현재 Units/Round 값 그대로 보존한다.
  receivedUs와 atUs는 실제 Item·PendingSignal의 내부 시각이다. 벽시계로 대체하지 않는다.
  speedBits는 float32 비트값으로 보존해 반올림을 피한다.
- instanceId는 포인터를 기록용 번호로 치환한 값이다. 같은 포인터의 동등성을 유지하고 도중에 표를 초기화하지 않는다.
  포인터 재사용을 임의로 새 객체로 판정하지 않는다. 원시 주소는 파일에 쓰지 않는다. 표 용량 초과는 계측 중단으로 처리한다.
- 텍스트는 생략하고 itemId로 출력과 비교한다. 전투 텍스트·uid·방 번호·본문·자유 입력 mark를 저장하지 않는다.
- 재생 어댑터는 기록된 Item·PendingSignal 값과 내부 시각을 그대로 주입한다. nowUs를 float 초로 바꿔 Tick을
  재호출하지 않는다. v1은 Tick 산출 자체가 아니라 **산출된 내부 시계에 대한 스케줄러 결정**을 검증한다.
- `take`·`signal-take` 목록으로 각 Pump의 입력 경계를 복원한다. 미래 Pump 소속 입력을 미리 넣지 않는다.
  입력 생산이 Pump 도중이었더라도 실제 필드 값과 소비 순서를 보존한다.
- 재생 어댑터는 입력·상태 주입만 담당한다. 게이트·모델 판단은 재구현하지 않고 실제 플러그인 소스를 링크한다.
- **decision·diagnostic은 재생 입력으로 쓰지 않는다.** 실제 계산 결과와 비교하는 기대값이다.
- 스케줄러 로직을 바꾸면 스케줄러 버전 상수를 올린다. 재생기는 모르는 버전을 거부한다.
- 기존 로그(`battle-log.txt`, `[timing]`, `[probe]`)는 재생 입력이 아니다. 참고용 "불완전 재생" 모드에서만 쓴다.

### 4.3 세대·폴백

- Reset의 세대 변경과 내부에서 생성한 Clear를 각각 기록하고 parentControlId로 연결한다.
  재생은 세대 변경과 기록된 Clear를 한 번씩 적용한다. 공개 Reset() 재호출로 Clear를 중복 생성하지 않는다.
  Discard는 세대만 변경한다.
- 현재 IsStale은 드레인·해제 도중에도 현재 세대를 다시 읽는다. v1은 **Reset/Discard의 세대 변경이 Pump 또는
  FallBackToModel 처리와 겹친 파일을 엄격 모드에서 거부**한다. 계측은 변경 구간과 처리 구간의 겹침을 지속 상태로
  남긴다. 변경이 구간 시작을 걸쳐 진행 중인 경우도 검출하며 시작·종료 세대 비교만으로 충분하다고 보지 않는다.
- 게임 처리에 잠금을 걸거나 세대 변경을 다음 Pump로 미뤄 경합을 없애지 않는다. 그것은 표시 정책 변경이다.
  경합 파일은 진단용으로 보존하고 기대 drop 결정을 재생 지시로 사용하지 않는다. 검사 지점별 순서 재현은 후속 확장이다.
- FallBackToModel 호출은 env 입력이며, 계산된 예약과 큐 이동은 decision 기대값이다.

### 4.4 시작 상태와 결정 표현

- start=clean은 새 프로세스의 첫 초기화와 모든 스케줄러 상태 변경 전부터 기록한 경우에만 허용한다.
  Reset만으로 clean이 되지 않는다. 배너 학습은 씬 수명이고 Init도 모든 정적 상태를 비우지 않는다.
  중간 시작은 mid로 남겨 엄격 모드에서 거부한다. v1은 임의 상태 스냅샷 복원을 지원하지 않는다.
- decision의 itemRef는 항상 대상 입력이다. causeRef는 원인 화면 신호가 있을 때 채우고, 신호 하나가 선행 항목
  여러 개를 풀면 각 항목에 같은 원인 ID를 연결한다. 그룹 동반 출력은 leaderItemRef로 선행 결과와 연결한다.

| action | 의미 | reason 예 |
| --- | --- | --- |
| schedule | 모델 예약 계산 | model |
| transfer | 게이트 큐에서 모델 예약 큐로 이동 | fallback |
| resolve | 게이트 해결, 아직 출력되지 않을 수 있음 | own-signal, later-signal, window-close, cap, budget |
| release | 큐에서 꺼내 처리 | model, sync, own-signal, later-signal, window-close, cap, budget, group-follow, counter-sync |
| drop | 세대 불일치 등으로 폐기 | stale, reset, discard |

실제 분기마다 사유를 배정하고 최소 사례를 검증한다. 모델 기한 출력과 동기화에 의한 앞당김을 구별한다.
Advance·Sync의 release는 output=none이다. 게이트 해결 시각과 실제 출력 시각을 분리한다.
시간 상한과 큐 예산은 현재 Resolution.Cap을 유지하고 **계측 사유에서만** 구분한다.
dueUs/floorUs는 실제 예약 값이 존재할 때만 기록하고, 해당 없는 단계는 null이다.

### 4.5 파일 완전성

- 순번은 writer 큐 삽입 시도 전 부여한다. 생산자의 예약·삽입 순서가 어긋날 수 있으므로 순번순 기록을 보장한다.
  큐 초과 시 게임 스레드는 기다리지 않고 dropped를 증가시킨다. 복구할 수 없는 누락은 불완전 처리한다.
- 쓰기 실패는 지속 상태로 남기며 후속 쓰기 성공으로 정상 상태로 되돌리지 않는다.
- end는 생산 중단과 진행 중인 계측 호출 종료 후 writer가 앞선 큐를 처리하고 작성하는 마지막 레코드다.
  sha256은 end 이전 전체 바이트(줄바꿈 포함), previousCount는 end 이전 레코드 수다. writer가 계산한다.
  종료 제한 시간 안에 flush가 끝나지 않으면 정상 end를 보장하지 않으며 파일 재검사에서 거부한다.
- 엄격 모드는 헤더·유일한 최종 end·연속 순번·개수·바이트 수·해시·ID 참조·Pump 쌍을 검사한다.
  mid, dropped>0, ioFailed, overlap, incomplete 종료, end 없음, 순번 누락/중복, 깨진 JSON, 미완성 Pump는 거부한다.
- 종료 시 대기 입력이 남은 것은 파일 유실과 다르다. 완전한 접두 구간은 재생하되 남은 입력의 최종 출력은 미관측으로
  보고하며 종료 이후의 출력을 추측하지 않는다.
- **(2026-09-26 변경) 임시 end.** 실게임 두 번의 종료에서 `ProcessExit`가 오지 않아 최종 end를 쓸 기회가 없었다.
  그래서 writer는 쓸 때마다 파일 끝에 `reason=checkpoint`인 end를 붙이고, 다음에 쓸 때 그 자리부터 덮어쓴다.
  파일에는 언제나 end가 하나만 있고 그것이 마지막 레코드다. 엄격 모드는 `checkpoint`를 "종료 경계를 확인하지
  못한 완전한 접두 구간"으로 받아들이고 그렇게 표시한다. 정상 종료 경로가 불리면 `normal`·`unload`로 바뀐다.

## 5. S2 — 건강 요약

`TraceTiming`과 무관하게 카운터를 유지한다. 로컬 게임 관측 번호를 사용하며 게임 방 번호는 기록하지 않는다.

- Reset에서 이전 활성 관측을 아직 닫지 않았다면 요약하고 새 관측을 연다.
- 기존 전투 이탈 판정·언로드 중 첫 종료 사건만 활성 관측을 닫고 BepInEx 로그에 1줄을 쓴다.
  모든 씬 변경을 전투 이탈로 보지 않는다. 뒤따르는 Reset·언로드는 닫힌 관측을 중복 출력하지 않는다.
- Reset 없이 중간 접속으로 전투를 관측하면 partial 관측을 열고 요약에도 명시한다.
- 카운터는 새 관측을 열 때 초기화한다. 세대와 관측 번호의 대응을 유지하고, 닫힌 관측의 후행 입력을 다음 게임에
  섞지 않는다. 후행 수는 별도 실행 진단으로 센다. 스레드 간 갱신·스냅샷은 원자 연산 등으로 보호한다.
- 현재 후킹 상태는 새 관측에도 승계한다. 상태 변화 횟수와 현재 상태를 구별한다.

포함할 것:

- 게이트 생성·해결·실제 출력 수(kind와 `signal`·`late`·`cap`·`budget`별), 종료 시 미해결 수
- 신호 수신 수(kind별) / 진단 사건: `stray`, `consume`, `weak settle`, `weak-orphan`, `extra-strike`, `outside`, `counter-held`
- 후킹 상태: 설치 성공, 오류로 자체 비활성, 모델 폴백 여부
- 계측 누락: 이름·경로·줄 예산 초과, 후보 버퍼 덮어쓰기, 문자열 표 초과, 분석 작업 드롭, 세대 불일치 신호 폐기, 기록 큐 드롭
- 시계·부하: Unity/폴백 시계 사용 수, 고정 구간의 speed 분포, 대기 큐 최대치, 내부 시계 기준 최대 표시 대기

writer의 드롭·I/O 실패는 요약 뒤에 확정될 수 있다. 게임 요약 시점 수치는 잠정이며 최종 파일 상태는 end와
별도 실행 종료 진단에 남긴다. 게임 요약 1회 조건 때문에 뒤늦은 쓰기 실패를 숨기지 않는다.

**해석 규칙(단독 단정 금지):** "게이트>0 & 신호=0"이면 후킹·이름 변경 후보, "신호>0 & 전부 stray"면 짝짓기 후보.
신호 0건만으로 이름 변경을 단정하지 않는다.

## 6. S2 — 후보 링 버퍼 (진단 옵션에서만)

`Diagnostics.ScreenProbe=true`일 때만 동작한다.

- 버퍼 항목: (마스킹된 이름/경로 문자열의 내부 번호, 내부 시각, on/off, 분류 태그). 문자열은 **전환 시점에** 예산 안에서
  복사한다. 전역 string.Intern은 사용하지 않는다. 원시 포인터를 보관하지 않으며 재생 신호용 익명 인스턴스 표와 별개다.
- 문자열 테이블은 항목 수·총 문자 수·개별 경로 길이 상한을 둔다. 버퍼·분석 작업이 참조하지 않는 문자열은 회수한다.
  상한 초과 시 새 후보를 버리고 횟수를 센다. 링 버퍼 크기만 제한하고 문자열이 무한히 남는 구조는 금지다.
- gap 구간 정의(수정 반영):
  - 항목 해제: [수신, 실제 해제] — 기본 대상은 late/cap/budget. 정상 weak 공개는 별도 종류이며 누락으로 단정하지 않는다
  - `stray`: 대응 입력이 없으므로 신호 전후의 제한된 시간창. 후속 구간을 기다리는 작업에도 용량 상한 적용
  - `weak-orphan`: [창 열림, 창 닫힘]
- 겹치는 구간은 분석용으로 병합하되 원래 사건 ID를 보존한다. 신호 콜백에서 버퍼 전체를 반복 검색하지 않는다.
  프레임당 검색 항목 수·대기 분석 수·보고 후보 수를 제한하고 상위 후보만 1~2줄로 보고한다.
- 켜짐·꺼짐 빈도를 구분한다. 구간에서 1~2회 등장했다는 사실은 후보 선정 근거이며 의미의 증명이 아니다. **자동 채택 금지.**
- 버퍼가 구간 앞부분을 덮어썼거나 수집·분석 예산이 초과됐다면 incomplete로 표시한다. 해당 구간의 빈도로
  “신호 없음”을 단정하지 않는다. 씬·판 경계를 넘은 구간도 구분한다.
- 각 용량·시간창·분석 예산을 구현 상수로 명시하고 경계 사례를 검증한다. 크기·덮어쓰기·드롭 수를 건강 요약에 포함한다.

## 7. S3 — 재생기

- 위치: `tools/harness` 확장. `OverlaySchedule`은 Unity에 의존하지 않으므로 그대로 링크한다.
- 입력: v1 재생 기록. 먼저 파일 전체의 완전성을 검사한다. 각 재생은 새 프로세스 또는 동등하게 검증된 완전 초기 상태에서 시작한다.
  decision·diagnostic은 기대값으로만, mark는 참고용으로만 사용한다.
- 출력: 입력별 예약·해결·출력·폐기의 순서, 내부 시각, 사유를 기록의 decision과 비교한다.
- **지표 2축 분리:**
  - 해제 근거: signal/late/cap/budget와 모델 기한·동기화 경로를 구분하고 4.4절의 세부 사유를 표시한다.
  - 화면 정확도: **독립 증거가 있는 사건만** 조기/지연/일치. 증거에는 itemId 대응, 공개 시점 구간,
    내부 시계와의 정렬 근거·오차를 붙인다. 없거나 오차 구간이 겹치면 판정 불가다. F10 mark는 병기만 한다.
- `stray`/`weak-orphan`/`consume`은 진단 이벤트 집계로 별도 출력.
- 기존 하네스(`tools/harness/Sched.cs`)는 계속 통과해야 한다.
- 다른 스케줄러 버전으로 변경 효과를 비교하는 것은 별도 비교 모드다. 과거 기록의 엄격 재현 성공으로 표시하지 않는다.

## 8. 진행 순서와 검증

1. S1 기록 형식·writer·어댑터 → 기록 on/off의 기존 하네스 출력 일치. off의 레코드 생성 부재와 on의 비용·파일 크기·큐 사용량 확인.
2. S2 건강 요약 → 모의 신호 카운터와 Reset→전투 이탈→언로드의 종료 중복 방지, 다중 판·중간 접속 검증.
3. S2 후보 버퍼 → 문자열 회수·덮어쓰기·예산 초과·대량 동시 gap 검증 뒤 진단 게임에서 수집 완전성과 보고 확인.
   후보가 없으면 없는 이유를 보고한다. 한 판에서 신호 발견을 보장하지 않는다.
4. S3 재생기 → 합성 기록으로 기존 하네스 재현 + 실제 기록 1개로 대조.
5. 그 뒤 별도 작업으로: 미확인 신호 조사(몬스터 이동, 파파라·렌 타격, 다중 주사위 잠금 수) → P3 판단.

각 단계는 앞 단계의 완료 기준을 깨지 않아야 한다.

추가 필수 회귀 사례:

- Advance의 kind/units/group/speed, Page 그룹, 다중 주사위 detail, 반격, 같은 그룹 동반 출력
- Reset의 Clear 1회 처리, Discard, 배너 학습 후 다음 판을 포함한 연속 기록
- 모델 기한·Sync 앞당김·게이트 해결과 출력 간격·시간 상한·큐 예산·FallBackToModel 예약 이동
- Pump/폴백 중 Reset·Discard 경합: 게임 동작 유지, 엄격 재생은 명시적으로 거부
- 큐 드롭·중간 배치 쓰기 실패·파일 잘림·end 누락·순번 누락/중복·해시 불일치·미완성 Pump 거부
- 중간 시작 거부, 정상 종료 때 대기 입력은 미관측으로 표시, 내부 시각·속도의 정확한 보존

## 9. 열린 결정 (이 작업 범위 밖)

**S0 — 신호 유실 정책.** 별도 결정으로 남긴다. 선택지:

1. 현행: 상한(60초)·큐 예산(4096)에서 해제. 로그가 멈추지 않지만, 화면이 그보다 오래 밀리면 조기 표시 가능.
2. **확인 신호가 없으면 결과를 계속 보류.** 상한으로 인한 조기 공개는 막지만, **잘못 짝지어진 신호가 다음 결과를
   여는 문제까지 막지는 못한다** — "절대 조기 없음"이 아니다. 후킹·이름이 깨지면 그 판 로그가 영영 멈춘다.
3. 현행 + 화면이 오래 밀렸을 때 대기 표시. 표시는 **상태 안내일 뿐** 강제 해제 정책의 안전성을 바꾸지 않는다.

README·검증 문서의 문구는 결정 뒤에 맞춘다. 현재 문구는 "관측 표본에서 조기 0건"으로 정정한다.

## 10. 참고 — 확인된 현재 구현

- `UI/OverlaySchedule.cs` — 입력(`Line`/`Advance`/`Page`/`Sync`/`Reset`/`Discard`), 시계(`Tick`), 모델 경로 `Pump`, `MaxWaiting=4096`.
- `UI/OverlaySchedule.Gate.cs` — 게이트/장부/장벽, `GateCapUs=60s`, `DiceTailUs`, `FallBackToModel`.
- `UI/OverlaySchedule.Turns.cs` — 라운드 팁·차례 배너 학습.
- `UI/ScreenProbe.cs` — `SetActive` 후킹, 이름 분류·예산·자체 비활성.
- `UI/ScreenRecorder.cs` — 전환 경로 기록·마스킹(`Mask` 휴리스틱).
- `UI/FramePump.cs` — 매 프레임 `Tick`→`Pump` 순서.
- `Log/TimingTrace.cs` — 현재 사람용 진단 로그. 재생 기록과 별개로 유지.
- `Log/LogFileWriter.cs` — 소켓 스레드를 막지 않는 큐·전용 스레드 writer 선례.

## 11. 구현 노트 (2026-09-26)

### 파일

| 파일 | 내용 |
| --- | --- |
| `UI/OverlaySchedule.Replay.cs` | 기록 시점 계측, 경합 검출, 레코드 형식(`FormatRecord`), `SchedulerVersion`, `Shutdown` |
| `Log/ReplayWriter.cs` | 유한 큐·전용 스레드 JSONL writer. 순번·해시·end 작성 |
| `UI/ScheduleHealth.cs` | 게임(관측)별 건강 요약 |
| `UI/ScreenCandidates.cs` | 후보 링 버퍼·문자열 표·구간 분석(Unity 비의존) |
| `tools/harness/ReplayAdapter.cs` | 재생 어댑터. 하네스에만 컴파일되는 `OverlaySchedule` 부분 클래스 |
| `tools/harness/Replay.cs` | 엄격 검사와 재생기 |
| `tools/harness/ReplayTests.cs`·`HealthTests.cs` | 회귀 사례 |

### 사용법

- `Diagnostics.Replay = true`(오버레이가 켜져 있어야 한다). 다음 실행부터
  `BepInEx/plugins/AstralPartyBattleLog/replay/replay-<날짜-시각>-<pid>.jsonl`에 실행 하나를 이어서 쓴다.
- 재생: `dotnet run -c Release --project tools/harness -- replay <파일>`. 종료 코드 0 일치, 1 불일치, 2 엄격 거부.
  `--lenient`는 검사에 실패해도 앞부분을 참고 재생하며 "불완전 재생(참고용)"으로 표시한다.
- 하네스를 인자 없이 돌리면 기존 사례에 더해 기록 on/off 비교, 새 프로세스 재생, 손상 파일 거부,
  writer 실패, 건강 요약, 후보 버퍼 사례를 모두 돈다.

### 명세에서 정한 세부

- **레코드 생성 위치.** 게임 스레드는 값만 담은 구조체를 큐에 넣고, 글자는 writer 스레드가 만든다. 기록이 꺼져
  있으면 구조체도 만들지 않는다(하네스 RP1이 생성 수 0을 확인).
- **순번.** writer 잠금 안에서 순번을 매기고 같은 잠금 안에서 큐에 넣는다. 버린 레코드도 순번을 소비해 파일에
  구멍으로 드러난다. 항목 레코드는 `Incoming`에 넣기 전에 남겨 `take`가 항상 뒤 순번이다.
- **쓰기 실패.** 한 번 실패하면 `ioFailed`가 끝까지 남고 계측을 멈춘다. 해시·개수·바이트는 실제로 쓴 것만 센다.
  조기 중단(쓰기 실패·용량 상한·인스턴스 표 초과)·유실이 있으면 end의 `reason`은 자동으로 `incomplete`다.
- **경합.** Reset/Discard는 진행 중 표시를 올린 뒤 횟수를 세고, Pump/FallBackToModel은 횟수를 먼저 읽고 진행 중
  표시를 읽는다. 처리 시작 직후에 시작한 변경도 종료 시점 횟수 비교로 잡힌다. **변경끼리 겹친 경우**(소켓 스레드
  Reset과 메인 스레드 Discard)도 기록 순서와 세대 순서가 어긋날 수 있어 `overlap`으로 둔다.
- **임시 end.** 게임을 끌 때 `ProcessExit`가 오지 않는다(2026-09-26 실게임 두 번, 항복 뒤 종료 포함). writer는
  약 100ms마다 모아 쓰고, 쓸 때마다 이전 임시 end 자리부터 데이터를 덮어쓴 뒤 새 `checkpoint` end를 붙이고
  파일 길이를 맞춘다. Pump 구간(`pump-begin`~`pump-end`) 한가운데서는 붙이지 않고 구간 뒤 레코드를 다음 쓰기로
  넘긴다. 해시는 이어서 계산한다(`GetCurrentHash`). 강제 종료 때 잃는 것은 마지막 쓰기 뒤의 약 100ms 분량이다.
  - 알려진 한계: `FallBackToModel`은 닫는 레코드가 없어, 그 결정 도중에 끊긴 checkpoint는 재생 불일치로 나온다.
    폴백은 실행당 많아야 한 번(후킹 실패·자체 비활성)이다.
- **종료.** 프로세스 종료(`ProcessExit`)는 세대를 바꾸지 않는다. 바꾸면 메인 스레드 Pump와 겹쳐 정상 종료 기록이
  모두 경합으로 거부된다. 언로드는 기존처럼 `Discard`한다. 종료는 진행 중 계측 호출을 1초까지 기다리고,
  writer flush를 2초까지 기다린다.
- **폐기 사유.** `drop`의 사유는 그 줄의 세대를 끝낸 변경(`reset`/`discard`)이고, 기록에 그 변경이 없으면 `stale`이다.
- **일반 줄의 해제 사유.** 모델 기한 = `model`, 동기화가 앞당김 = `sync`, 화면 신호가 앞당김 = `later-signal`
  (`causeRef`에 그 신호), 반격 전 5036 처리 = `counter-sync`, 큐 예산 = `budget`. 게이트 해제는 `resolve`의
  사유를 그대로 쓰고 `dueUs`/`floorUs`는 null, `resolvedUs`와 `releasedUs`를 따로 둔다.
- **FallBackToModel 결정**은 Pump 밖이라 `pumpId`가 null이다. 경합 여부는 end의 `overlap`에 남는다.
- **추가한 필드·종류.** `diagnostic`에 `pumpId`를 둔다. 진단 종류에 `banner-learned`, `stale-signal`(세대 불일치
  신호 폐기)을 더했다.
- **instanceId.** 포인터 0은 0으로 둔다(배너 미학습 판정이 `IntPtr.Zero` 비교라 뜻이 겹친다). 나머지는 1부터.
- **용량.** writer 큐 65,536건, 파일 512MiB, 인스턴스 표 65,536개.

하네스 기존 사례 전체를 기록하면 2,463건·약 288KB이고, writer 큐 최대 깊이는 120~135건이었다. 실게임에서는
프레임마다 `tick`·`pump-begin`·`pump-end` 3건이 기본으로 붙는다.

### 건강 요약

관측이 닫힐 때 BepInEx 로그에 한 줄을 쓴다.

```text
[health] game#2 start=reset end=left gates(made res:s/l/c/b shown:s/l/c/b open) dice=12 11/1/0/0 11/1/0/0 0 pk=… round=… turn=…
  | signals dice=30 window=16 hit=40 round=4 top=50 | diag stray=… consume=… weak-settle=… weak-orphan=… extra-strike=… outside=… counter-held=…
  | hook=installed mode=screen changes=0 moved=0 | loss name=… path=… line=… cand-overwrite=… str-table=… analysis-drop=… stale-signal=…
  replay=…(provisional) | clock unity=… fallback=… speed 1x=… maxQueue=… maxWait=…ms
```

(실제로는 한 줄이다.) `s/l/c/b`는 자기 신호(창 닫힘 포함)·늦은 경로·상한·큐 예산이다. `open`은 닫힐 때 아직
해결되지 않은 게이트 수다. `moved`는 모델 폴백으로 넘어간 미해결 게이트 수다. 실행 끝에는 `[health] run end`
한 줄에 관측 밖 입력 수(`trailing`)와 writer 최종 상태, 후보 버퍼 상태를 남긴다.

### 후보 버퍼 상수

| 상수 | 값 |
| --- | --- |
| 링 항목 수 | 16,384 |
| 문자열 표 | 4,096개, 총 262,144자, 경로 하나 200자(넘으면 뒤쪽을 남긴다) |
| `stray` 시간창 | 신호 ±3초 |
| 구간 최대 길이 | 90초(넘으면 뒤쪽만 보고 `clipped`) |
| 대기 분석 작업 | 32개(넘으면 버리고 `analysis-drop`) |
| 작업 하나의 사건 번호 | 16개(넘으면 `+N`) |
| 프레임당 검색 항목 / 보고 줄 | 4,096 / 2 |
| 보고 후보 | 구간에서 1~2회 바뀐 경로 상위 3개 |

보고 형식은 `[candidates] events=late:dice#3,weak:pk#4 span=…ms entries=… known dice=… window=… hit=… round=… top=…
complete|incomplete flags=… top=<경로> on1/off1 +120ms | …`다. `flags`의 `front-overwritten`·`overrun`·`budget`·`clipped`는
불완전, `boundary`는 씬·판 경계를 넘은 구간이다. 후보가 없으면 이유를 적고, 불완전 구간이면 "absence not proven"을 붙인다.

### 실게임 확인 (2026-09-26)

- 봇전 한 판(4라운드 중 항복, 1배속): 기록 77,461건·7.3MB, 순번·참조·Pump 쌍 이상 없음. 당시 빌드는 임시 end가
  없어 end가 빠졌고 엄격 재생은 거부됐다. 참고 재생에서 결정 1,035건이 모두 일치했다.
- 같은 판 `[health]`: 주사위 24(자기 신호 12·늦은 경로 12), PK 6·라운드 3 모두 자기 신호, 차례 23 중 22가 배너,
  1건은 이탈 때 미해결(`drop/discard` 1건과 같은 항목). 상한·큐 예산 해제 0, writer 유실 0.
- 파일 크기는 약 2MB/분이다(프레임마다 `tick`·`pump-begin`·`pump-end`). 512MiB 상한은 약 4시간 분량이다.

- 둘째 판(임시 end 빌드, 1배속): 145,851건·13.7MB, `checkpoint`로 닫혀 **엄격 재생 통과**(결정 972건 일치).
  `[health]`와 기록의 해결 사유 수가 일치했다. 최대 대기 40.1초는 신호 없는 몬스터 주사위가 뒤 PK 타격까지
  붙잡힌 것이었다(SIGNAL-GATING.md 1절 "창 열림"). 이 분석으로 표시 규칙을 바꾸고 `SchedulerVersion`을 2로 올렸다.
- 셋째 판(버전 2 빌드, 대부분 2배속): 91,446건·8.6MB, `checkpoint`로 닫혀 엄격 재생 통과(결정 1,130건 일치).
  늦은 경로 17건 중 4건이 PK 창 열림으로 풀렸고 최대 대기는 9.3초였다. `[health]`와 해결 사유 수가 일치했다.

### 비교 모드

`dotnet run -c Release --project tools/harness -- compare <파일> [--lenient]`. 다른 스케줄러 버전의 기록에 현재 규칙을
돌려 줄마다 공개 시각이 어떻게 바뀌는지 보고한다(7절 "별도 비교 모드"). 입력(수신·신호·시각)은 결정과 무관하게 기록
그대로라 가정 실험이 된다. 원래 자기 신호·창 닫힘으로 공개된 줄은 그 시각이 화면에 보인 순간이므로, 그보다 먼저
나가면 조기 표시로 센다. 엄격 재현 성공으로 표시하지 않는다.

### 남은 일

- 화면 정확도 판정에 쓸 독립 증거(공개 시점 구간)를 기록에 넣는 방법. 지금은 해제 근거만 검증한다.
- `Diagnostics.ScreenProbe`로 진단 게임을 돌려 후보 수집 완전성과 보고를 확인한다(8절 3단계 후반).
- 기록 켬 상태의 실게임 프레임 비용·파일 크기를 잰다.
