using System;
using System.Collections.Generic;
using System.Threading;
using AstralPartyBattleLog.Log;

namespace AstralPartyBattleLog.UI;

public enum ScreenSignal { DiceFace, Window, Hit, RoundTip, TopTip }

// 주사위·PK 결과 줄을 화면 신호로 공개한다. 규칙과 근거는 docs/SIGNAL-GATING.md.
internal static partial class OverlaySchedule
{
    private enum Gate { None, Dice, Pk, Round, Turn }
    private enum Resolution { None, Signal, Late, Cap }

    private sealed class Entry
    {
        public Item Item;
        public long Seq;
        public Gate Gate;
        public List<int> Pips = new();
        public bool Counter;
        public bool HeldTraced;
        public bool Scheduled;
        public long DueUs, FloorUs;
        public Resolution Res;
        public long ResolvedUs;
        public Window? Win;
        // 계측 전용: 해결 사유와 원인 신호 번호.
        public Why Why;
        public long CauseId = long.MinValue;
    }

    private sealed class Ledger
    {
        public Gate Gate;
        public List<int> Pips = new();
        public bool Counter;
        public long Seq;
        public long ReceivedUs;
        public long ItemId;
    }

    private sealed class Window
    {
        public int Strikes;
        public long LastSeq = -1;
        public bool Ended;
        public long EndUs;
        public long StartUs;
    }

    private readonly struct PendingSignal
    {
        public readonly ScreenSignal Kind;
        public readonly int Pip;
        public readonly bool On;
        public readonly IntPtr Instance;
        public readonly long AtUs;
        public readonly int Generation;
        // 재생 기록 번호. 기록을 끄면 0이다.
        public readonly long Id;

        public PendingSignal(ScreenSignal kind, int pip, bool on, IntPtr instance, long atUs, int generation, long id)
        {
            Kind = kind; Pip = pip; On = on; Instance = instance; AtUs = atUs; Generation = generation; Id = id;
        }
    }

    // 관측 최대 화면 지연 36.7초의 약 1.6배. 표시 누락을 막는 값이지 연출이 끝났다는 보장이 아니다.
    private const long GateCapUs = 60_000_000;
    // 주사위 연출 2.70초 − 관측 최소 굴림 1.14초. 결과가 보인 뒤 이동이 끝나기까지.
    private const long DiceTailUs = 1_560_000;

    private static bool _gating;
    private static long _anchorUs = long.MinValue;
    private static int _gateGeneration = -1;

    private static readonly List<Entry> Queue = new();
    private static readonly List<Ledger> Unconsumed = new();
    private static readonly List<PendingSignal> Signals = new();
    private static readonly HashSet<IntPtr> ActiveHits = new();
    private static Window? _window;
    private static Window? _barrier;
    private static long _barrierCapUs;

    public static bool UsesScreenSignals => _gating;

    // 후킹 설치 실패나 반복 오류로 신호가 끊기면 모델 경로로 돌아간다. 조기 표시 위험은 측정 4 넷째 판과 같다.
    // 대기 중인 줄은 신호를 더 받을 수 없으므로 모델 예약으로 넘긴다. 메인 스레드에서만 부른다.
    public static void FallBackToModel()
    {
        _touched = true;
        bool rec = _recording;
        if (rec)
        {
            EmitEnv(Env.Fallback);
            BeginProcessing();
        }
        try
        {
            if (!_gating) return;
            _gating = false;
            ScheduleHealth.Gating(false);
            _barrier = null;
            foreach (Entry e in Queue)
            {
                if (IsStale(e.Item))
                {
                    DropEntry(e);
                    continue;
                }
                if (!e.Scheduled)
                {
                    e.DueUs = DueOf(e.Item, out e.FloorUs);
                    Decide(Act.Schedule, e.Item, Why.Model, due: e.DueUs, floor: e.FloorUs);
                }
                Waiting.Enqueue(new Scheduled(e.Item, e.DueUs, e.FloorUs, e.Seq));
                Decide(Act.Transfer, e.Item, Why.Fallback, due: e.DueUs, floor: e.FloorUs);
                if (e.Gate != Gate.None && e.Res == Resolution.None)
                    ScheduleHealth.GateLeft(e.Item.Generation, GateIndex(e.Gate), HealthCount.FallbackMoved);
            }
            ResetGating(_gateGeneration);
            Signals.Clear();
        }
        finally
        {
            if (rec) EndProcessing();
        }
    }

