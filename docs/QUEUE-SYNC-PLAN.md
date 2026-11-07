# 대기열 공개 동기화 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 게임 처리 대기열의 맨 앞을 읽어, 오버레이 줄을 게임이 그 메시지를 처리하기 시작한 뒤에(결과 줄은 처리가 끝난 뒤에) 공개한다.

**Architecture:** IL2CPP 읽기(`Net/QueueReader.cs`)는 맨 앞 (CMDID, DOWNSN)과 목록 길이만 돌려준다. 판단(`Net/QueueTracker.cs`)은 모드가 받은 프레임의 도착 순서와 맨 앞을 맞춰 시작·끝 경계를 낸다. 스케줄러(`UI/OverlaySchedule.Queue.cs` + `Gate.cs`)는 펌프마다 그 경계의 스냅숏으로 줄을 공개한다. 1단계(Task 1~5)는 기록만, 2단계(Task 6~8)는 공개 규칙 적용, Task 9는 릴리스 준비다.

**Tech Stack:** C# (플러그인 net6.0, BepInEx 6 IL2CPP, Il2CppInterop 1.5.3), 하네스 net8.0 콘솔, Python 3 + pytest(분석 도구).

**Spec:** [docs/QUEUE-SYNC.md](QUEUE-SYNC.md). 실행하는 사람은 둘 다 읽는다.

## Global Constraints

- 사용자에게 보이는 출력·문서·주석·커밋 메시지는 한국어. 커밋 제목은 `<type>: 한국어 요약`, 함수·변수 이름 없이 사용자 영향 위주. 본문에 빌드·테스트 결과를 쓰지 않는다. 끝 줄은 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- 주석은 규칙의 근거(실측·디컴파일)나 직관에 어긋나는 이유가 필요한 곳에만 쓴다. 무엇을 하는지 설명하는 주석은 쓰지 않는다.
- IL2CPP 등록 금지: `ClassInjector.RegisterTypeInIl2Cpp`, IL2CPP 이벤트 `+=`, `WrapToIl2Cpp`. Harmony 패치를 새로 걸지 않는다.
- `il2cpp_` 호출은 `Net/QueueReader.cs` 한 파일에만 둔다. 대기열 항목에서 `<CMDID>k__BackingField`·`<DOWNSN>k__BackingField`와 목록의 `_items`·`_size` 밖은 읽지 않는다.
- `Net/QueueTracker.cs`와 `UI/OverlaySchedule.Queue.cs`는 Unity·IL2CPP를 참조하지 않는다(하네스가 BepInEx.Core만 참조).
- PvP에서 기능을 끄지 않는다. 판단이 갈리면 늦게 공개하는 쪽을 고른다.
- 즉시 처리 9종: 5004, 5096, 5238, 5008, 5150, 5246, 5328, 1001, 5012.
- 결과 줄: 그룹을 연 프레임의 cmd가 1040(`UpdateHeroAttr`), 1013(`LandBuffs`), 1096(`HeroSkillMoveEffect`), 5044(`MoveAgain`).
- 걷기 끝 기준을 갖는 메시지(열린 메시지): 5022, 1096, 5044 → 뒤따르는 5028. 경계 cmd: 5022, 1026, 1007, 1117, 1015, 1016.
- 재연결 신호: 5008·5012 수신, `ErrId` ≠ 0인 프레임, 게임 프레임을 낸 소켓의 끊김(다음 5008까지 기준점 없음).
- 도착 시점은 펌프 틱(FramePump가 돈 횟수). 빈 목록은 6틱 연속 + 도착 뒤 3틱. 대기 목록 상한 4096, 재해석 주기 60프레임.
- 추적이 멈춘 뒤 지금 방식으로 돌아가기까지 60초(스케줄러 시계, 멈춤을 처음 본 펌프부터).
- 하네스 성공은 종료 코드 0이다(각 묶음이 `=== … 전부 통과 ===` 배너를 찍는다). 실패하면 종료 코드가 실패 수다.
- 게임 자산·디컴파일 결과는 커밋하지 않는다(`ref/`는 gitignore). 한글이 든 문자열 치환은 Bash가 아니라 Edit 도구로 한다(cp949).
- 원격 푸시와 릴리스 게시는 사용자 허락 없이 하지 않는다. 스킬 저장소(`E:\projects\claude-skills`)만 전역 규칙대로 커밋·푸시한다.

## Review Focus

1. **로그인 전·로비에서 읽기:** 핫업데이트 어셈블리가 아직 없거나 `NetManager._inst`가 0이면 예외 없이 "못 읽음"이어야 한다. Task 5에서 LogOutput에 대기열 관련 예외·경고가 없고, 로그인 전에는 상태가 꺼짐으로 남는지 확인한다(로비에서 빈 목록으로 맞음이 되는 것은 정상이다).
2. **판 이탈·항복:** 게임이 대기열을 비우면(`ClearRPCMsgData`) 남은 항목이 모두 끝난 것으로 처리돼야 한다. Task 1b QT12.
3. **봇 차례 대량 대기:** 60초 넘게 기다리는 게이트 줄은 대기열 규칙 적용 중에 상한에 걸리지 않고, 추적이 60초 넘게 멈추면 지금 방식(상한 포함)으로 돌아간다. Task 6 QS5.
4. **재연결:** 방 동기화(5008)·퇴장(5012)·오류 프레임·소켓 끊김 뒤에는 기준점 뒤에 도착한 항목과 맞을 때까지 경계를 멈춘다. Task 1b QT6·QT7·QT15, Task 6 QS14.
5. **도착 직후의 빈 목록:** 도착한 지 3틱이 안 된 항목은 빈 목록이어도 끝난 것으로 보지 않고, 빈 목록은 6틱 연속이어야 한다. Task 1b QT3.

---

### Task 1: 처리 추적기

**Files:**
- Create: `Net/QueueTracker.cs`
- Create: `tools/harness/QueueTests.cs`
- Modify: `tools/harness/harness.csproj` (`Net/QueueTracker.cs` 링크), `tools/harness/Program.cs` (`fails += QueueTests.Run();`를 `HealthTests.Run()` 앞에)

