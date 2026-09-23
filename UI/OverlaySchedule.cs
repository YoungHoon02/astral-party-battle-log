using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AstralPartyBattleLog.Log;
using BepInEx.Logging;

namespace AstralPartyBattleLog.UI;

/// <summary>
/// 로그 줄을 게임 화면이 그 장면에 도달할 때 오버레이에 내보낸다.
/// 재생 모델과 측정값은 <c>docs/OVERLAY-TIMING.md</c>.
/// </summary>
internal static partial class OverlaySchedule
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
        public readonly int Detail;

        public Item(Signal signal, LineKind kind, long group, int units, long receivedUs, float speed,
                    int generation, string? text, int round, int detail)
        {
            Signal = signal; Kind = kind; Group = group; Units = units; ReceivedUs = receivedUs;
            Speed = speed; Generation = generation; Text = text; Round = round; Detail = detail;
        }
    }

    private readonly struct Scheduled
    {
        public readonly Item Item;
        public readonly long DueUs;
        public readonly long FloorUs;
        public readonly long Seq;

        public Scheduled(Item item, long dueUs, long floorUs, long seq)
        {
            Item = item;
            DueUs = dueUs;
            FloorUs = floorUs;
            Seq = seq;
        }
    }

    // 1배속 연출 길이(µs). 근거와 측정은 docs/OVERLAY-TIMING.md "순차 재생".
    private const long CardUs = 2_870_000;
    private const long SubmitUs = 2_230_000;
    private const long AttackUs = 9_450_000;
    private const long AttackRevealUs = 1_940_000;
    private const long SkillUs = 1_920_000;
    private const long GameStartUs = 18_150_000;
    private const long StepUs = 300_000;
    private const long DiceUs = 2_700_000;
    private const long IdleAttackRevealUs = 4_350_000;
    private const long IdleDiceRevealUs = 1_600_000;

    private static readonly ConcurrentQueue<Item> Incoming = new();
    private static readonly Queue<Scheduled> Waiting = new();

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
    private static long _lastGroupFloorUs;
    private static long _seq;
    private static long _forceThroughSeq = -1;
    private static long _lastTracedGroup = -1;

    private static bool _enabled;
    private static long _maxLagUs;

    public static bool TraceTiming => TimingTrace.Enabled;

    public static void Init(ManualLogSource log, bool enabled, int maxLagMs, bool traceTiming,
                            bool screenSignals = false)
    {
        _enabled = enabled;
        _gating = enabled && screenSignals;
        _anchorUs = long.MinValue;
        _maxLagUs = Math.Max(0, maxLagMs) * 1000L;
        TimingTrace.Init(log, traceTiming);
    }

    // unscaledDelta < 0이면 Unity 값을 못 읽은 것이라 실시간 시계로 대신한다.
    // maxDelta로 자르는 이유: 연출은 잘린 deltaTime으로 진행하므로, 안 자르면 끊김 뒤에 로그가 먼저 풀린다.
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

    private static float SanitizeSpeed(float s) =>
        float.IsFinite(s) && s >= 0.25f && s <= 10f ? s : 1f;

    public static void Line(string text, LineKind kind, long group, int units) =>
        Push(Signal.Line, kind, group, units, text, 0);

    public static void Line(string text, LineKind kind, long group, int units, int detail) =>
        Push(Signal.Line, kind, group, units, text, 0, detail);

    public static void Advance(LineKind kind, long group, int units) =>
        Push(Signal.Advance, kind, group, units, null, 0);

    public static void Page(int round, long group) =>
        Push(Signal.Page, LineKind.Round, group, 0, null, round);

    // 동기화 항목은 Units/Round 자리를 쓰지 않아 opcode와 본문 길이를 실어 나른다.
    public static void Sync(int cmd, int len) => Push(Signal.Sync, LineKind.Immediate, -1, cmd, null, len);

    public static void Reset()
    {
        Interlocked.Increment(ref _generation);
        // 따로 보내면 이미 꺼내 둔 지난 판 줄이 비운 뒤에 붙을 수 있다.
        Push(Signal.Clear, LineKind.Immediate, -1, 0, null, 0);
    }

    public static void Discard()
    {
        Interlocked.Increment(ref _generation);
    }

    private static void Push(Signal signal, LineKind kind, long group, int units, string? text, int round,
                             int detail = 0) =>
        Incoming.Enqueue(new Item(signal, kind, group, units, Volatile.Read(ref _nowUs),
                                  Volatile.Read(ref _speed), Volatile.Read(ref _generation),
                                  text, round, detail));

    public static void Pump(Action<string> line, Action<int> page, Action clear)
    {
        long now = _nowUs;
        if (_gating)
        {
            PumpGated(now, line, page, clear);
            return;
        }

        while (Incoming.TryDequeue(out Item item))
        {
            if (IsStale(item)) continue;
            long seq = ++_seq;
            if (item.Signal == Signal.Sync) _forceThroughSeq = seq;
            long due = DueOf(item, out long floor);
            Waiting.Enqueue(new Scheduled(item, due, floor, seq));
        }

        while (Waiting.Count > 0)
        {
            Scheduled head = Waiting.Peek();
            if (IsStale(head.Item))
            {
                Waiting.Dequeue();
                continue;
            }
            // 동기화는 밀린 줄을 앞당기지만 결과 공개 하한까지만이다. 본인 주사위는 결정 창 닫힘(5308)이
            // 수 ms 뒤에 따라와, 하한이 없으면 공개 지연이 곧바로 무시된다(측정 4 셋째 판).
            long ready = head.Seq <= _forceThroughSeq ? head.FloorUs : head.DueUs;
            if (ready > now && Waiting.Count <= MaxWaiting) break;

            Waiting.Dequeue();
            Release(head, now, line, page, clear);
        }
    }

    // 비우기는 세대가 지나도 버리지 않는다. 새 판 직후 전투 이탈이 겹치면 비우기가 곧바로
    // 낡는데, 버리면 지난 판 페이지가 새 판에 남는다.
    private static bool IsStale(Item item) =>
        item.Signal != Signal.Clear && item.Generation != Volatile.Read(ref _generation);

    // 새 덩어리는 max(수신, 커서)에 시작하고 커서를 시작 + 연출 길이로 민다. 동기화는 커서를
    // 그 시각으로 되돌리거나 당겨, 모델 오차가 내 차례마다 0으로 돌아간다.
    private static long DueOf(Item item, out long floor)
    {
        floor = item.ReceivedUs;
        if (item.Generation != _scheduleGeneration || item.Signal == Signal.Clear)
        {
            _scheduleGeneration = item.Generation;
            // 앵커는 판 전환(ResetGating)에서만 지운다. 신호로 공개한 줄은 여기를 거치지 않아, 그 뒤 첫 줄에서
            // 지우면 방금 세운 앵커가 사라져 뒤 줄이 몰려 나온다(하네스 G13).
            _cursorUs = Math.Max(item.ReceivedUs, _anchorUs);
            _lastGroup = -1;
            ResetSegment(item.ReceivedUs);
        }

        if (item.Signal is Signal.Sync or Signal.Clear)
        {
            if (item.Signal == Signal.Sync) TraceSegment(item);
            _cursorUs = Math.Max(item.ReceivedUs, _anchorUs);
            _lastGroup = -1;
            ResetSegment(item.ReceivedUs);
            return item.ReceivedUs;
        }

        if (item.Group >= 0 && item.Group == _lastGroup)
        {
            floor = _lastGroupFloorUs;
            return _lastGroupDueUs;
        }

        bool idle = item.ReceivedUs >= _cursorUs;
        if (item.ReceivedUs > _cursorUs) _segIdleUs += item.ReceivedUs - _cursorUs;
        Tally(item);
        TraceItem(item, idle);

        long start = Math.Max(item.ReceivedUs, _cursorUs);
        // 장벽 뒤에서 기다린 줄은 앵커부터 지연을 센다. 수신 기준이면 모두 앵커로 당겨져 몰려 나온다(하네스 G13).
        start = Math.Min(start, Math.Max(item.ReceivedUs, _anchorUs) + _maxLagUs);
        start = Math.Max(start, _anchorUs);

        long due = start;
        if (_enabled)
        {
            float speed = item.Speed;
            due += (long)(RevealOf(item, idle) / speed);
            floor += (long)(RevealOf(item, idle: true) / speed);
            due = Math.Max(due, floor);
            _cursorUs = start + (long)(DurationOf(item) / speed);
        }

        _lastGroup = item.Group;
        _lastGroupDueUs = due;
        _lastGroupFloorUs = floor;
        return due;
    }

    // 연출 길이 보정용 계측. segitem을 하나씩 남기는 이유는 docs/OVERLAY-TIMING.md "연출 길이 보정용 계측".
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

    private static void TraceItem(Item item, bool idle)
    {
        if (!TraceTiming) return;
        TimingTrace.Write($"segitem kind={item.Kind.ToString().ToLowerInvariant()} units={item.Units} "
                          + $"at={(item.ReceivedUs - _segStartUs) / 1000}ms speed={item.Speed:0.##} "
                          + $"idle={(idle ? 1 : 0)}");
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

    // 화면이 쉬고 있을 때(수신이 커서보다 늦음) 시작한 덩어리는 줄이 연출 시작과 함께 나가 결과가
    // 먼저 드러났다(측정 4: 주사위 −1.1~1.3초, PK −1.3~2.4초). 밀린 덩어리는 이미 늦으므로 그대로 둔다.
    // 공개 시점만 늦추고 다음 사건을 위한 연출 길이(DurationOf)는 바꾸지 않는다.
    // 쉬는 화면 값은 "수신 뒤 결과가 보이기까지의 최소 시간"이라 밀린 덩어리·동기화에도 하한으로 쓴다.
    private static long RevealOf(Item item, bool idle) => item.Kind switch
    {
        LineKind.Attack => idle ? IdleAttackRevealUs : AttackRevealUs,
        LineKind.Dice when idle => IdleDiceRevealUs,
        _ => 0,
    };

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
                // 여기서 재면 신호 자체의 대기(항상 한 프레임)만 나온다. 예약할 때 synclead로 남긴다.
                return;
        }

        if (!TraceTiming || item.Group == _lastTracedGroup) return;
        _lastTracedGroup = item.Group;
        TimingTrace.Write($"show kind={item.Kind.ToString().ToLowerInvariant()} "
                          + $"planned={(s.DueUs - item.ReceivedUs) / 1000}ms "
                          + $"waited={(now - item.ReceivedUs) / 1000}ms speed={item.Speed:0.##}");
    }

    public static void Mark() => TimingTrace.Write($"mark speed={Volatile.Read(ref _speed):0.##}");
}