    // 메인 스레드(SetActive 후킹)에서 온다. Pump가 수신 줄을 먼저 받은 뒤 처리한다.
    public static void Screen(ScreenSignal kind, int pip, bool on, IntPtr instance)
    {
        _touched = true;
        long at = Volatile.Read(ref _nowUs);
        int generation = Volatile.Read(ref _generation);
        ScheduleHealth.Signal(generation, (int)kind);
        if (!_gating) return;
        var s = new PendingSignal(kind, pip, on, instance, at, generation, _recording ? ++_signalIds : 0);
        if (_recording) RecordSignal(s);
        Signals.Add(s);
    }

    private static void ResetGating(int generation)
    {
        _gateGeneration = generation;
        Queue.Clear();
        Unconsumed.Clear();
        ActiveHits.Clear();
        _window = null;
        _barrier = null;
        _anchorUs = long.MinValue;
        TopOnCount.Clear();
    }

    private static void PumpGated(long now, Action<string> line, Action<int> page, Action clear)
    {
        int generation = Volatile.Read(ref _generation);
        if (generation != _gateGeneration)
        {
            foreach (Entry e in Queue) DropEntry(e);
            ResetGating(generation);
        }

        while (Incoming.TryDequeue(out Item item))
        {
            Take(item);
            if (IsStale(item))
            {
                Drop(item);
                continue;
            }
            long seq = ++_seq;
            if (item.Signal == Signal.Sync) ForceThrough(seq, bySignal: false);
            Queue.Add(NewEntry(item, seq));
        }

        foreach (PendingSignal s in Signals)
        {
            TakeSignal(s);
            if (s.Generation != _gateGeneration)
            {
                ScheduleHealth.Count(HealthCount.StaleSignal);
                continue;
            }
            _cause = s.Id;
            Handle(s);
        }
        _cause = Null;
        Signals.Clear();

        foreach (Entry e in Queue)
            if (e.Gate != Gate.None && e.Res == Resolution.None && now - e.Item.ReceivedUs >= GateCapUs)
            {
                Resolve(e, Resolution.Cap, now, Why.Cap);
                GateTrace($"timeout kind={e.Gate.ToString().ToLowerInvariant()} waited={(now - e.Item.ReceivedUs) / 1000}ms");
            }

        while (Queue.Count > 0)
        {
            Entry head = Queue[0];
            if (IsStale(head.Item))
            {
                Queue.RemoveAt(0);
                DropEntry(head);
                continue;
            }

            if (head.Gate != Gate.None)
            {
                if (head.Res == Resolution.None)
                {
                    if (Queue.Count <= MaxWaiting) break;
                    Resolve(head, Resolution.Cap, now, Why.Budget);
                }
                ReleaseGated(head, now, line, page, clear);
                continue;
            }

            bool toCounter = false;
            bool lifted = BarrierLifted(now);
            if (!lifted && Queue.Count <= MaxWaiting)
            {
                toCounter = SyncBeforeCounter();
                if (!toCounter) break;
            }

            if (!head.Scheduled)
            {
                head.DueUs = DueOf(head.Item, out head.FloorUs);
                head.Scheduled = true;
                Decide(Act.Schedule, head.Item, Why.Model, due: head.DueUs, floor: head.FloorUs);
            }
            bool forced = head.Seq <= _forceThroughSeq;
            long ready = forced ? Math.Max(head.FloorUs, _anchorUs) : head.DueUs;
            if (!toCounter && ready > now && Queue.Count <= MaxWaiting) break;

            Queue.RemoveAt(0);
            Why why = toCounter ? Why.CounterSync
                : ready > now || !lifted ? Why.Budget
                : PlannedWhy(forced, head.DueUs, now);
            Release(new Scheduled(head.Item, head.DueUs, head.FloorUs, head.Seq), now, line, page, clear, why,
                    why == Why.LaterSignal ? _forceCause : Null);
        }
    }

    private static Entry NewEntry(Item item, long seq)
    {
        Entry e = ClassifyEntry(item, seq);
        if (e.Gate != Gate.None) ScheduleHealth.GateCreated(item.Generation, GateIndex(e.Gate));
        return e;
    }

