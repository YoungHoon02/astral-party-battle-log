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
- 결과 줄: 그룹을 연 프레임의 cmd가 `Op.UpdateHeroAttr`(1040) 또는 `Op.LandBuffs`(1013).
- 주사위 끝 기준의 경계 cmd: 5022, 1026, 1007, 1117, 1015, 1016.
- 빈 목록 여유 3프레임, 대기 목록 상한 4096, 재해석 주기 60프레임.
- 하네스 성공은 종료 코드 0이다(각 묶음이 `=== … 전부 통과 ===` 배너를 찍는다). 실패하면 종료 코드가 실패 수다.
- 게임 자산·디컴파일 결과는 커밋하지 않는다(`ref/`는 gitignore). 한글이 든 문자열 치환은 Bash가 아니라 Edit 도구로 한다(cp949).
- 원격 푸시와 릴리스 게시는 사용자 허락 없이 하지 않는다. 스킬 저장소(`E:\projects\claude-skills`)만 전역 규칙대로 커밋·푸시한다.

## Review Focus

1. **로그인 전·로비에서 읽기:** 핫업데이트 어셈블리가 아직 없거나 `NetManager._inst`가 0이면 예외 없이 "못 읽음"이어야 한다. Task 5에서 LogOutput에 대기열 관련 예외·경고가 없고 `queue live=1`이 전투에서만 나오는지 확인한다.
2. **판 이탈·항복:** 게임이 대기열을 비우면(`ClearRPCMsgData`) 남은 항목이 모두 끝난 것으로 처리돼야 한다. Task 1 QT12.
3. **봇 차례 대량 대기:** 60초 넘게 기다리는 게이트 줄은 추적이 맞음인 동안 상한에 걸리지 않고, 추적이 끊기면 지금 방식(상한 포함)으로 돌아간다. Task 6 QS5.
4. **재연결:** 방 동기화(5008)나 소켓 끊김 뒤에는 그 뒤에 도착한 항목과 맞을 때까지 규칙을 멈춘다. Task 1 QT6·QT7.
5. **도착 직후의 빈 목록:** 도착한 지 3프레임이 안 된 항목은 빈 목록이어도 끝난 것으로 보지 않는다. Task 1 QT3.

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
  - `FramePump.Postfix`: 프레임 중복 검사 직후 `QueueTracker.BeginFrame(frame)`. `NameHarvest.Tick()` 뒤, `PumpSchedule()` 앞에서 `if (QueueTracker.Enabled) QueueTracker.Apply(QueueReader.Read());`를 try로 감싸고, 예외면 `QueueTracker.Disable("exception: " + e.Message)`.
  - `BattleLogger.OnFrame`: 첫 줄에서 `long arrival = QueueTracker.Arrive(cmdId, header.DownSn);`. `_group = ++_groupSeq;` 바로 뒤에서 `QueueTracker.NoteGroup(_group, arrival, cmdId);`. 기존 `frame` 기록 줄을 그룹 배정 뒤로 옮기고 끝에 ` seq=<arrival 또는 ->` ` grp=<_group>`을 붙인다.
  - `SocketTap`: 게임 프레임을 한 번이라도 낸 소켓이 `DropSocket`되거나 0바이트 수신으로 지워질 때 `QueueTracker.SocketDropped()`.
  - `Plugin.cs`: `Config.Bind("Overlay", "QueueSync", true, "게임이 메시지를 처리하는 순서(대기열)를 읽어, 줄을 게임이 그 장면을 처리하기 시작한 뒤에 보여 준다. 결과(체력·골드·버프) 줄은 처리가 끝난 뒤에 보여 준다. 대기열을 못 읽으면 연출 길이 추정으로 돌아간다. SyncWithAnimation이 켜져 있어야 공개 시점에 쓰인다.")` → `QueueTracker.Configure(queueSync.Value)`. 소켓 후킹보다 먼저 부른다.
- [ ] **Step 3: 확인**
  - `dotnet build AstralPartyBattleLog.csproj -c Release` 성공.
  - `dotnet run --project tools/harness/harness.csproj` 종료 코드 0.
  - `grep -rln "il2cpp_" --include=*.cs . | grep -v "^./tools/"` 출력이 `./Net/QueueReader.cs` 한 줄.
