using System;
using System.Collections.Generic;
using System.Threading;
using AstralPartyBattleLog.Log;

namespace AstralPartyBattleLog.UI;

public enum ScreenSignal { DiceFace, Window, Hit }

// 주사위·PK 결과 줄을 화면 신호로 공개한다. 규칙과 근거는 docs/SIGNAL-GATING.md.
internal static partial class OverlaySchedule
{
    private enum Gate { None, Dice, Pk }
    private enum Resolution { None, Signal, Late, Cap }

    private sealed class Entry
    {
        public Item Item;
        public long Seq;
        public Gate Gate;
        public int Pip;
        public bool Counter;
        public bool Scheduled;
        public long DueUs, FloorUs;
        public Resolution Res;
        public long ResolvedUs;
        public Window? Win;
    }

    private sealed class Ledger
    {
        public Gate Gate;
        public int Pip;
        public bool Counter;
        public long Seq;
        public long ReceivedUs;
    }

    private sealed class Window
    {
        public int Strikes;
        public long LastSeq = -1;
        public bool Ended;
        public long EndUs;
    }

    private readonly struct PendingSignal
    {
        public readonly ScreenSignal Kind;
        public readonly int Pip;
        public readonly bool On;
        public readonly IntPtr Instance;
        public readonly long AtUs;
        public readonly int Generation;

        public PendingSignal(ScreenSignal kind, int pip, bool on, IntPtr instance, long atUs, int generation)
        {
            Kind = kind; Pip = pip; On = on; Instance = instance; AtUs = atUs; Generation = generation;
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

    public static bool ScreenSignalsEnabled => _gating;

    // 후킹 설치에 실패하면 모델 경로로 돌아간다. 이 경우 조기 표시 위험은 측정 4 넷째 판과 같다.
    public static void DisableScreenSignals() => _gating = false;

    // 메인 스레드(SetActive 후킹)에서 온다. Pump가 수신 줄을 먼저 받은 뒤 처리한다.
    public static void Screen(ScreenSignal kind, int pip, bool on, IntPtr instance)
    {
        if (!_gating) return;
        Signals.Add(new PendingSignal(kind, pip, on, instance, Volatile.Read(ref _nowUs),
                                      Volatile.Read(ref _generation)));
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
    }

    private static void PumpGated(long now, Action<string> line, Action<int> page, Action clear)
    {
        int generation = Volatile.Read(ref _generation);
        if (generation != _gateGeneration) ResetGating(generation);

        while (Incoming.TryDequeue(out Item item))
        {
            if (IsStale(item)) continue;
            long seq = ++_seq;
            if (item.Signal == Signal.Sync) _forceThroughSeq = seq;
            Queue.Add(NewEntry(item, seq));
        }

        foreach (PendingSignal s in Signals)
            if (s.Generation == _gateGeneration) Handle(s);
        Signals.Clear();

        foreach (Entry e in Queue)
            if (e.Gate != Gate.None && e.Res == Resolution.None && now - e.Item.ReceivedUs >= GateCapUs)
            {
                Resolve(e, Resolution.Cap, now);
                GateTrace($"timeout kind={e.Gate.ToString().ToLowerInvariant()} waited={(now - e.Item.ReceivedUs) / 1000}ms");
            }

        while (Queue.Count > 0)
        {
            Entry head = Queue[0];
            if (IsStale(head.Item))
            {
                Queue.RemoveAt(0);
                continue;
            }

            if (head.Gate != Gate.None)
            {
                if (head.Res == Resolution.None)
                {
                    if (Queue.Count <= MaxWaiting) break;
                    Resolve(head, Resolution.Cap, now);
                }
                ReleaseGated(head, now, line, page, clear);
                continue;
            }

            if (!BarrierLifted(now) && Queue.Count <= MaxWaiting) break;

            if (!head.Scheduled)
            {
                head.DueUs = DueOf(head.Item, out head.FloorUs);
                head.Scheduled = true;
            }
            long ready = head.Seq <= _forceThroughSeq ? Math.Max(head.FloorUs, _anchorUs) : head.DueUs;
            if (ready > now && Queue.Count <= MaxWaiting) break;

            Queue.RemoveAt(0);
            Release(new Scheduled(head.Item, head.DueUs, head.FloorUs, head.Seq), now, line, page, clear);
        }
    }

    private static Entry NewEntry(Item item, long seq)
    {
        var e = new Entry { Item = item, Seq = seq };
        if (item.Signal != Signal.Line) return e;
        if (item.Kind == LineKind.Dice)
        {
            e.Gate = Gate.Dice;
            e.Pip = item.Detail;
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
        Anchor(_barrier.Ended ? _barrier.EndUs : now);
        _barrier = null;
        return true;
    }

    private static void Anchor(long atUs)
    {
        _anchorUs = Math.Max(_anchorUs, atUs);
        _cursorUs = Math.Max(_cursorUs, atUs);
    }

    private static void ReleaseGated(Entry head, long now, Action<string> line, Action<int> page, Action clear)
    {
        Queue.RemoveAt(0);
        Release(new Scheduled(head.Item, now, now, head.Seq), now, line, page, clear);
        GateTrace($"release kind={head.Gate.ToString().ToLowerInvariant()} by={head.Res.ToString().ToLowerInvariant()} "
                  + $"waited={(now - head.Item.ReceivedUs) / 1000}ms");

        // PK 피해 줄은 PK 결과와 같은 그룹이라 함께 공개한다.
        while (Queue.Count > 0 && Queue[0].Gate == Gate.None && Queue[0].Item.Group >= 0
               && Queue[0].Item.Group == head.Item.Group && !IsStale(Queue[0].Item))
        {
            Entry follower = Queue[0];
            Queue.RemoveAt(0);
            Release(new Scheduled(follower.Item, now, now, follower.Seq), now, line, page, clear);
        }
        _lastGroup = -1;

        if (head.Res != Resolution.Signal)
        {
            Anchor(now);
            return;
        }
        if (head.Gate == Gate.Dice)
        {
            Anchor(head.ResolvedUs + (long)(DiceTailUs / head.Item.Speed));
            return;
        }
        if (head.Win is { Ended: true } done) Anchor(done.EndUs);
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
                    _window = new Window();
                }
                else CloseWindow(s.AtUs);
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

    private static void CloseWindow(long atUs)
    {
        if (_window is null) return;
        if (_window.Strikes == 0) GateTrace("weak");
        _window.Ended = true;
        _window.EndUs = atUs;
        _window = null;
    }

    private static void MatchDice(int pip, long atUs)
    {
        Entry? pending = null;
        foreach (Entry e in Queue)
            if (e.Gate == Gate.Dice && e.Res == Resolution.None && e.Pip == pip && e.Item.ReceivedUs < atUs)
            {
                pending = e;
                break;
            }
        Ledger? owed = Unconsumed.Find(l => l.Gate == Gate.Dice && l.Pip == pip && l.ReceivedUs < atUs);

        if (owed is not null && (pending is null || owed.Seq < pending.Seq))
        {
            Unconsumed.Remove(owed);
            GateTrace($"consume kind=dice pip={pip}");
            return;
        }
        if (pending is null)
        {
            GateTrace($"stray kind=dice pip={pip}");
            return;
        }
        Resolve(pending, Resolution.Signal, atUs);
        ResolveOlder(pending.Seq, atUs);
    }

    private static void Strike(long atUs)
    {
        if (_window is null)
        {
            GateTrace("outside");
            return;
        }
        Window w = _window;
        w.Strikes++;

        if (w.Strikes == 1)
        {
            (Entry? pending, Ledger? owed) = OldestPk(afterSeq: -1, atUs);
            if (owed is not null && (pending is null || owed.Seq < pending.Seq))
            {
                Unconsumed.Remove(owed);
                GateTrace("consume kind=pk");
                return;
            }
            if (pending is null) return;
            Resolve(pending, Resolution.Signal, atUs);
            pending.Win = w;
            w.LastSeq = pending.Seq;
            ResolveOlder(pending.Seq, atUs);
            return;
        }

        // 두 번째 이후 스트라이크는 이 창에서 방금 공개한 PK 바로 다음 항목이 반격일 때만 쓴다.
        if (w.LastSeq < 0)
        {
            GateTrace("extra-strike");
            return;
        }
        (Entry? next, Ledger? nextOwed) = OldestPk(afterSeq: w.LastSeq, long.MaxValue);
        bool owedFirst = nextOwed is not null && (next is null || nextOwed.Seq < next.Seq);
        if (owedFirst && nextOwed!.Counter && nextOwed.ReceivedUs < atUs)
        {
            Unconsumed.Remove(nextOwed);
            w.LastSeq = nextOwed.Seq;
            GateTrace("consume kind=pk counter=1");
            return;
        }
        if (!owedFirst && next is not null && next.Counter && next.Item.ReceivedUs < atUs)
        {
            Resolve(next, Resolution.Signal, atUs);
            next.Win = w;
            w.LastSeq = next.Seq;
            return;
        }
        GateTrace("extra-strike");
    }

    private static (Entry?, Ledger?) OldestPk(long afterSeq, long beforeUs)
    {
        // 수신 순서라 가장 오래된 미해결 항목이 이벤트보다 늦게 왔으면 그 뒤도 모두 늦다.
        Entry? pending = null;
        foreach (Entry e in Queue)
            if (e.Gate == Gate.Pk && e.Seq > afterSeq && e.Res == Resolution.None)
            {
                if (e.Item.ReceivedUs < beforeUs) pending = e;
                break;
            }

        Ledger? owed = null;
        foreach (Ledger l in Unconsumed)
            if (l.Gate == Gate.Pk && l.Seq > afterSeq && l.ReceivedUs < beforeUs && (owed is null || l.Seq < owed.Seq))
                owed = l;
        return (pending, owed);
    }

    // 화면은 수신 순서대로 재생하므로(설계 전제 1) 뒤 사건의 신호가 오면 앞 대기 줄은 이미 지나갔다.
    private static void ResolveOlder(long seq, long atUs)
    {
        foreach (Entry e in Queue)
        {
            if (e.Seq >= seq) break;
            if (e.Gate != Gate.None && e.Res == Resolution.None) Resolve(e, Resolution.Late, atUs);
        }
    }

    private static void Resolve(Entry e, Resolution how, long atUs)
    {
        e.Res = how;
        e.ResolvedUs = atUs;
        if (how == Resolution.Signal) return;
        // 자기 신호 없이 공개한 줄은 모두 장부에 올린다. 그 신호가 늦게 오면 다음 줄 대신 여기서 소비된다.
        Unconsumed.Add(new Ledger
        {
            Gate = e.Gate, Pip = e.Pip, Counter = e.Counter, Seq = e.Seq, ReceivedUs = e.Item.ReceivedUs,
        });
    }

    private static void GateTrace(string text)
    {
        if (TraceTiming) TimingTrace.Write($"gate {text}");
    }
}