    private static Entry ClassifyEntry(Item item, long seq)
    {
        var e = new Entry { Item = item, Seq = seq };
        // 1라운드는 넘어올 앞 라운드 줄이 없고, 페이지가 전투 씬 로드 전에 와서 오버레이 표시 판단에 쓰인다.
        if (item.Signal == Signal.Page && item.Round >= 2)
        {
            e.Gate = Gate.Round;
            return e;
        }
        if (item.Signal != Signal.Line) return e;
        if (item.Kind == LineKind.Turn && item.Detail == 0)
        {
            e.Gate = TurnGate(item, seq);
            return e;
        }
        if (item.Kind == LineKind.Dice)
        {
            e.Gate = Gate.Dice;
            e.Pips = DecodePips(item.Detail);
        }
        else if (item.Kind == LineKind.Attack)
        {
            e.Gate = Gate.Pk;
            e.Counter = item.Detail == 1;
        }
        return e;
    }

    // PK를 신호로 공개했으면 뒤 줄은 같은 창의 꺼짐까지 기다린다. 창이 끝나지 않으면 상한까지.
    private static bool BarrierLifted(long now)
    {
        if (_barrier is null) return true;
        if (!_barrier.Ended && now < _barrierCapUs) return false;
        Anchor(_barrier.Ended ? _barrier.EndUs : now, seen: _barrier.Ended);
        _barrier = null;
        return true;
    }

    // 반격 전 카드 선택 응답(5036)은 원래 PK와 반격 사이에 온다. 화면에 보이지 않으므로 같은 창의 반격이
    // 타격에 짝지어졌으면 먼저 처리해 반격을 타격 시점에 낸다. 장벽은 반격이 다시 세운다(측정 4 다섯째 판 재생).
    // 사이에 보이는 줄이나 미해결 결과가 있으면 근거 범위 밖이라 기다리고 기록만 남긴다.
    private static bool SyncBeforeCounter()
    {
        Entry? counter = null;
        int end = 0;
        for (; end < Queue.Count; end++)
            if (Queue[end].Gate == Gate.Pk && Queue[end].Counter && Queue[end].Res == Resolution.Signal
                && Queue[end].Win == _barrier)
            {
                counter = Queue[end];
                break;
            }
        if (counter is null) return false;

        for (int i = 0; i < end; i++)
        {
            Item it = Queue[i].Item;
            if (it.Signal == Signal.Sync && it.Units == Op.BattleUseCard) continue;
            if (!counter.HeldTraced)
            {
                counter.HeldTraced = true;
                Note(Diag.CounterHeld, counter.Item.Id);
                GateTrace($"counter-held by={(Queue[i].Gate != Gate.None ? Queue[i].Gate.ToString() : it.Kind.ToString()).ToLowerInvariant()} "
                          + $"signal={it.Signal.ToString().ToLowerInvariant()}");
            }
            return false;
        }
        return true;
    }

    // seen: 화면 신호가 이 시각의 화면 위치를 보여 줬다. 그 앞 줄들로 민 모델 커서는 낡은 추정이라 앵커로
    // 되돌린다(동기화와 같은 이유). 늦은 경로·상한은 화면 위치를 모르므로 커서를 당기지 않는다.
    private static void Anchor(long atUs, bool seen = false)
    {
        _anchorUs = Math.Max(_anchorUs, atUs);
        _cursorUs = seen ? _anchorUs : Math.Max(_cursorUs, atUs);
    }

