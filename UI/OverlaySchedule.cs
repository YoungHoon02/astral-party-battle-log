using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AstralPartyBattleLog.Log;
using BepInEx.Logging;

namespace AstralPartyBattleLog.UI;

/// <summary>
/// 해석이 끝난 로그 줄을 게임 화면이 그 장면에 도달할 때 오버레이에 내보낸다.
///
/// <b>늦추는 것은 화면 출력뿐이다.</b> 패킷 해석과 로거 상태는 수신 즉시 끝나고, 파일
/// 로그도 그때 쓰인다.
///
/// 게임은 연출을 <b>차례대로</b> 재생하고 서버는 봇 행동을 화면을 기다리지 않고 보낸다. 그래서
/// 봇 차례가 이어지면 화면이 패킷보다 수십 초 뒤처진다(실측 최대 36.7초). 이 큐도 덩어리를
/// 하나씩 "재생"하며 화면이 어디쯤인지 좇고, <b>내 결정 창이 열리면(<see cref="Sync"/>) 화면이
/// 따라잡은 것으로 보고</b> 밀린 줄을 모두 내보낸다.
///
/// 소켓 스레드는 넣기만 하고, 순서 계산과 출력은 메인 스레드의 <see cref="Pump"/>가 한다.
/// Unity에 의존하지 않는다 — 시각과 배속은 <see cref="Tick"/>으로 받는다.
/// 모델과 측정값은 <c>docs/OVERLAY-TIMING.md</c>.
/// </summary>
internal static class OverlaySchedule
{
    private enum Signal { Line, Page, Clear, Sync, Advance }

    private readonly struct Item
    {
        public readonly Signal Signal;
        public readonly LineKind Kind;
        public readonly long Group;
        public readonly int Units;
        public readonly long ReceivedUs;
        public readonly float Speed;
        public readonly int Generation;
        public readonly string? Text;
        public readonly int Round;

        public Item(Signal signal, LineKind kind, long group, int units, long receivedUs, float speed,
                    int generation, string? text, int round)
        {
            Signal = signal; Kind = kind; Group = group; Units = units; ReceivedUs = receivedUs;
            Speed = speed; Generation = generation; Text = text; Round = round;
        }
    }

    private readonly struct Scheduled
    {
        public readonly Item Item;
        public readonly long DueUs;
        public readonly long Seq;

        public Scheduled(Item item, long dueUs, long seq)
        {
            Item = item;
            DueUs = dueUs;
            Seq = seq;
        }
    }

    // 1배속 연출 길이(마이크로초). 봇전 세 판(관측 20개)의 화면 시각에 재생 모델을 맞춘 값이다.
    private const long CardUs = 2_870_000;
    private const long SubmitUs = 2_230_000;
    private const long AttackUs = 9_450_000;
    private const long AttackRevealUs = 1_940_000;
    private const long SkillUs = 1_920_000;
    private const long GameStartUs = 18_150_000;

    /// <summary>
    /// 추가 이동(<see cref="LineKind.Move"/>)의 칸당 시간. 주사위와 달리 실측 표본이 아직
    /// 없다 — 네 판에서 한 건도 안 나왔다. 관측이 생기면 <see cref="DiceUs"/>처럼 고쳐야 한다.
    /// </summary>
    private const long StepUs = 300_000;

    /// <summary>
    /// 주사위 한 번의 연출. <b>칸 수에 비례하지 않는다.</b>
    ///
    /// 길이를 갖는 항목이 주사위 하나뿐인 구간 일곱을 실측했다:
    /// 1칸 1266ms · 3칸 1923ms · 5칸 1585ms · 6칸 2556ms · 8칸 2661/2345ms · 10칸 1535ms.
    /// 회귀하면 <c>1539ms + 76ms×칸</c>에 R²=0.19로, 칸 수는 사실상 설명력이 없다.
    /// 10칸이 1535ms이고 1칸이 1266ms다.
    ///
    /// 전에 쓰던 칸당 300ms는 적은 칸수에서 크게 모자라 로그가 연출보다 먼저 떴고(1칸에
    /// 300ms 대 실측 1266ms), 많은 칸수에서는 과하게 늦췄다(10칸에 3000ms 대 1535ms).
    ///
    /// 값은 최소제곱이 아니라 <b>관측을 모두 덮는 하한</b>이다 — 모자라면 스포일러가 되고
    /// 남으면 조금 늦을 뿐이라 오차의 대가가 비대칭이다. 표본이 일곱이라 잠정값이다.
    /// </summary>
    private const long DiceUs = 2_700_000;