**Interfaces:**
- Produces (namespace `AstralPartyBattleLog.Net`):
  - `enum QueueRead : byte { Unreadable, Empty, Head }`
  - `readonly struct QueueObservation { QueueRead Kind; int Cmd; long Down; int Size; }` + `static readonly QueueObservation Unreadable`, 생성자 `(QueueRead kind, int cmd = 0, long down = 0, int size = 0)`
  - `readonly struct QueueSnapshot { bool Live; long StartedThrough; long DoneThrough; long LastDrained; long OpenDice; }` — 도착 번호는 1부터, 0은 "없음". `OpenDice`는 끝 기준이 아직 안 정해진 5022의 도착 번호(없으면 0).
  - `readonly struct QueueHealth { long Frames, LiveFrames, Matched, Unmatched, SuspendReconnect, SuspendSocket, SuspendUnmatched, Unreadable, Overflow; int MaxSize; int MaxWaitFrames; }` — `Frames`는 `Apply`가 불린 프레임 수, `LiveFrames`는 그중 `Apply`를 마친 뒤 Live였던 수. `MaxWaitFrames`는 (처리 시작 프레임 − 도착 프레임)의 최대.
  - `static class QueueTracker`:
    - `const int EmptyMarginFrames = 3; const int MaxPending = 4096;`
    - `void Configure(bool enabled)`, `void Disable(string reason)`(이번 실행 동안 영구), `bool Enabled { get; }`
    - IO 스레드: `long Arrive(int cmd, long down)` — 즉시 처리 9종·비활성이면 -1. 5008이면 재연결 신호도 건다. `void SocketDropped()`
    - `void NoteGroup(long group, long arrival, int cmd)`, `bool TryRefOf(long group, out long reference, out int cmd)` — arrival < 0이면 false. 그룹 표는 잠금으로 보호하고 8192개를 넘으면 오래된 절반을 버린다.
    - 메인 스레드: `void BeginFrame(int frame)`, `void Apply(in QueueObservation obs)`, `QueueSnapshot Snapshot()`, `int DrainLinks(List<(long Dice, long End)> into)` — 지난 호출 뒤 새로 정해진 주사위 끝 기준만.
    - `QueueHealth Health { get; }`, `internal void ResetForTest()`

- [ ] **Step 1: 시험 작성** — `QueueTests.Run()`에 아래를 넣고 묶음 끝에 `=== 대기열 추적 전부 통과 ===`/`=== 대기열 추적 실패 N건 ===`을 찍는다. 각 시험은 `ResetForTest(); Configure(true); BeginFrame(…)`으로 시작한다. `H(cmd, down)`은 `new QueueObservation(QueueRead.Head, cmd, down, 1)`, `E`는 Empty.
  - QT1 맞추기: Arrive (1015,100)(1040,99)(5028,105) → Apply(H(1040,99)) → Started=2, Done=1, Live.
  - QT2 감소 순번: Arrive (5040,278)(1007,273)(1040,274) → Apply(H(1007,273)) → Started=2, Done=1.
  - QT3 빈 목록 여유: 프레임 10에 Arrive 1건 → 프레임 12 Apply(E) → Done=0 → 프레임 13 Apply(E) → Done=1.
  - QT4 같은 쌍: Arrive (1040,5)(1040,5) → Apply(H(1040,5)) → Started=1, Done=0.
  - QT5 어긋남: Live 상태에서 Apply(H(9999,1)) → Live=false, Unmatched=1, SuspendUnmatched=1 → 대기 항목과 맞는 H → Live.
  - QT6 방 동기화: Live 상태, Arrive (1040,1)(5028,2) → Arrive(5008,3)==-1, Live=false → Apply(H(5028,2)) → Live=false → Arrive(1015,4) → Apply(E)(3프레임 뒤) → Live=false → Arrive(1040,5) → Apply(H(1040,5)) → Live, SuspendReconnect=1.
  - QT7 소켓 끊김: QT6과 같되 5008 대신 `SocketDropped()`, SuspendSocket=1.
  - QT8 주사위 끝 기준(괄호 안은 도착 번호): Arrive 5022(1) 1019(2) 1002(3) 5028(4) → Apply → DrainLinks에 (1,4). Arrive 5022(5) 1026(6) → Apply → (5,5). Arrive 5022(7) → Apply → Snapshot.OpenDice=7. Apply 없이 Arrive 5022(8) → Snapshot.LastDrained=7.
  - QT1~QT7·QT13의 괄호는 (cmd, down)이다. 도착 번호는 즉시 처리 9종을 빼고 1부터 센다. "Live 상태"는 시작 직후 `Apply(E)`로 만든다.
  - QT9 즉시 처리: 9종 각각 Arrive == -1. `NoteGroup(10, -1, 5150)` → `TryRefOf(10, …)` false. `NoteGroup(11, 3, 1040)` → true, (3, 1040).
  - QT10 비활성: Configure(false) → Arrive == -1, Apply 뒤 Live=false. Configure(true) 뒤 Disable("x") → Configure(true)를 다시 불러도 Enabled=false.
  - QT11 상한: Apply 없이 Arrive 5000건 → Apply(H(맨 끝 항목)) → Overflow=904, Done=4999, Started=5000.
  - QT12 판 이탈: Arrive 30건 → Apply(H(5번째)) → 3프레임 뒤 Apply(E) → Done=30, Started=30.
  - QT13 못 읽음: Live 상태에서 Apply(Unreadable) → Live=false, Unreadable=1 → 맞는 H → Live(재연결 신호가 없으므로 앞 항목과 맞아도 된다).
- [ ] **Step 2: 실행해 실패 확인** — `dotnet run --project tools/harness/harness.csproj` → QueueTracker가 없어 빌드 실패.
- [ ] **Step 3: `Net/QueueTracker.cs` 구현** — 규칙은 spec "처리 추적". 도착은 `ConcurrentQueue`에 넣고 `Apply` 첫머리에서 대기 목록(List)으로 옮기며 주사위 끝 기준을 정한다. 도착 프레임은 `BeginFrame`이 `Volatile.Write`한 값을 `Arrive`가 읽는다. 맞음이 되는 조건: H가 맞고 (재연결 신호 뒤라면) 그 항목 번호 > 신호 때의 마지막 도착 번호, 또는 E이고 재연결 신호가 걸려 있지 않음. `TimingTrace.Enabled`이면 spec "기록"의 `queue head/empty/unmatched/live` 줄을 쓴다(`f=`는 현재 프레임).
- [ ] **Step 4: 실행해 통과 확인** — 같은 명령, 종료 코드 0, `=== 대기열 추적 전부 통과 ===`.
- [ ] **Step 5: 커밋** — `feat: 게임이 메시지를 처리하는 순서를 따라가는 추적 로직 추가`

### Task 1b: 추적기 보강 (설계 검토 반영)