- [ ] **Step 4: 커밋** — `feat: 게임 처리 대기열의 진행을 진단 기록에 남기기`

### Task 3: 줄 기준 연결과 기록 줄

**Files:**
- Modify: `UI/OverlaySchedule.cs` (`Item`에 `Ref`·`Result`, `Push`, show 기록 줄), `UI/OverlaySchedule.Gate.cs` (gate release 기록 줄), `UI/OverlaySchedule.Replay.cs` (`Rec.QueueRef`), `tools/harness/ReplayAdapter.cs`, `tools/harness/Replay.cs`, `tools/harness/Sched.cs`

**Interfaces:**
- Consumes: `QueueTracker.TryRefOf`.
- Produces: `Item.Ref`(long, 없으면 -1), `Item.Result`(bool). 재생 레코드 `queue-ref`(A=항목 번호, B=기준, C=결과 줄이면 1) — `Rec` 열거형의 `Mark` 뒤에 `QueueRef`, `RecNames`에 `"queue-ref"`.

- [ ] **Step 1: 시험 작성** — `Sched.cs`에 `RunQueueRefs`를 두고 `Run()`에서 부른다. `MemorySink`(`tools/harness/Replay.cs`)로 기록을 켜고 `BattleLogger`에 `Mirror`류를 `OverlaySchedule`로 연결한 뒤 합성 프레임을 순서대로 넣는다. `queue-ref` 레코드로 확인한다.
  - QS-R1: 주사위(5022, 도착 1) 줄의 Ref=1, Result=0.
  - QS-R2: PK 결과(1007, 도착 2) 뒤 PK 원인 1040(도착 3)의 피해 줄 Ref=2(그룹 기준).
  - QS-R3: 대상 미상으로 보류된 1040(도착 4)이 뒤 프레임 때 나가도 Ref=4, Result=1.
  - QS-R4: 1라운드 페이지와 방 짧은 채팅(5150) 줄에는 `queue-ref`가 없다.
  - QS-R5: 기록 파일을 재생하면 차이 0(기존 재생 검증 경로).
- [ ] **Step 2: 실행해 실패 확인.**
- [ ] **Step 3: 구현** — `Push`에서 `group >= 0 && TryRefOf(group, out r, out cmd)`이면 Ref=r, Result=`cmd is Op.UpdateHeroAttr or Op.LandBuffs`. `Signal.Page`이고 `round == 1`이면 Ref=-1. `RecordItem` 바로 뒤 Ref ≥ 0이면 `queue-ref`를 남긴다. 재생기는 `queue-ref`를 같은 번호의 `item`에 붙여 `ItemIn`(새 필드 `Ref`, `Result`, 기본 -1/false)으로 주입한다. show 줄과 gate release 줄 끝에 ` grp=<그룹> ref=<Ref 또는 ->` ` why=<Why 이름>`을 붙인다. 공개 판단은 바꾸지 않는다(`SchedulerVersion` 그대로).
- [ ] **Step 4: 실행해 통과 확인** — 하네스 종료 코드 0.
- [ ] **Step 5: 커밋** — `chore: 진단 기록의 표시 줄에 기준 메시지 번호 추가`

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
  - PQ4 게이트 신호: `gate release kind=pk by=signal ref=1` 11.0초 → 구간 안 1건. 주사위는 끝 기준을 frame 줄 순서로(5022 다음 5028, 경계 cmd 전) 정해 구간을 계산한다.
  - PQ5 결과 줄 늦어짐: show 줄에 `why=queue-end`인 결과 줄의 (show − 처리 끝)을 모은다.
  - PQ6 맞음 비율: `queue live=1`~`queue live=0` 구간 합 ÷ 첫 `live=1`부터 마지막 기록까지.
  - PQ7 `--expect-queue`: 맞음 구간 안에서 ref가 있는 줄이 처리 시작 − margin보다 이르면 종료 코드 1, 결과 줄(`why`가 queue-end·own-signal·group-follow가 아닌데 처리 끝 − margin보다 이름)도 1.
  - PQ8 `queue` 줄이 없으면 종료 코드 2.