    private static void ReleaseGated(Entry head, long now, Action<string> line, Action<int> page, Action clear)
    {
        Queue.RemoveAt(0);
        Release(new Scheduled(head.Item, now, now, head.Seq), now, line, page, clear, head.Why, head.CauseId,
                resolvedUs: head.ResolvedUs, planned: false);
        ScheduleHealth.GateShown(head.Item.Generation, GateIndex(head.Gate), HealthResolution(head.Why));
        if (ScreenCandidates.Enabled && head.Why != Why.OwnSignal)
            ScreenCandidates.Gap(head.Why switch
            {
                Why.WindowClose => GapKind.Weak,
                Why.LaterSignal => GapKind.Late,
                Why.Cap => GapKind.Cap,
                _ => GapKind.Budget,
            }, head.Gate.ToString().ToLowerInvariant(), head.Item.ReceivedUs, now);
        GateTrace($"release kind={head.Gate.ToString().ToLowerInvariant()} by={head.Res.ToString().ToLowerInvariant()} "
                  + $"waited={(now - head.Item.ReceivedUs) / 1000}ms");

        // PK 피해 줄은 PK 결과와 같은 그룹이라 함께 공개한다.
        while (Queue.Count > 0 && Queue[0].Gate == Gate.None && Queue[0].Item.Group >= 0
               && Queue[0].Item.Group == head.Item.Group && !IsStale(Queue[0].Item))
        {
            Entry follower = Queue[0];
            Queue.RemoveAt(0);
            Release(new Scheduled(follower.Item, now, now, follower.Seq), now, line, page, clear, Why.GroupFollow,
                    leader: head.Item.Id, planned: false);
        }
        _lastGroup = -1;

        if (head.Res != Resolution.Signal)
        {
            Anchor(now);
            return;
        }
        if (head.Gate is Gate.Round or Gate.Turn)
        {
            Anchor(head.ResolvedUs, seen: true);
            return;
        }
        if (head.Gate == Gate.Dice)
        {
            Anchor(head.ResolvedUs + (long)(DiceTailUs / head.Item.Speed), seen: true);
            return;
        }
        if (head.Win is { Ended: true } done) Anchor(done.EndUs, seen: true);
        else if (head.Win is not null)
        {
            _barrier = head.Win;
            _barrierCapUs = head.Item.ReceivedUs + GateCapUs;
        }
    }

    private static void Handle(PendingSignal s)
    {
        switch (s.Kind)
        {
            case ScreenSignal.DiceFace:
                if (!s.On && s.Pip > 0) MatchDice(s.Pip, s.AtUs);
                break;
            case ScreenSignal.Window:
                // 타격 꺼짐을 하나 놓치면 활성 수가 0으로 돌아오지 않아 다음 창의 첫 타격이 빠진다.
                ActiveHits.Clear();
                if (s.On)
                {
                    CloseWindow(s.AtUs);
                    _window = new Window { StartUs = s.AtUs };
                    PkStarted(s.AtUs);
                }
                else CloseWindow(s.AtUs);
                break;
            case ScreenSignal.RoundTip:
                if (s.On) MatchRound(s.AtUs);
                break;
            case ScreenSignal.TopTip:
                TopTip(s);
                break;
            case ScreenSignal.Hit:
                if (!s.On)
                {
                    ActiveHits.Remove(s.Instance);
                    break;
                }
                // 연타는 인스턴스가 겹쳐 켜지고 반격은 앞 인스턴스가 모두 꺼진 뒤 켜진다(설계 전제 4).
                if (ActiveHits.Add(s.Instance) && ActiveHits.Count == 1) Strike(s.AtUs);
                break;
        }
    }

    // PK 창이 열렸으면 화면이 PK 시작에 왔으므로 그보다 먼저 받은 사건은 지났다(설계 전제 1). 신호가 없는 몬스터
    // 주사위가 몬스터와 부딪힌 PK의 타격까지 붙잡혀, 본인 카드 선택 동안 +26초 늦었다(2026-09-26 재생 기록).
    // 창의 PK는 결과가 창보다 늦게 올 수 있어(전제 5) 풀지 않는다. 장부에 PK가 있으면 이 창이 그 PK 것일 수 있어
    // 손대지 않는다. 창보다 늦게 받은 줄은 이 PK 뒤 사건일 수 있어 풀지 않는다.
    private static void PkStarted(long atUs)
    {
        (Entry? pk, Ledger? owed) = OldestPk(afterSeq: -1, long.MaxValue, originalOnly: true);
        if (owed is not null) return;
        foreach (Entry e in Queue)
        {
            if (pk is not null && e.Seq >= pk.Seq || e.Item.ReceivedUs >= atUs) break;
            if (e.Gate != Gate.None && e.Res == Resolution.None) Resolve(e, Resolution.Late, atUs, Why.LaterSignal);
        }
    }

    private static void CloseWindow(long atUs)
    {
        if (_window is null) return;
        _window.Ended = true;
        _window.EndUs = atUs;
        if (_window.Strikes == 0) SettleWeak(_window, atUs);
        _window = null;
    }

    // 로거가 주사위 여러 개의 눈을 두 자리씩 담아 넘긴다("5+2" → 205). 0이면 눈을 모른다(몬스터 등).
    private static List<int> DecodePips(int detail)
    {
        var pips = new List<int>();
        for (; detail > 0; detail /= 100) pips.Add(detail % 100);
        return pips;
    }