Task 1 커밋 뒤 설계 검토에서 바뀐 규칙(spec "처리 추적"의 틱·빈 목록 6틱·걷기 끝 기준 표·추적 상태 셋·재연결 신호)을 반영한다.

**Files:**
- Modify: `Net/QueueTracker.cs`, `tools/harness/QueueTests.cs`

**Interfaces (Task 1에서 바뀌는 것):**
- `enum QueueMode : byte { Off, Live, Frozen }`
- `QueueSnapshot { QueueMode Mode; long StartedThrough; long DoneThrough; }` — `Live`·`LastDrained`·`OpenDice`는 없앤다. Frozen이면 두 경계는 멈춘 순간의 값이다.
- `long Arrive(int cmd, long down, int err)` — `err != 0`이면 재연결 신호(사유 `error`). 5012도 신호(사유 `exit`).
- `void SocketDropped()` — 기준점 없이 멈추고, 다음 5008 도착 때 그때의 마지막 도착 번호를 기준점으로 둔다.
- `void BeginFrame(int unityFrame)` — 틱을 1 올리고, `unityFrame`은 기록 줄의 `f=`에만 쓴다. 도착은 틱으로 찍는다.
- `int DrainLinks(List<(long Opener, long End)> into)` — 열린 메시지 표 {5022, 1096, 5044} → 5028. 열린 메시지가 여럿이면 같은 5028이 모두 닫는다.
- `QueueHealth { long Frames, LiveFrames, FrozenFrames, Matched, Unmatched, Unreadable, Overflow, FreezeUnmatched, FreezeUnreadable, FreezeReconnect, FreezeExit, FreezeError, FreezeSocket; int Size; int HeadWaitTicks; }` — 멈춤 사유 수는 이미 멈춘 상태에서 온 신호도 센다. `Size`는 마지막으로 읽은 대기열 길이(빈 목록·못 읽음이면 0), `HeadWaitTicks`는 지금 처리 중인 항목의 (처리 시작 틱 − 도착 틱)(없으면 0). 판마다의 최대는 Task 7이 펌프마다 받은 값으로 낸다.
- `bool Live`·`QueueRead`·`QueueObservation`·`NoteGroup`·`TryRefOf`·`Configure`·`Disable`·`ResetForTest`는 그대로.
- 기록 줄: `queue state=<off|live|frozen> why=<사유> f=<프레임>`(기존 `queue live=` 대신), `queue link seq=<열린> end=<끝> f=<프레임>`.

- [ ] **Step 1: 시험 고치기·더하기** (QueueTests.cs)
  - QT3 → 틱 기준: 틱 10에 도착, 틱 11~15 Apply(E) 5번 → Done=0, 틱 16 Apply(E)(6번째 연속) → Done=1. 중간에 H가 끼면 연속 수가 0으로 돌아간다.
  - QT6 → 5008: 맞음에서 Arrive(5008) 직후 Mode=Frozen, Snapshot 경계는 멈춘 순간 값. 기준점 이하 항목과 맞으면 안의 경계는 나아가도 Snapshot은 그대로. 기준점 뒤 항목과 맞으면 Live, Snapshot이 현재 값. FreezeReconnect=1.
  - QT7 → 소켓: `SocketDropped()` 뒤에는 어떤 항목과 맞아도 Frozen. Arrive(5008) 뒤 도착한 항목과 맞으면 Live. FreezeSocket=1.
  - QT8 → 끝 기준 표: 5022(1) 1096(2) 1002(3) 5028(4) → (1,4)(2,4). 5044(5) 1026(6) → (5,5). 1096(7) 1007(8) → (7,7). 5022(9) 5022(10) → (9,9), 10은 열린 채로 남는다.
  - QT12 → 판 이탈: 30건 도착, H(5번째), 6틱 연속 E → Done=30.
  - QT13 → 못 읽음: 맞음에서 Apply(Unreadable) → Frozen(경계 유지), FreezeUnreadable=1 → 맞는 H → Live.
  - QT14 Frozen 동안 안의 경계 진행: Frozen에서 H로 안의 Started가 나아가도 Snapshot은 멈춘 값, Live가 되면 현재 값.
  - QT15 퇴장·오류: Arrive(5012) → Frozen, FreezeExit=1. Arrive(1040, d, err: 3) → 번호를 받고 Frozen, FreezeError=1, 기준점은 그 번호.
  - QT16 빈 목록으로 풀림: 어긋남·못 읽음으로 멈춘 뒤 6틱 연속 E → Live. 재연결 신호로 멈췄으면 E로 풀리지 않는다.
  - QT17 건강: Frames·LiveFrames·FrozenFrames·Matched. 도착 틱 2, 처리 시작 틱 7인 항목이 맨 앞인 동안 HeadWaitTicks=5, Size=관측 값. 빈 목록이 되면 둘 다 0.
  - QT18 이미 멈춘 상태의 신호도 센다: 어긋남으로 Frozen → Arrive(5008) → FreezeReconnect=1.
  - QT19 그룹 표: 같은 그룹을 여러 번 적어도 내부 순서 큐가 늘지 않고, 8193번째 그룹에서 오래된 절반이 지워진다.
  - QT20 맞음에서 `Disable` → Mode=Off, Enabled=false.
- [ ] **Step 2: 실행해 실패 확인.**
- [ ] **Step 3: 구현** — 위 인터페이스. Task 1 리뷰의 사소한 지적도 함께 고친다: 그룹 순서 큐가 같은 키로 늘지 않게, 기록 쓰기는 잠금 밖에서, `Apply`가 받은 틱을 안쪽 함수에 넘김, 무엇을 하는지 설명하는 머리 주석 삭제, `Configure(false)`가 도착 대기분을 비움, 건강 값은 메인 스레드 전용으로 두고 잠금 오해를 없앰.
- [ ] **Step 4: 실행해 통과 확인** — 하네스 종료 코드 0.
- [ ] **Step 5: 커밋** — `feat: 재연결과 읽기 실패에도 먼저 보여 주지 않도록 추적 상태 보강`

### Task 2: 대기열 읽기와 도착 기록

**Files:**
- Create: `Net/QueueReader.cs`
- Modify: `UI/FramePump.cs`, `Log/BattleLogger.cs` (`OnFrame` 첫머리와 그룹 배정), `Net/SocketTap.cs`, `Plugin.cs`

**Interfaces:**
- Consumes: Task 1의 `QueueTracker`, `QueueObservation`.
- Produces: `static class QueueReader { QueueObservation Read(); string State { get; } }` — 메인 스레드 전용. `State`는 `resolving`/`ready`/`disabled:<사유>`.