- [ ] **Step 2: 실행해 실패 확인** — `pytest tools/test_probe_queue.py -q` → 모듈 없음.
- [ ] **Step 3: 구현** — 출력은 spec "분석" 항목 순서의 표. 결과 줄 여부는 frame 줄의 cmd(1040·1013)로 판단한다(그룹 → 도착 번호 → cmd).
- [ ] **Step 4: 실행해 통과 확인** — `pytest tools/test_probe_queue.py -q` 전부 통과.
- [ ] **Step 5: 커밋** — `chore: 대기열 진행과 표시 시각을 대조하는 분석 도구 추가`

### Task 5: 1단계 측정 (게임)

Claude가 computer-use로 진행한다. 사용자는 컴퓨터를 쓰지 않는다.

- [ ] **Step 1: 배치** — 게임을 끈 상태에서 Release 빌드를 `D:/SteamLibrary/steamapps/common/Astral Party/8vJXnINT/BepInEx/plugins/AstralPartyBattleLog/`에 복사. `BepInEx/config/astralparty.battlelog.cfg`에서 `TraceTiming = true`. `BepInEx/plugins`의 다른 모드(AnimSpeed 등)와 설정을 기록한다.
- [ ] **Step 2: 판 진행** — Steam UI로 실행 → 솔로 모드 → 연습 모드 → 협동 챌린지 맵 → 3~4라운드 → 메뉴 → 항복하기. 1배속 2판. AnimSpeed는 전투 씬을 제외 목록에 두고 있어 전투는 1배속이다(2026-11-07 `config.json`). 다른 배속 판은 다른 모드의 설정을 바꿔야 하므로 하지 않고, 규칙이 연출 길이를 쓰지 않는다는 점을 근거로 기록에 남긴다.
- [ ] **Step 3: 분석** — `python tools/probe_queue.py "<게임>/BepInEx/LogOutput.log"`. 예외·경고(`QueueReader`, `QueueTracker`) 검색. 판마다 `[health]` 줄의 `| queue` 구간(Task 7 전이면 생략).
- [ ] **Step 4: 기록** — spec에 "1단계 결과" 절을 더한다: 판 정보, 맞음 비율, 어긋남과 원인, 종류별 머문 시간, 지금 방식의 이른 줄 수, 게이트 종류별 신호의 구간 안 비율, 결과 줄 늦어짐 분포. "2단계로 넘어가는 기준" 판정과, 처리 끝 해결에서 뺄 게이트 종류가 있으면 그 결정.
- [ ] **Step 5: 커밋** — `docs: 대기열 관찰 측정 결과 기록` (TraceTiming은 측정이 끝나면 false로 되돌린다)

### Task 6: 공개 규칙

**Files:**
- Create: `UI/OverlaySchedule.Queue.cs`
- Modify: `UI/OverlaySchedule.Gate.cs` (`PumpGated`), `UI/OverlaySchedule.cs` (`Pump`에서 스냅숏), `UI/OverlaySchedule.Replay.cs` (`Why.QueueStart`·`Why.QueueEnd`, `Rec.Queue`·`Rec.QueueLink`, `HealthResolution`, `SchedulerVersion = 4`), `tools/harness/ReplayAdapter.cs`, `tools/harness/Replay.cs`, `tools/harness/Sched.cs`, `tools/harness/harness.csproj`

**Interfaces:**
- Consumes: `QueueTracker.Snapshot()`, `DrainLinks`, `Item.Ref`/`Result`.
- Produces (OverlaySchedule 내부): 펌프마다 한 번 잡는 `_q`(QueueSnapshot)와 `_links`(Dictionary<long,long>), `bool QueueActive => _gating && _q.Live`, `bool Started(long r) => r <= _q.StartedThrough`, `bool Ended(long r) => r != long.MaxValue && r <= _q.DoneThrough`, `long EndOf(long r)`(링크가 있으면 그 값, `r > _q.LastDrained || r == _q.OpenDice`이면 long.MaxValue, 아니면 r). 재생 레코드 `queue`(A=Live?1:0, B=Started, C=Done, D=LastDrained, E=OpenDice; 값이 바뀐 펌프에만, `pump-begin` 바로 뒤) · `queue-link`(A=주사위 기준, B=끝 기준).