    // 주사위 여러 개는 눈마다 오브젝트가 따로 꺼진다. 모든 눈이 꺼져야 공개하고, 첫 눈에서 앞 대기 줄을 푼다.
    private static void MatchDice(int pip, long atUs)
    {
        Entry? pending = null;
        foreach (Entry e in Queue)
            if (e.Gate == Gate.Dice && e.Res == Resolution.None && e.Pips.Contains(pip) && e.Item.ReceivedUs < atUs)
            {
                pending = e;
                break;
            }
        Ledger? owed = Unconsumed.Find(l => l.Gate == Gate.Dice && l.Pips.Contains(pip) && l.ReceivedUs < atUs);

        if (owed is not null && (pending is null || owed.Seq < pending.Seq))
        {
            owed.Pips.Remove(pip);
            if (owed.Pips.Count == 0) Unconsumed.Remove(owed);
            Note(Diag.Consume, owed.ItemId);
            GateTrace($"consume kind=dice pip={pip}");
            return;
        }
        if (pending is null)
        {
            Stray(atUs);
            GateTrace($"stray kind=dice pip={pip}");
            return;
        }
        pending.Pips.Remove(pip);
        ResolveOlder(pending.Seq, atUs);
        if (pending.Pips.Count == 0) Resolve(pending, Resolution.Signal, atUs, Why.OwnSignal);
    }

    private static void Strike(long atUs)
    {
        if (_window is null)
        {
            Note(Diag.Outside);
            GateTrace("outside");
            return;
        }
        Window w = _window;
        w.Strikes++;

        if (w.Strikes == 1)
        {
            (Entry? pending, Ledger? owed) = OldestPk(afterSeq: -1, atUs, originalOnly: true);
            if (owed is not null && (pending is null || owed.Seq < pending.Seq))
            {
                Unconsumed.Remove(owed);
                Note(Diag.Consume, owed.ItemId);
                GateTrace("consume kind=pk");
                return;
            }
            if (pending is null) return;
            Resolve(pending, Resolution.Signal, atUs, Why.OwnSignal);
            pending.Win = w;
            w.LastSeq = pending.Seq;
            ResolveOlder(pending.Seq, atUs);
            return;
        }

        // 두 번째 이후 스트라이크는 이 창에서 방금 공개한 PK 바로 다음 항목이 반격일 때만 쓴다.
        if (w.LastSeq < 0)
        {
            Note(Diag.ExtraStrike);
            GateTrace("extra-strike");
            return;
        }
        (Entry? next, Ledger? nextOwed) = OldestPk(afterSeq: w.LastSeq, long.MaxValue, originalOnly: false);
        bool owedFirst = nextOwed is not null && (next is null || nextOwed.Seq < next.Seq);
        if (owedFirst && nextOwed!.Counter && nextOwed.ReceivedUs < atUs)
        {
            Unconsumed.Remove(nextOwed);
            w.LastSeq = nextOwed.Seq;
            Note(Diag.Consume, nextOwed.ItemId);
            GateTrace("consume kind=pk counter=1");
            return;
        }
        if (!owedFirst && next is not null && next.Counter && next.Item.ReceivedUs < atUs)
        {
            Resolve(next, Resolution.Signal, atUs, Why.OwnSignal);
            next.Win = w;
            w.LastSeq = next.Seq;
            return;
        }
        Note(Diag.ExtraStrike);
        GateTrace("extra-strike");
    }

    // 창마다 원래 PK 하나가 대응한다(반격은 같은 창을 쓴다). 타격 없이 닫힌 창의 꺼짐은 그 PK 연출이 끝난
    // 시점이라 여기서 공개한다. 대기로 두면 다음 신호까지 늦고(측정 4 여덟째 판 +8.2초), 장부 항목을 정산하지
    // 않으면 타격 없는 캐릭터의 결과가 뒤 PK의 타격을 대신 소비하는 연쇄가 생긴다(일곱째 판).
    private static void SettleWeak(Window w, long atUs)
    {
        (Entry? pending, Ledger? owed) = OldestPk(afterSeq: -1, atUs, originalOnly: true);
        if (owed is not null && (pending is null || owed.Seq < pending.Seq))
        {
            Unconsumed.Remove(owed);
            Note(Diag.WeakSettle, owed.ItemId);
            GateTrace("weak settle=ledger");
            return;
        }
        if (pending is null)
        {
            Note(Diag.WeakOrphan);
            if (ScreenCandidates.Enabled) ScreenCandidates.Gap(GapKind.WeakOrphan, "pk", w.StartUs, atUs);
            GateTrace("weak-orphan");
            return;
        }
        Resolve(pending, Resolution.Signal, atUs, Why.WindowClose);
        pending.Win = w;
        ResolveOlder(pending.Seq, atUs);
        Note(Diag.WeakSettle, pending.Item.Id);
        GateTrace("weak settle=pending");
    }