- [ ] **Step 1: `QueueReader` 구현** — spec "읽는 경로"를 그대로 따른다. 순서와 값:
  - 해석(성공할 때까지 60프레임마다): `il2cpp_domain_get_assemblies` → 이미지마다 `il2cpp_class_from_name`으로 `("Core.Net","NetManager")`, `("Core.Net","RPCMsgManager")`, `("Core.Net","OnPushRPCMsgData")`. 클래스가 없으면 `resolving`으로 두고 Unreadable.
  - `_inst`: `NetManager`부터 `il2cpp_class_get_parent`를 따라 `il2cpp_class_get_field_from_name`. flags & 0x10, !(flags & 0x40), `il2cpp_class_from_type(il2cpp_field_get_type(f)) == NetManager`. 소유 클래스는 `il2cpp_field_get_parent(f)`.
  - `RPC`(클래스 = RPCMsgManager), `_OnPushRPCMsg`(타입 코드 0x15, `listClass = il2cpp_class_from_type`), `_items`(0x1D), `_size`(0x08), `<CMDID>k__BackingField`(0x08), `<DOWNSN>k__BackingField`(0x0A). 인스턴스 필드는 `il2cpp_field_get_offset`을 캐시한다(객체 헤더 포함). 클래스는 찾았는데 필드·타입이 다르면 `QueueTracker.Disable("layout:<필드>")`.
  - 프레임마다: 소유 클래스가 초기화됐는지(`UnityVersionHandler.Wrap((Il2CppClass*)owner).InitializedAndNoError`, 한 번 참이면 다시 보지 않음) → `il2cpp_field_static_get_value(instField, &inst)` → inst·rpc·list가 0이면 Unreadable → `items`·`size` → 0 ≤ size ≤ `il2cpp_array_length(items)` 아니면 Unreadable → size==0이면 Empty → `elem = *(IntPtr*)(items + 4*IntPtr.Size)`, 0이면 Unreadable → Head(cmd, down, size).
  - 포인터 값이 바뀔 때마다 inst·rpc·list·elem의 `il2cpp_object_get_class`를 해석한 클래스와 비교하고, 다르면 그 프레임은 Unreadable.
  - 래퍼 객체, `il2cpp_runtime_invoke`, `il2cpp_field_get_value_object`는 쓰지 않는다. 파일 첫머리 주석에 읽는 경로 목록(허용목록)을 둔다.
- [ ] **Step 2: 연결**
  - `FramePump.Postfix`: 프레임 중복 검사 직후 `QueueTracker.BeginFrame(frame)`(틱을 올린다). `NameHarvest.Tick()` 뒤, `PumpSchedule()` 앞에서 `if (QueueTracker.Enabled) QueueTracker.Apply(QueueReader.Read());`를 try로 감싸고, 예외면 `QueueTracker.Disable("exception: " + e.Message)`.
  - `BattleLogger.OnFrame`: 첫 줄에서 `long arrival = QueueTracker.Arrive(cmdId, header.DownSn, errId);`. `_group = ++_groupSeq;` 바로 뒤에서 `QueueTracker.NoteGroup(_group, arrival, cmdId);`. 기존 `frame` 기록 줄을 그룹 배정 뒤로 옮기고 끝에 ` seq=<arrival 또는 ->` ` grp=<_group>`을 붙인다.
  - `SocketTap`: 게임 프레임을 한 번이라도 낸 소켓이 `DropSocket`되거나 0바이트 수신으로 지워질 때 `QueueTracker.SocketDropped()`.
  - `Plugin.cs`: `Config.Bind("Overlay", "QueueSync", true, "게임이 메시지를 처리하는 순서(대기열)를 읽어, 줄을 게임이 그 장면을 처리하기 시작한 뒤에 보여 준다. 결과(체력·골드·버프) 줄은 처리가 끝난 뒤에 보여 준다. 대기열을 못 읽으면 연출 길이 추정으로 돌아간다. SyncWithAnimation이 켜져 있어야 공개 시점에 쓰인다.")` → `QueueTracker.Configure(queueSync.Value)`. 소켓 후킹보다 먼저 부른다.
- [ ] **Step 3: 확인**
  - `dotnet build AstralPartyBattleLog.csproj -c Release` 성공.
  - `dotnet run --project tools/harness/harness.csproj` 종료 코드 0.
  - `grep -rln "il2cpp_" --include=*.cs . | grep -v "^./tools/"` 출력이 `./Net/QueueReader.cs` 한 줄.
- [ ] **Step 4: 커밋** — `feat: 게임 처리 대기열의 진행을 진단 기록에 남기기`

### Task 3: 줄 기준 연결과 기록 줄

**Files:**
- Modify: `UI/OverlaySchedule.cs` (`Item`에 `Ref`·`Result`·`Opener`, `Push`, show 기록 줄), `UI/OverlaySchedule.Gate.cs` (gate release 기록 줄), `UI/OverlaySchedule.Replay.cs` (`Rec.QueueRef`), `Log/BattleLogger.cs` (PK 원인 1040 줄 나누기, "게임 종료" 구분선 그룹), `tools/harness/ReplayAdapter.cs`, `tools/harness/Replay.cs`, `tools/harness/Sched.cs`, 그룹 배정을 단정하는 기존 하네스 시험(`tools/harness/Program.cs`의 종류·그룹 태깅)

**Interfaces:**
- Consumes: `QueueTracker.TryRefOf`.
- Produces: `Item.Ref`(long, 없으면 -1), `Item.Result`(bool), `Item.Opener`(bool, 그룹을 연 cmd가 5022·1096·5044). 재생 레코드 `queue-ref`(A=항목 번호, B=기준, C=플래그: 1=결과 줄, 2=열린 메시지) — `Rec` 열거형의 `Mark` 뒤에 `QueueRef`, `RecNames`에 `"queue-ref"`.