    private static readonly ConcurrentQueue<Item> Incoming = new();
    private static readonly Queue<Scheduled> Waiting = new();

    /// <summary>
    /// 대기열 상한. 넘치면 기한을 무시하고 오래된 것부터 내보낸다 — 버리지는 않는다.
    /// 시간 값이 아니라 메모리 안전장치다.
    /// </summary>
    private const int MaxWaiting = 4096;

    private static readonly Stopwatch FallbackClock = Stopwatch.StartNew();
    private static long _fallbackLastUs;

    private static long _nowUs;
    private static float _speed = 1f;
    private static int _generation;

    private static int _scheduleGeneration = -1;
    private static long _cursorUs = long.MinValue;
    private static long _lastGroup = -1;
    private static long _lastGroupDueUs;
    private static long _seq;
    private static long _forceThroughSeq = -1;
    private static long _lastTracedGroup = -1;

    private static bool _enabled;
    private static long _maxLagUs;

    public static bool TraceTiming => TimingTrace.Enabled;

    public static void Init(ManualLogSource log, bool enabled, int maxLagMs, bool traceTiming)
    {
        _enabled = enabled;
        _maxLagUs = Math.Max(0, maxLagMs) * 1000L;
        TimingTrace.Init(log, traceTiming);
    }

    /// <summary>
    /// <paramref name="unscaledDelta"/>가 음수면 Unity 값을 못 읽은 것이라 실시간 시계로
    /// 대신한다 — 시계가 멈추면 아무것도 안 나온다.
    ///
    /// 한 프레임 증분을 <paramref name="maxDelta"/>로 자른다. 게임 연출은 Unity가 잘라낸
    /// <c>deltaTime</c>으로 진행하므로, 끊김이 길어도 연출은 그만큼만 나아간다. 자르지 않으면
    /// 끊김 뒤에 로그가 연출보다 먼저 한꺼번에 풀린다.
    /// </summary>
    public static void Tick(float unscaledDelta, float maxDelta, float timeScale)
    {
        long wall = FallbackClock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
        double dt = unscaledDelta < 0 ? (wall - _fallbackLastUs) / 1_000_000.0 : unscaledDelta;
        _fallbackLastUs = wall;

        if (!(dt > 0)) dt = 0;
        if (maxDelta > 0 && dt > maxDelta) dt = maxDelta;

        Volatile.Write(ref _nowUs, _nowUs + (long)(dt * 1_000_000));
        Volatile.Write(ref _speed, SanitizeSpeed(timeScale));
    }

    /// <summary>
    /// AnimSpeedMod는 <c>Time.timeScale</c>에 배속을 직접 쓴다. 게임 자체 연출도 이 값을
    /// 바꿀 수 있어서, 재생 속도로 보기 어려운 값은 1로 되돌린다. 범위는 측정값이 아니라
    /// 안전장치다 — AnimSpeedMod의 배속 목록(1~5)을 넉넉히 감싼다.
    /// </summary>
    private static float SanitizeSpeed(float s) =>
        float.IsFinite(s) && s >= 0.25f && s <= 10f ? s : 1f;

    /// <param name="units">주사위·추가 이동의 칸 수. 그 밖에는 0.</param>
    public static void Line(string text, LineKind kind, long group, int units) =>
        Push(Signal.Line, kind, group, units, text, 0);