    private static (Entry?, Ledger?) OldestPk(long afterSeq, long beforeUs, bool originalOnly)
    {
        // 수신 순서라 가장 오래된 미해결 항목이 이벤트보다 늦게 왔으면 그 뒤도 모두 늦다.
        Entry? pending = null;
        foreach (Entry e in Queue)
            if (e.Gate == Gate.Pk && e.Seq > afterSeq && e.Res == Resolution.None
                && !(originalOnly && e.Counter))
            {
                if (e.Item.ReceivedUs < beforeUs) pending = e;
                break;
            }

        Ledger? owed = null;
        foreach (Ledger l in Unconsumed)
            if (l.Gate == Gate.Pk && l.Seq > afterSeq && l.ReceivedUs < beforeUs && !(originalOnly && l.Counter)
                && (owed is null || l.Seq < owed.Seq))
                owed = l;
        return (pending, owed);
    }

    // 화면은 수신 순서대로 재생하므로(설계 전제 1) 뒤 사건의 신호가 오면 앞 대기 줄은 이미 지나갔다.
    private static void ResolveOlder(long seq, long atUs)
    {
        foreach (Entry e in Queue)
        {
            if (e.Seq >= seq) break;
            if (e.Gate != Gate.None && e.Res == Resolution.None) Resolve(e, Resolution.Late, atUs, Why.LaterSignal);
        }
    }

    // why는 계측 사유다. 판단은 how만 쓴다(상한과 큐 예산은 둘 다 Cap).
    private static void Resolve(Entry e, Resolution how, long atUs, Why why)
    {
        e.Res = how;
        e.ResolvedUs = atUs;
        e.Why = why;
        e.CauseId = how == Resolution.Cap ? Null : _cause;
        ScheduleHealth.GateResolved(e.Item.Generation, GateIndex(e.Gate), HealthResolution(why));
        Decide(Act.Resolve, e.Item, why, e.CauseId, resolved: atUs);
        if (how == Resolution.Signal)
        {
            // 화면이 이 사건에 왔으면 앞 줄의 연출은 이미 지났다(설계 전제 1). 앞 일반 줄의 모델 대기를 풀지
            // 않으면 신호를 받은 줄도 그 뒤에서 기다린다(측정 4 여덟째 판 +2.88·+3.48초).
            ForceThrough(e.Seq, bySignal: true);
            return;
        }
        // 눈을 모르는 주사위는 올 신호가 없어 장부에 올릴 것도 없다. 라운드 팁은 뒤에 받은 페이지와만 짝지어져
        // 늦게 와도 다른 줄을 먼저 풀지 않는다. 차례 줄이 늦은 경로로 풀렸다면 그 배너는 이미 놓친 것이라,
        // 장부에 올리면 뒤 차례 줄이 모두 배너 하나씩 밀린다.
        if (e.Gate == Gate.Dice && e.Pips.Count == 0 || e.Gate is Gate.Round or Gate.Turn) return;
        // 자기 신호 없이 공개한 줄은 모두 장부에 올린다. 그 신호가 늦게 오면 다음 줄 대신 여기서 소비된다.
        Unconsumed.Add(new Ledger
        {
            Gate = e.Gate, Pips = new List<int>(e.Pips), Counter = e.Counter, Seq = e.Seq,
            ReceivedUs = e.Item.ReceivedUs, ItemId = e.Item.Id,
        });
    }

    private static void Stray(long atUs)
    {
        Note(Diag.Stray);
        if (ScreenCandidates.Enabled)
            ScreenCandidates.Gap(GapKind.Stray, "signal", atUs - ScreenCandidates.StrayWindowUs,
                                 atUs + ScreenCandidates.StrayWindowUs);
    }

    private static void GateTrace(string text)
    {
        if (TraceTiming) TimingTrace.Write($"gate {text}");
    }
}