- [ ] **Step 1: 시험 작성** — `Sched.cs`에 `RunQueueRefs`를 두고 `Run()`에서 부른다. `MemorySink`(`tools/harness/Replay.cs`)로 기록을 켜고 `BattleLogger`에 `Mirror`류를 `OverlaySchedule`로 연결한 뒤 합성 프레임을 순서대로 넣는다. `queue-ref` 레코드와 줄 순서로 확인한다.
  - QS-R1: 주사위(5022, 도착 1) 줄의 Ref=1, 플래그=2.
  - QS-R2: PK 결과(1007, 도착 2) 뒤 PK 원인 1040(도착 3)에 체력(필드 3)과 골드(필드 2) 효과가 함께 있으면, 체력 줄은 Ref=2(PK 그룹)로 먼저, 골드 줄은 Ref=3·플래그=1로 뒤에 나온다. 효과 순서가 골드→체력이어도 체력 줄이 먼저다.
  - QS-R3: 대상 미상으로 보류된 1040(도착 4)이 뒤 프레임 때 나가도 Ref=4, 플래그=1.
  - QS-R4: 1라운드 페이지와 방 짧은 채팅(5150) 줄에는 `queue-ref`가 없다.
  - QS-R5: 게임 종료(1016, 도착 5) 다음 하트비트(5004)가 오면 "게임 종료" 구분선은 Ref=5.
  - QS-R6: 이동 효과(1096, 도착 6) 줄의 Ref=6, 플래그=3.
  - QS-R7: 기록 파일을 재생하면 차이 0(기존 재생 검증 경로).
- [ ] **Step 2: 실행해 실패 확인.**
- [ ] **Step 3: 구현**
  - `Push`에서 `group >= 0 && TryRefOf(group, out r, out cmd)`이면 Ref=r, Result=`cmd is 1040 or 1013 or 1096 or 5044`, Opener=`cmd is 5022 or 1096 or 5044`. `Signal.Page`이고 `round == 1`이면 Ref=-1. `RecordItem` 바로 뒤 Ref ≥ 0이면 `queue-ref`를 남긴다. 재생기는 `queue-ref`를 같은 번호의 `item`에 붙여 `ItemIn`(새 필드 `Ref`, `Result`, `Opener`, 기본 -1/false/false)으로 주입한다.
  - `BattleLogger`: `DecodeEffect`가 체력(필드 3) 줄을 따로 알려 주게 하고, `DecodeUpdateHeroAttr`에서 PK 원인이고 PK 그룹이 있으면 머리줄과 체력 줄을 PK 그룹으로 먼저, 나머지 줄을 프레임 자기 그룹으로 뒤에 낸다(체력 줄이 없으면 전부 자기 그룹). 골드 짝 맞추기 보류도 자기 그룹을 쓴다. 1016을 받을 때 그 그룹을 기억해 두었다가 "게임 종료" 구분선을 그 그룹으로 낸다.
  - show 줄과 gate release 줄 끝에 ` grp=<그룹> ref=<Ref 또는 ->` ` why=<Why 이름>`을 붙인다. 공개 판단은 바꾸지 않는다(`SchedulerVersion` 그대로).
- [ ] **Step 4: 실행해 통과 확인** — 하네스 종료 코드 0.
- [ ] **Step 5: 커밋** — 두 개로 나눈다: `fix: PK 뒤 골드·버프 변화 줄을 PK 타격이 아니라 그 결과 연출에 맞춰 묶기`(로거), `chore: 진단 기록의 표시 줄에 기준 메시지 번호 추가`(스케줄러·재생).

### Task 4: 분석 도구

**Files:**
- Create: `tools/probe_queue.py`, `tools/test_probe_queue.py`

**Interfaces:**
- CLI: `python tools/probe_queue.py <LogOutput.log> [--expect-queue] [--margin-ms 100]`. 종료 코드: 0 통과(또는 보고만), 1 `--expect-queue` 기준 위반, 2 `[timing] queue` 줄이 하나도 없음.
- 함수: `parse(lines) -> Log`, `windows(log) -> dict[int, Window]`(도착 번호 → 처리 시작·끝 초), `evaluate(log, windows, margin_ms) -> Report`, `render(report) -> str`.

- [ ] **Step 1: 시험 작성** (`pytest tools/test_probe_queue.py`), 합성 로그 문자열로:
  - PQ1 `[timing] HH:mm:ss.fff` 접두를 BepInEx 접두가 있든 없든 읽고, 자정을 넘으면 24시간을 더한다.
  - PQ2 처리 구간: head 1(10.0초) → head 3(12.0초) → empty done=3(15.0초)에서 시작·끝이 1:(10,12), 2:(12,12), 3:(12,15).
  - PQ3 이른 줄: ref=3 show 11.5초 → 처리 시작(12.0)보다 0.5초 이름 1건, margin 100ms 안(11.95초)은 0건.
  - PQ4 게이트 신호: `gate release kind=pk by=signal ref=1` 11.0초 → 구간 안 1건. 열린 메시지(5022·1096·5044)의 끝 기준은 `queue link seq= end=` 줄로 정한다.
  - PQ5 결과 줄 늦어짐: show 줄에 `why=queue-end`인 결과 줄의 (show − 처리 끝)을 모은다.
  - PQ6 맞음 비율: `queue state=live`부터 다음 `queue state=` 줄까지 구간 합 ÷ 첫 `state=live`부터 마지막 기록까지. 멈춤 비율도 같이 낸다.
  - PQ7 `--expect-queue`: 규칙 적용 구간(`state=live` 구간과 `state=frozen` 뒤 60초 안) 안에서 ref가 있는 줄이 처리 시작 − margin보다 이르면 종료 코드 1, 결과 줄이 끝 기준의 처리 끝 − margin보다 일러도 1.
  - PQ8 `queue` 줄이 없으면 종료 코드 2.
  - PQ9 결과 줄 가운데 처리 끝보다 이른 줄을 cmd(1040·1013·1096·5044)별로 세고 최대값을 낸다.
- [ ] **Step 2: 실행해 실패 확인** — `pytest tools/test_probe_queue.py -q` → 모듈 없음.
- [ ] **Step 3: 구현** — 출력은 spec "분석" 항목 순서의 표. 결과 줄 여부는 frame 줄의 cmd(1040·1013·1096·5044)로 판단한다(그룹 → 도착 번호 → cmd). `--expect-queue`의 판정은 `state=live`·`state=frozen` 구간 안의 줄에만 건다.
- [ ] **Step 4: 실행해 통과 확인** — `pytest tools/test_probe_queue.py -q` 전부 통과.
- [ ] **Step 5: 커밋** — `chore: 대기열 진행과 표시 시각을 대조하는 분석 도구 추가`

### Task 5: 1단계 측정 (게임)

Claude가 computer-use로 진행한다. 사용자는 컴퓨터를 쓰지 않는다.