- [ ] **Step 1: 시험 작성** — `Sched.cs`에 `RunQueueRules`. 각 시험은 `QueueTracker.ResetForTest(); Configure(true)`, `Setup(log, signals: true)`, 프레임을 `BeginFrame`으로 올리며 `Arrive`·`NoteGroup`으로 메시지를 만들고 `OverlaySchedule.Line/Page`로 줄을 넣는다. 펌프 전에 `QueueTracker.Apply(H/E)`. 아래 도착 번호는 설명용 예시이고, 시험마다 추적기를 비우므로 실제 번호는 시험 안에서 1부터 센다.
  - QS1 하한: 카드 줄(도착 2). 맨 앞=1인 채 10초 진행 → 안 나감. 맨 앞=2 → 나감, `why=queue-start`.
  - QS2 결과 줄: 1040 그룹 줄(도착 3). 맨 앞=3 → 안 나감. 맨 앞=4 → 나감, `why=queue-end`.
  - QS3 PK: 1007 그룹의 PK 줄과 같은 그룹 피해 줄(도착 5). 맨 앞=4일 때 창 켜짐+타격 신호 → 안 나감. 맨 앞=5 → 두 줄 함께 나감.
  - QS4 주사위 신호 놓침: 몬스터 주사위 줄(5022 도착 6, 눈 모름), 1002(7), 5028(8). 맨 앞=8 → 안 나감. 3프레임 뒤 빈 목록 → 나감, 사유 queue-end.
  - QS5 상한: PK 줄(도착 9), 맨 앞=8에 머문 채 70초 → 안 나감. `Apply(Unreadable)` → 그 펌프에서 상한으로 나감.
  - QS6 기준 없음: `NoteGroup(g, -1, 5150)` 그룹의 줄은 맨 앞과 상관없이 지금 방식으로 나감.
  - QS7 1라운드 페이지는 맨 앞과 상관없이 나감.
  - QS8 추적 끔: `Configure(false)`이면 QS1 줄이 모델 기한에 나감.
  - QS9 늦게 온 PK 피해: PK 줄이 신호로 나간 뒤 같은 그룹(1007 기준)의 피해 줄이 오면 바로 나감.
  - QS10 재생: QS1~QS4를 `MemorySink`로 기록하고 재생하면 차이 0.
  - QS11 처리 끝 해결에서 뺀 게이트 종류: `QueueEndGates`에서 `Gate.Round`를 뺀 상태로 라운드 페이지(도착 1)의 기준 처리가 끝나도 신호 없이는 안 나가고, 수신 60초 뒤 상한으로 나간다.
- [ ] **Step 2: 실행해 실패 확인.**
- [ ] **Step 3: 구현** — spec "2단계: 공개 규칙"의 규칙 1~6을 `PumpGated`에 넣는다.
  - `Pump` 첫머리(기록 구간 안)에서 `_q = QueueTracker.Snapshot()`, `DrainLinks`로 `_links` 갱신, 기록 중이면 `queue`·`queue-link` 레코드. 재생 어댑터는 그 펌프의 레코드로 `_q`·`_links`를 채우고 `Snapshot` 대신 쓴다.
  - `private static readonly HashSet<Gate> QueueEndGates = { Gate.Dice, Gate.Pk, Gate.Round, Gate.Turn };` — Task 5에서 처리 끝보다 늦은 신호가 나온 종류는 여기서 뺀다(뺀 이유를 그 줄 옆 주석에 실측 근거로 적는다).
  - 상한 반복문: `QueueActive && e.Item.Ref >= 0 && QueueEndGates.Contains(e.Gate)`이면 상한을 건너뛰고, `Ended(e.Gate == Gate.Dice ? EndOf(ref) : ref)`이면 `Resolve(e, Resolution.Late, now, Why.QueueEnd)`. `QueueEndGates`에 없는 종류는 지금처럼 신호와 60초 상한을 기다린다(하한은 그대로 걸린다).
  - 해제 반복문: 낡은 항목 검사 다음에 `QueueActive && head.Item.Ref >= 0 && !Started(head.Item.Ref) && Queue.Count <= MaxWaiting`이면 멈춘다. 게이트 없는 줄은 모델 예약을 계산해 둔 뒤(`DueOf`) 결과 줄이면 `Ended(ref)`까지 기다리고, 내보낼 때 `Why.QueueEnd`/`Why.QueueStart`, 이어서 `Anchor(now, seen: true)`. 장벽·반격 동기화·모델 기한은 이 경로에서 보지 않는다.
  - `HealthResolution`: `Why.QueueEnd` → 1(늦은 경로). `WhyNames`에 `"queue-start"`, `"queue-end"`.
  - `PumpModel`(화면 신호 후킹 실패 뒤 경로)에는 규칙을 넣지 않는다.