    public static void Advance(LineKind kind, long group, int units) =>
        Push(Signal.Advance, kind, group, units, null, 0);

    public static void Page(int round, long group) =>
        Push(Signal.Page, LineKind.Round, group, 0, null, round);

    /// <summary>
    /// 내 결정 창이 열렸거나 내 입력에 대한 응답이 왔다. 사용자는 화면을 보고서야 입력할 수
    /// 있으므로, 이 시점에는 그 전에 받은 것이 모두 화면에 나와 있다.
    ///
    /// 신호마다 의미가 다르다. <b>재생 커서를 맞추는 데는 셋 다 쓰지만</b>, 연출 길이를
    /// 배우는 관측으로는 검증된 "결정 창 열림"만 써야 한다 — 카드 제출 응답처럼 단계
    /// 중간에 오는 신호는 그 시점이 연출 사슬의 끝이라는 보장이 없다.
    ///
    /// 그래서 무엇이 신호였는지를 기록에 남긴다. 구분은 분석 쪽에서 한다
    /// (<c>tools/fit-timing.js</c>). <paramref name="units"/>·<paramref name="round"/>는
    /// 동기화 항목에서는 쓰이지 않아 opcode와 본문 길이를 실어 나르는 데 쓴다.
    /// </summary>
    public static void Sync(int cmd, int len) => Push(Signal.Sync, LineKind.Immediate, -1, cmd, null, len);

    public static void Reset()
    {
        Interlocked.Increment(ref _generation);
        // 비우기도 큐를 탄다. 따로 보내면 이미 꺼내 둔 지난 판 줄이 비운 뒤에 붙을 수 있다.
        Push(Signal.Clear, LineKind.Immediate, -1, 0, null, 0);
    }

    /// <summary>
    /// 전투를 벗어났다. 대기 중인 줄만 버리고 화면은 그대로 둔다 — 결과 화면에서도
    /// 이미 보인 로그는 볼 수 있어야 한다.
    /// </summary>
    public static void Discard()
    {
        Interlocked.Increment(ref _generation);
    }

    private static void Push(Signal signal, LineKind kind, long group, int units, string? text, int round) =>
        Incoming.Enqueue(new Item(signal, kind, group, units, Volatile.Read(ref _nowUs),
                                  Volatile.Read(ref _speed), Volatile.Read(ref _generation),
                                  text, round));

    public static void Pump(Action<string> line, Action<int> page, Action clear)
    {
        long now = _nowUs;

        while (Incoming.TryDequeue(out Item item))
        {
            if (IsStale(item)) continue;
            long seq = ++_seq;
            // 동기화 앞에 받은 것은 화면에 이미 나왔다. 기한과 상관없이 내보낸다.
            if (item.Signal == Signal.Sync) _forceThroughSeq = seq;
            Waiting.Enqueue(new Scheduled(item, DueOf(item), seq));
        }

        while (Waiting.Count > 0)
        {
            Scheduled head = Waiting.Peek();
            if (IsStale(head.Item))
            {
                Waiting.Dequeue();
                continue;
            }
            if (head.Seq > _forceThroughSeq && head.DueUs > now && Waiting.Count <= MaxWaiting) break;

            Waiting.Dequeue();
            Release(head, now, line, page, clear);
        }
    }

    /// <summary>
    /// 비우기는 세대가 지나도 버리지 않는다. 새 판 직후 전투 이탈이 겹치면 비우기 신호의
    /// 세대가 곧바로 낡는데, 버리면 지난 판 페이지가 새 판에 남는다. 비우기는 여러 번
    /// 실행돼도 무해하다.
    /// </summary>
    private static bool IsStale(Item item) =>
        item.Signal != Signal.Clear && item.Generation != Volatile.Read(ref _generation);