- [ ] **Step 1: 배치** — 게임을 끈 상태에서 Release 빌드를 `D:/SteamLibrary/steamapps/common/Astral Party/8vJXnINT/BepInEx/plugins/AstralPartyBattleLog/`에 복사. `BepInEx/config/astralparty.battlelog.cfg`에서 `TraceTiming = true`. `BepInEx/plugins`의 다른 모드(AnimSpeed 등)와 설정을 기록한다.
- [ ] **Step 2: 판 진행** — Steam UI로 실행 → 솔로 모드 → 연습 모드 → 협동 챌린지 맵 → 3~4라운드 → 메뉴 → 항복하기. 1배속 2판. AnimSpeed는 전투 씬을 제외 목록에 두고 있어 전투는 1배속이다(2026-11-07 `config.json`). 다른 배속 판은 다른 모드의 설정을 바꿔야 하므로 하지 않고, 규칙이 연출 길이를 쓰지 않는다는 점을 근거로 기록에 남긴다.
- [ ] **Step 3: 분석** — `python tools/probe_queue.py "<게임>/BepInEx/LogOutput.log"`. 예외·경고(`QueueReader`, `QueueTracker`) 검색. 판마다 `[health]` 줄의 `| queue` 구간(Task 7 전이면 생략).
- [ ] **Step 4: 기록** — spec에 "1단계 결과" 절을 더한다: 판 정보, 맞음 비율, 어긋남과 원인, 종류별 머문 시간, 지금 방식의 이른 줄 수, 게이트 종류별 신호의 구간 안 비율, 결과 줄 늦어짐 분포. "2단계로 넘어가는 기준" 판정과, 끝 기준 표에 더할 쌍이 있으면 그 결정(신호가 처리 끝보다 늦게 나온 게이트 종류마다).
- [ ] **Step 5: 커밋** — `docs: 대기열 관찰 측정 결과 기록` (TraceTiming은 측정이 끝나면 false로 되돌린다)

### Task 6: 공개 규칙

**Files:**
- Create: `UI/OverlaySchedule.Queue.cs`
- Modify: `UI/OverlaySchedule.Gate.cs` (`PumpGated`, 짝짓기 함수, `Resolve`, `ReleaseGated`), `UI/OverlaySchedule.Turns.cs` (`TurnGate`, `MatchRound`, `MatchTurn`, `ResolveScreenReached`), `UI/OverlaySchedule.cs` (`Pump`, `PumpModel`), `UI/OverlaySchedule.File.cs` (멈춤 기록), `UI/OverlaySchedule.Replay.cs` (`Why.QueueStart`·`Why.QueueEnd`, `Rec.Queue`·`Rec.QueueLink`, `HealthResolution`, `SchedulerVersion = 4`), `tools/harness/ReplayAdapter.cs`, `tools/harness/Replay.cs`, `tools/harness/Sched.cs`. Task 5에서 끝 기준 쌍을 더하기로 했을 때만 `Net/QueueTracker.cs`, `tools/harness/QueueTests.cs`.

**Interfaces:**
- Consumes: `QueueTracker.Snapshot()`(Mode·StartedThrough·DoneThrough), `QueueTracker.DrainLinks`, `Item.Ref`/`Result`/`Opener`.
- Produces (OverlaySchedule 내부, `OverlaySchedule.Queue.cs`):
  - 펌프 첫머리에 한 번 잡는 `_q`(QueueSnapshot)와 `Links`(Dictionary<long,long>, 열린 메시지 → 끝 기준). `Links`가 4096개를 넘으면 키가 `_q.DoneThrough − 4096`보다 작은 것을 지운다(그만큼 앞의 메시지를 기다리는 줄은 없다).
  - `const long QueueFreezeUs = 60_000_000;`, `_frozenSinceUs`(멈춤을 처음 본 펌프의 스케줄러 시각, 멈춤이 아니면 long.MinValue).
  - `bool _queueActive` — 펌프 첫머리에 한 번 계산: `_enabled && (_q.Mode == QueueMode.Live || _q.Mode == QueueMode.Frozen && now − _frozenSinceUs < QueueFreezeUs)`.
  - `bool Started(long r) => r <= _q.StartedThrough`, `bool Ended(long r) => r <= _q.DoneThrough`, `long EndOf(long r, bool opener)` — 열린 메시지가 아니면 r, 링크가 있으면 그 값, 아직 없으면 long.MaxValue.
  - `bool InWindow(Entry e)` — 기준이 없으면 참, 있으면 `Started(ref) && !Ended(EndOf(ref, opener))`.
  - 재생 레코드 `queue`(A=Mode, B=StartedThrough, C=DoneThrough; 값이 바뀐 펌프에만, `pump-begin` 바로 뒤), `queue-link`(A=열린 메시지, B=끝 기준; 그 펌프에 새로 받은 것마다).
  - `internal static Func<long> FileClockUs`(기본 Stopwatch 마이크로초) — 파일 멈춤 판정용 벽시계. 하네스가 바꿔 끼운다.