- [ ] **Step 4: 실행해 통과 확인** — 하네스 종료 코드 0(기존 시험 포함).
- [ ] **Step 5: 커밋** — `feat: 게임이 장면을 처리하기 시작한 뒤에 로그 줄을 보여 주기`

### Task 7: 건강 기록, 문서, 스킬 매니페스트

**Files:**
- Modify: `UI/ScheduleHealth.cs`, `tools/harness/HealthTests.cs`, `CONTRIBUTING.md`, `docs/ARCHITECTURE.md`, `docs/SIGNAL-GATING.md`(대기열 규칙이 앞선다는 한 줄), `README.md`(설정 설명)
- Modify (스킬 저장소, 먼저 `git -C E:/projects/claude-skills pull`): `astral-party-modding/references/manifest.json`, `astral-party-modding/references/game-paths.md` — `~/.claude/skills/`와 클론 양쪽

- [ ] **Step 1: 시험 작성** — HS-Q1: 추적을 켠 관측의 게임 줄 끝에 ` | queue live=<n>% matched=<n> unmatched=<n> suspend=<재연결>/<소켓>/<어긋남> unread=<n> overflow=<n> maxsize=<n> maxwait=<n>f`가 붙고, 기존 HS 시험은 그대로 통과. 추적이 꺼져 있으면 ` | queue off`.
- [ ] **Step 2: 구현** — `ScheduleHealth`가 판 관측을 닫을 때 `QueueTracker.Health`의 그 판 몫(관측 시작 때 값과의 차)을 붙인다. 맞음 비율은 판 동안 `Apply`가 불린 프레임 가운데 Live였던 비율.
- [ ] **Step 3: 문서**
  - CONTRIBUTING 1절 끝에 "### 게임 메모리 읽기" — 읽는 경로 표(spec "읽는 경로"), 지켜야 할 것(본문·다른 필드 금지, 범용 경로 함수 금지), 감사 명령 `grep -rln "il2cpp_" --include='*.cs' . | grep -v '^./tools/'` → `./Net/QueueReader.cs` 한 줄. 7절 자동 판정 목록에 `QT1~QT13`, `QS-R1~5`, `QS1~QS10`, `HS-Q1`.
  - ARCHITECTURE: "대기열로 공개 시점 맞추기" 절 — 읽기와 판단의 분리, 실패하면 지금 방식으로 돌아감, 크래시 금지 목록을 지키는 방법.
  - README: `QueueSync` 설정 한 줄.
- [ ] **Step 4: 스킬** — `manifest.json`에 다섯 경로(spec "읽는 경로")를 추가하고 `python ~/.claude/skills/il2cpp-dll-inspector/scripts/analyze_dll.py E:/projects/AstralPartyBattleLog/ref/snapshots/2026-10-17b/runtime.json --verify <manifest>`가 통과하는지 본다(이 스냅숏은 2026-11-07 추출본과 SHA-1이 같다). `game-paths.md`에 대기열 절, `pitfalls.md` "관찰된 함정"에 "`DOWNSN`은 도착 순서대로 늘지 않는다 [관찰 2026-11-07]". 클론 저장소에 같은 변경을 커밋하고 `main`에 푸시.
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