    /// <summary>
    /// 재생 모델. <c>커서</c>는 화면이 지금 재생 중인 덩어리가 끝나는 시각이다.
    /// <list type="bullet">
    /// <item>새 덩어리는 <c>max(수신, 커서)</c>에 시작한다 — 앞 연출이 끝나야 다음이 나온다.
    ///   줄은 <c>시작 + 드러나는 시점</c>에 보이고, 커서는 <c>시작 + 연출 길이</c>로 간다.</item>
    /// <item>같은 그룹(한 패킷, 또는 PK 결과와 그 피해)은 첫 줄의 기한을 같이 쓴다.</item>
    /// <item>동기화는 커서를 그 시각으로 <b>되돌리거나 당긴다</b> — 모델이 틀렸어도 내 차례마다
    ///   오차가 0으로 돌아가 계속 쌓이지 않는다.</item>
    /// <item>시작은 수신 뒤 최대 지연을 넘지 않는다. 모델이 과하게 밀려도 로그가 멈추지 않는다.</item>
    /// </list>
    /// 배속은 넣을 때의 값을 쓰고, 기다리는 도중 배속이 바뀌어도 다시 계산하지 않는다.
    /// </summary>
    private static long DueOf(Item item)
    {
        if (item.Generation != _scheduleGeneration || item.Signal == Signal.Clear)
        {
            _scheduleGeneration = item.Generation;
            _cursorUs = item.ReceivedUs;
            _lastGroup = -1;
            ResetSegment(item.ReceivedUs);
        }

        if (item.Signal is Signal.Sync or Signal.Clear)
        {
            if (item.Signal == Signal.Sync) TraceSegment(item);
            _cursorUs = item.ReceivedUs;
            _lastGroup = -1;
            ResetSegment(item.ReceivedUs);
            return item.ReceivedUs;
        }

        if (item.Group >= 0 && item.Group == _lastGroup) return _lastGroupDueUs;

        if (item.ReceivedUs > _cursorUs) _segIdleUs += item.ReceivedUs - _cursorUs;
        Tally(item);
        TraceItem(item);

        long start = Math.Max(item.ReceivedUs, _cursorUs);
        start = Math.Min(start, item.ReceivedUs + _maxLagUs);

        long due = start;
        if (_enabled)
        {
            float speed = item.Speed;
            due += (long)(RevealOf(item) / speed);
            _cursorUs = start + (long)(DurationOf(item) / speed);
        }

        _lastGroup = item.Group;
        _lastGroupDueUs = due;
        return due;
    }

    // ── 연출 길이 보정용 계측 ────────────────────────────────────────────────
    //
    // 동기화 신호는 게임이 결정 창을 여는 순간 나온다. 사용자가 아니라 게임이 여는 것이라,
    // 그 시각은 "앞선 연출 사슬이 끝난 정확한 시점"이다.
    //
    // 화면도 연출을 차례대로 재생하므로 도착 시각 t_i와 참 길이 d_i에 대해
    //
    //     끝_i = max(t_i, 끝_{i-1}) + d_i,   끝_마지막 = 동기화 시각
    //
    // 이 성립한다. 이 max 때문에 "합 = elapsed - idle"로 접으면 안 된다 — 여기서 재는 idle은
    // 모델이 자기 (틀린) 길이로 계산한 값이라, 화면이 실제로 쉰 구간과 다르다. 접어서 풀면
    // 화면이 중간에 쉰 구간에서 식이 어긋난다(3차 측정에서 같은 판의 두 구간이 +4.9초와
    // -9.7초로 갈렸다).
    //
    // 그래서 도착 시각과 함께 항목을 하나씩 남기고, 푸는 쪽에서 max를 그대로 두고 맞춘다.
    // 누계(synclead)는 눈으로 훑기 위한 요약이며 푸는 데 쓰지 않는다.
    //
    // 구간 구성을 여기서 직접 남기는 이유: 바깥에서 show 줄로 재구성하면 출력 없는 예약
    // (Signal.Advance)이 빠지고 그룹 묶음도 흉내 내야 해서 어긋난다.
    //
    // AttackRevealUs는 커서를 움직이지 않으므로 이 방법으로는 구할 수 없다.
    private static long _segStartUs, _segIdleUs;
    private static int _segCard, _segSubmit, _segAttack, _segSkill, _segSteps, _segGameStart;