- [ ] **Step 1: 시험 작성** — `Sched.cs`에 `RunQueueRules`. 각 시험은 `QueueTracker.ResetForTest(); Configure(true)`, `Setup(log, signals: true)`, `BeginFrame`으로 틱을 올리며 `Arrive`·`NoteGroup`으로 메시지를 만들고 `OverlaySchedule.Line/Page`로 줄을 넣는다. 펌프 전에 `QueueTracker.Apply(H/E)`. 아래 도착 번호는 설명용 예시이고, 시험마다 추적기를 비우므로 실제 번호는 시험 안에서 1부터 센다.
  - QS1 하한: 카드 줄(도착 2). 맨 앞=1인 채 10초 진행 → 안 나감. 맨 앞=2 → 나감, `why=queue-start`.
  - QS2 결과 줄: 1040 그룹 줄(도착 3). 맨 앞=3 → 안 나감. 맨 앞=4 → 나감, `why=queue-end`.
  - QS3 PK와 결과 분리: 1007 그룹의 PK 줄과 체력 줄(도착 5), 그 1040의 골드 줄(도착 6, 결과 줄). 맨 앞=4일 때 창 켜짐+타격 → 아무것도 안 나감. 맨 앞=5에서 창 켜짐+타격 → PK 줄(own-signal)과 체력 줄(group-follow)이 함께 나가고 골드 줄은 남음. 맨 앞=7 → 골드 줄 나감(queue-end).
  - QS4 주사위 신호 놓침: 몬스터 주사위 줄(5022 도착 6, 눈 모름), 1002(7), 5028(8) → 링크 (6,8). 맨 앞=8 → 안 나감. 6틱 연속 빈 목록 → 나감, `why=queue-end`.
  - QS5 상한 없음과 멈춤 시한: PK 줄(도착 9), 맨 앞=8에 머문 채 70초 → 안 나감. `Apply(Unreadable)`로 멈춤 → 59초 더 → 안 나감. 멈춤을 처음 본 펌프부터 60초가 지난 펌프 → 상한으로 나감(`why=cap`).
  - QS6 기준 없음: `NoteGroup(g, -1, 5150)` 그룹의 줄은 맨 앞과 상관없이 지금 방식으로 나감.
  - QS7 1라운드 페이지는 맨 앞과 상관없이 나감.
  - QS8 꺼짐: `Configure(false)`이면 QS1 줄이 모델 기한에 나감. `Setup(log, enabled: false)`(화면 동기화 끔)이면 맨 앞과 상관없이 바로 나감.
  - QS9 늦게 온 PK 체력 줄: PK 줄이 신호로 나간 뒤 같은 그룹(1007 기준)의 체력 줄이 오면 바로 나감.
  - QS10 창 밖 신호: 주사위 줄(도착 2, 눈 3) 앞의 카드(도착 1)가 처리 중일 때 눈 3 꺼짐 → 안 나감. 맨 앞=2에서 눈 3 꺼짐 → 나감(own-signal).
  - QS11 장부 없음: 주사위 A(도착 1, 눈 3)가 신호 없이 처리 끝으로 풀린 뒤, 주사위 B(도착 2, 눈 3) 처리 중 눈 3 꺼짐 → B가 그 펌프에 나감(지금 방식이면 A의 장부가 이 신호를 소비한다).
  - QS12 열린 메시지: 1096 줄(도착 1, 결과 줄), 1002(2). 맨 앞=2 → 링크가 아직 없어 안 나감. 5028(3) 도착 → 링크 (1,3), 맨 앞=3 → 안 나감. 6틱 연속 빈 목록 → 나감(queue-end).
  - QS13 모델 경로: `FallBackToModel()` 뒤 PK 줄+체력 줄(1007, 도착 1), 카드 줄(도착 2). 맨 앞=1 → 안 나감. 맨 앞=2 → 같은 펌프에 PK·체력 줄(queue-end)과 카드 줄(queue-start).
  - QS14 멈춤 동안 경계 고정: 맞음에서 카드 줄(도착 3), 맨 앞=2. 5008 도착 → 멈춤. 맨 앞=3이 돼도(기준점 이하) 안 나감. 5008 뒤 도착한 항목(4)과 맞으면 → 나감(queue-start).
  - QS15 파일 기록: 파일을 붙이고 결과 줄이 130초 기다리는 동안(펌프는 계속 돎) 파일에 쓰이지 않고, 처리 끝에 나가면 표시 줄로 쓰인다. 펌프 없이 `FileClockUs`만 61초 진행한 뒤 새 줄이 들어오면 앞 줄이 미표시로 쓰인다.
  - QS16 재생: QS1~QS5·QS10~QS14를 `MemorySink`로 기록하고 재생하면 차이 0.
- [ ] **Step 2: 실행해 실패 확인.**
- [ ] **Step 3: 구현** — spec "2단계: 공개 규칙"의 규칙 1~8.
  - `Pump`: `BeginPump` 뒤 `_q`·`Links`·`_frozenSinceUs`·`_queueActive`를 갱신하고, 기록 중이면 `queue`·`queue-link` 레코드를 남긴다. 재생 중이면 `ReplayPort.Pump`가 넘긴 스냅숏과 링크를 쓰고 `QueueTracker`를 부르지 않는다.
  - 짝짓기(규칙 2): 적용 중이면 `MatchDice`·`Strike`(첫 타격과 반격)·`SettleWeak`·`MatchRound`·`MatchTurn`의 후보는 창 안의 줄뿐이다. 처리가 끝난 줄은 건너뛰고 처리 전 줄을 만나면 멈춘다. 장부 조회와 장부 추가(`Resolve`, `TurnGate`), `ResolveOlder`, `PkStarted`는 하지 않는다. 기준 없는 게이트 줄은 창 조건 없이 지금처럼 짝짓는다.
  - 상한 반복문(규칙 3·5): 적용 중이고 기준이 있는 게이트 줄은 상한을 건너뛰고, `Ended(EndOf(ref, opener))`이면 `Resolve(e, Resolution.Late, now, Why.QueueEnd)`.
  - 해제 반복문(규칙 1·4·6): 낡은 항목 검사 다음에, 적용 중이고 기준이 있는 줄은 `!Started(ref) && Queue.Count <= MaxWaiting`이면 멈춘다. 게이트 없는 줄은 모델 예약을 계산해 둔 뒤(`DueOf`) 결과 줄이면 `Ended(EndOf(ref, opener))`까지 기다리고, 내보낼 때 `Why.QueueEnd`/`Why.QueueStart`, 이어서 `Anchor(now, seen: true)`. 이 경로에서는 장벽·반격 동기화·모델 기한을 보지 않는다. `ReleaseGated`는 `Why.QueueEnd`이면 `Anchor(now, seen: true)`, 후보 기록 분류는 `GapKind.Late`.
  - `PumpModel`(규칙 7): 적용 중이고 기준이 있는 줄은, 결과 줄과 주사위·PK 줄이면 `Ended(EndOf(ref, opener))`, 나머지는 `Started(ref)`가 될 때 내보낸다. `MaxWaiting`을 넘으면 지금처럼 배출한다. 사유와 앵커는 위와 같다.
  - 파일(규칙 8): `Pump`가 `FileClockUs()`를 `_lastPumpUs`에 남긴다(`AttachFile`이 처음 값을 둔다). `HoldForFile`은 `FileClockUs() − _lastPumpUs ≥ FileStallMarginUs`일 때만 보류 중인 줄을 모두 미표시로 쓴다. 줄의 나이와 `_maxLagUs`·`GateCapUs`는 보지 않는다.
  - `HealthResolution`: `Why.QueueEnd` → 1(늦은 경로). `WhyNames`에 `"queue-start"`, `"queue-end"`.
  - Task 5에서 끝 기준 표에 쌍을 더하기로 했으면 추적기의 표에 더하고 QT8에 그 경우를 더한다.
- [ ] **Step 4: 실행해 통과 확인** — 하네스 종료 코드 0(기존 시험 포함).
- [ ] **Step 5: 커밋** — `feat: 게임이 장면을 처리하기 시작한 뒤에 로그 줄을 보여 주기`

### Task 7: 건강 기록, 문서, 스킬 매니페스트

**Files:**
- Modify: `UI/ScheduleHealth.cs`, `UI/OverlaySchedule.cs`(펌프마다 추적 건강 값을 넘김), `tools/harness/HealthTests.cs`, `CONTRIBUTING.md`, `docs/ARCHITECTURE.md`, `docs/SIGNAL-GATING.md`, `README.md`(설정 설명)
- Modify (스킬 저장소, 먼저 `git -C E:/projects/claude-skills pull`): `astral-party-modding/references/manifest.json`, `astral-party-modding/references/game-paths.md`, `astral-party-modding/references/pitfalls.md` — `~/.claude/skills/`와 클론 양쪽

- [ ] **Step 1: 시험 작성** — HS-Q1: 추적을 켠 관측의 게임 줄 끝에 ` | queue live=<n>% frozen=<n>% matched=<n> unmatched=<n> unread=<n> overflow=<n> freeze=<어긋남>/<못읽음>/<재연결>/<퇴장>/<오류>/<소켓> maxsize=<n> maxwait=<n>t`가 붙는다(값은 그 판 몫). 판 동안 건강 값을 여러 번 넘기면 누적 값은 판 시작 때와의 차이, `maxsize`·`maxwait`는 받은 값의 최대다. 추적이 꺼져 있거나 그 판에 값을 받지 않았으면 ` | queue off`. 기존 HS 시험은 그대로 통과(게임 줄이 정확히 한 줄).
- [ ] **Step 2: 구현** — `OverlaySchedule.Pump`가 펌프마다 `ScheduleHealth.Queue(QueueTracker.Enabled, QueueTracker.Health)`를 부른다(재생 중에는 부르지 않는다). `ScheduleHealth`는 자기 잠금 안에서 최신 값을 두고, 판 관측을 열 때 그 값을 시작점으로 복사하고, 닫을 때 위 형식으로 붙인다. 맞음·멈춤 비율은 판 동안 늘어난 `LiveFrames`·`FrozenFrames` ÷ 늘어난 `Frames`. 판 관측은 IO 스레드에서 닫힐 수 있고 `QueueTracker.Health`는 메인 스레드 전용이라 이렇게 넘긴다.
- [ ] **Step 3: 문서**
  - CONTRIBUTING 1절 끝에 "### 게임 메모리 읽기" — 읽는 경로 표(spec "읽는 경로"), 지켜야 할 것(본문·다른 필드 금지, 범용 경로 함수 금지), 감사 명령 `grep -rln "il2cpp_" --include='*.cs' . | grep -v '^./tools/'` → `./Net/QueueReader.cs` 한 줄. 7절 자동 판정 목록에 `QT1~QT20`, `QS-R1~R7`, `QS1~QS16`, `HS-Q1`.
  - ARCHITECTURE: "대기열로 공개 시점 맞추기" 절 — 읽기와 판단의 분리, 추적 상태 셋(멈춤이 60초를 넘으면 지금 방식), 크래시 금지 목록을 지키는 방법, 남는 위험(게임이 대기열을 비운 뒤 처리기가 다른 항목을 지우는 경우).
  - SIGNAL-GATING: 대기열 규칙 적용 중에는 처리 중인 줄의 신호만 받고 장부·앞 줄 늦은 해결·60초 상한을 쓰지 않는다는 절.
  - README: `QueueSync` 설정 한 줄.
- [ ] **Step 4: 스킬** — `manifest.json`에 다섯 경로(spec "읽는 경로")를 추가하고 `python ~/.claude/skills/il2cpp-dll-inspector/scripts/analyze_dll.py E:/projects/AstralPartyBattleLog/ref/snapshots/2026-10-17b/runtime.json --verify <manifest>`가 통과하는지 본다(이 스냅숏은 2026-11-07 추출본과 SHA-1이 같다). `game-paths.md`에 대기열 절(읽는 경로, 즉시 처리 9종, 대기열을 비우는 경로). `pitfalls.md` "관찰된 함정"에 "`DOWNSN`은 도착 순서대로 늘지 않는다 [관찰 2026-11-07]"와 "즉시 처리 9종은 대기열을 거치지 않아 맨 앞에 나타나지 않는다 [디컴파일 2026-11-07]". 클론 저장소에 같은 변경을 커밋하고 `main`에 푸시.
- [ ] **Step 5: 확인·커밋** — 하네스 종료 코드 0. `docs: 대기열 읽기의 허용목록과 감사 방법 문서화`(코드 변경이 함께면 `feat:`로 나눠 커밋).

### Task 8: 2단계 검증 (게임)

- [ ] **Step 1:** Task 5 Step 1~2와 같이 배치·진행(TraceTiming 켬). 1배속 2판.
- [ ] **Step 2:** `python tools/probe_queue.py <LogOutput.log> --expect-queue` 종료 코드 0. 판마다 `[health]` 줄의 `queue` 구간과 게이트 `open`이 0인지(판 끝에 나가지 못한 줄 없음) 본다. 오버레이를 화면으로 보며 PK 피해 줄이 타격 때 함께 나오는지 한 번 확인한다.
- [ ] **Step 3:** 위반이 있으면 원인을 찾아 Task 6 시험에 그 경우를 더하고 고친 뒤 다시 측정한다. 같은 가설로 3번 넘게 빌드·실행해도 새 정보가 없으면 멈추고 기록한다.
- [ ] **Step 4:** spec에 "2단계 검증" 절을 더하고 커밋 — `docs: 대기열 공개 규칙 실게임 검증 기록`. TraceTiming은 false로 되돌린다.

### Task 9: 릴리스 준비

- [ ] **Step 1:** 버전 0.4.0 — `Plugin.cs`의 `[BepInPlugin(Guid, "Astral Party Battle Log", "0.4.0")]`, `AstralPartyBattleLog.csproj`의 버전.
- [ ] **Step 2:** README에 `## v0.4.0` 절 — 사용자 영향 위주(게임 화면이 그 장면을 처리하기 시작한 뒤에 줄이 뜬다, 결과 줄은 연출이 끝난 뒤, 몬스터 주사위가 다음 신호를 기다리지 않는다, 새 설정 `QueueSync`).
- [ ] **Step 3:** `powershell -File tools/build-release.ps1`로 로컬 빌드만 한다(게시는 하지 않는다).
- [ ] **Step 4:** 전체 브랜치 리뷰 서브에이전트(`git diff main...feature/queue-sync`) — 지적을 반영하고 하네스·pytest를 다시 돌린다.
- [ ] **Step 5:** 커밋 — `chore: 대기열 공개 동기화를 담은 0.4.0 배포 준비`. 푸시·게시는 사용자에게 묻는다.