    private static void ResetSegment(long atUs)
    {
        _segStartUs = atUs;
        _segIdleUs = 0;
        _segCard = _segSubmit = _segAttack = _segSkill = _segSteps = _segGameStart = 0;
    }

    private static void Tally(Item item)
    {
        switch (item.Kind)
        {
            case LineKind.Card: _segCard++; break;
            case LineKind.Submit: _segSubmit++; break;
            case LineKind.Attack: _segAttack++; break;
            case LineKind.Skill: _segSkill++; break;
            case LineKind.Dice or LineKind.Move: _segSteps += item.Units; break;
            case LineKind.Round when item.Signal == Signal.Page && item.Round == 1: _segGameStart++; break;
        }
    }

    /// <summary>커서를 움직인 항목 하나. <c>at</c>은 구간 시작 기준 도착 시각이다.</summary>
    private static void TraceItem(Item item)
    {
        if (!TraceTiming) return;
        TimingTrace.Write($"segitem kind={item.Kind.ToString().ToLowerInvariant()} units={item.Units} "
                          + $"at={(item.ReceivedUs - _segStartUs) / 1000}ms speed={item.Speed:0.##}");
    }

    private static void TraceSegment(Item sync)
    {
        if (!TraceTiming) return;
        long syncUs = sync.ReceivedUs;
        TimingTrace.Write($"synclead cmd={sync.Units} len={sync.Round} "
                          + $"ahead={(_cursorUs - syncUs) / 1000}ms backlog={Waiting.Count} "
                          + $"elapsed={(syncUs - _segStartUs) / 1000}ms idle={_segIdleUs / 1000}ms "
                          + $"card={_segCard} submit={_segSubmit} attack={_segAttack} "
                          + $"skill={_segSkill} steps={_segSteps} start={_segGameStart}");
    }

    private static long RevealOf(Item item) => item.Kind == LineKind.Attack ? AttackRevealUs : 0;

    private static long DurationOf(Item item) => item.Kind switch
    {
        LineKind.Card => CardUs,
        LineKind.Submit => SubmitUs,
        LineKind.Attack => AttackUs,
        LineKind.Skill => SkillUs,
        LineKind.Dice => DiceUs,
        LineKind.Move => item.Units * StepUs,
        LineKind.Round when item.Signal == Signal.Page && item.Round == 1 => GameStartUs,
        _ => 0,
    };

    private static void Release(Scheduled s, long now, Action<string> line, Action<int> page, Action clear)
    {
        Item item = s.Item;
        switch (item.Signal)
        {
            case Signal.Line: line(item.Text!); break;
            case Signal.Page: page(item.Round); break;
            case Signal.Clear: clear(); break;
            case Signal.Advance: return;
            case Signal.Sync:
                // 실제 신호 값은 예약할 때 synclead로 남긴다. 여기서 재면 신호 자체의
                // 대기 시간(항상 한 프레임)만 나와 쓸모가 없었다.
                return;
        }

        if (!TraceTiming || item.Group == _lastTracedGroup) return;
        _lastTracedGroup = item.Group;
        TimingTrace.Write($"show kind={item.Kind.ToString().ToLowerInvariant()} "
                          + $"planned={(s.DueUs - item.ReceivedUs) / 1000}ms "
                          + $"waited={(now - item.ReceivedUs) / 1000}ms speed={item.Speed:0.##}");
    }

    /// <summary>
    /// 측정용 표시(F10). 화면에서 본 장면의 시각을 남긴다 — <c>battle-log.txt</c>의 수신 시각과
    /// 맞춰 화면 지연을 잰다.
    /// </summary>
    public static void Mark() => TimingTrace.Write($"mark speed={Volatile.Read(ref _speed):0.##}");
}
