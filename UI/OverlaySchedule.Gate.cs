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
    }

    private sealed class Ledger
    {
        public Gate Gate;
        public List<int> Pips = new();
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
    // 차례 배너는 게임이 2초 띄운다. 같은 이름의 생각 중 팁과 가르는 기준이다.
    private const long BannerUs = 2_000_000;
    private const long BannerToleranceUs = 150_000;
    private const int BannerVotes = 2;

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

    // 위쪽 팁 오브젝트 두 개(차례 배너·생각 중 팁)는 이름이 같다. 2초 떠 있다 꺼진 쪽을 배너로 학습한다.
    private static readonly Dictionary<IntPtr, long> TopOnUs = new();
    private static readonly Dictionary<IntPtr, int> TopVotes = new();
    private static IntPtr _banner;

    public static bool UsesScreenSignals => _gating;

    // 후킹 설치 실패나 반복 오류로 신호가 끊기면 모델 경로로 돌아간다. 조기 표시 위험은 측정 4 넷째 판과 같다.
    // 대기 중인 줄은 신호를 더 받을 수 없으므로 모델 예약으로 넘긴다. 메인 스레드에서만 부른다.
    public static void FallBackToModel()
    {
        if (!_gating) return;
        _gating = false;
        _barrier = null;
        foreach (Entry e in Queue)
        {
            if (IsStale(e.Item)) continue;
            if (!e.Scheduled) e.DueUs = DueOf(e.Item, out e.FloorUs);
            Waiting.Enqueue(new Scheduled(e.Item, e.DueUs, e.FloorUs, e.Seq));
        }
        ResetGating(_gateGeneration);
        Signals.Clear();
    }

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

    // 오브젝트 포인터는 씬이 바뀌면 재사용될 수 있다. 판 세대가 아니라 씬에 묶는다.
    public static void ForgetBanner()
    {
        TopOnUs.Clear();
        TopVotes.Clear();
        _banner = IntPtr.Zero;
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

            bool toCounter = false;
            if (!BarrierLifted(now) && Queue.Count <= MaxWaiting)
            {
                toCounter = SyncBeforeCounter();
                if (!toCounter) break;
            }

            if (!head.Scheduled)
            {
                head.DueUs = DueOf(head.Item, out head.FloorUs);
                head.Scheduled = true;
            }
            long ready = head.Seq <= _forceThroughSeq ? Math.Max(head.FloorUs, _anchorUs) : head.DueUs;
            if (!toCounter && ready > now && Queue.Count <= MaxWaiting) break;

            Queue.RemoveAt(0);
            Release(new Scheduled(head.Item, head.DueUs, head.FloorUs, head.Seq), now, line, page, clear);
        }
    }

    private static Entry NewEntry(Item item, long seq)
    {
        var e = new Entry { Item = item, Seq = seq };
        // 1라운드는 넘어올 앞 라운드 줄이 없고, 페이지가 전투 씬 로드 전에 와서 오버레이 표시 판단에 쓰인다.
        if (item.Signal == Signal.Page && item.Round >= 2)
        {
            e.Gate = Gate.Round;
            return e;
        }
        if (item.Signal != Signal.Line) return e;
        // 배너를 학습하기 전에는 기다릴 신호가 없으므로 지금처럼 추정으로 낸다.
        if (item.Kind == LineKind.Turn && _banner != IntPtr.Zero && item.Detail == 0)
        {
            e.Gate = Gate.Turn;
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
                    _window = new Window();
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
            GateTrace($"consume kind=dice pip={pip}");
            return;
        }
        if (pending is null)
        {
            GateTrace($"stray kind=dice pip={pip}");
            return;
        }
        pending.Pips.Remove(pip);
        ResolveOlder(pending.Seq, atUs);
        if (pending.Pips.Count == 0) Resolve(pending, Resolution.Signal, atUs);
    }

    // 라운드 팁은 공용 안내 팁이라 다른 안내에도 켜진다. 대기 중인 라운드 페이지가 있을 때만 짝짓는다.
    // 가장 늦게 받은 페이지에 짝짓고, 그 앞 대기 줄(신호 없는 몬스터 주사위 등)은 늦은 경로로 함께 푼다.
    private static void MatchRound(long atUs)
    {
        Entry? page = null;
        foreach (Entry e in Queue)
            if (e.Gate == Gate.Round && e.Res == Resolution.None && e.Item.ReceivedUs < atUs) page = e;
        if (page is null)
        {
            GateTrace("stray kind=round");
            return;
        }
        // 게임은 앞 연출이 모두 끝난 뒤 라운드 팁을 띄운다. PK 창 꺼짐을 놓쳤어도 장벽을 붙잡을 이유가 없다.
        _barrier = null;
        ResolveOlder(page.Seq, atUs);
        Resolve(page, Resolution.Signal, atUs);
    }

    private static void TopTip(PendingSignal s)
    {
        if (s.On)
        {
            TopOnUs[s.Instance] = s.AtUs;
            if (s.Instance == _banner) MatchTurn(s.AtUs);
            return;
        }
        if (!TopOnUs.Remove(s.Instance, out long onUs) || _banner != IntPtr.Zero) return;
        // 게임 대기가 배속을 따르는지 확인하지 못해 실시간·배속 보정 길이 둘 다 받는다.
        long shown = s.AtUs - onUs;
        float speed = Volatile.Read(ref _speed);
        if (Math.Abs(shown - BannerUs) > BannerToleranceUs
            && Math.Abs((long)(shown * speed) - BannerUs) > BannerToleranceUs) return;
        int votes = TopVotes[s.Instance] = TopVotes.GetValueOrDefault(s.Instance) + 1;
        if (votes < BannerVotes) return;
        foreach (KeyValuePair<IntPtr, int> other in TopVotes)
            if (other.Key != s.Instance && other.Value >= votes) return;
        _banner = s.Instance;
        GateTrace("banner learned");
    }

    // 차례 시작 줄은 수신 순서대로 배너 하나씩 대응한다. 앞 대기 줄(이동만 한 몬스터 주사위 등)은 늦은 경로로 푼다.
    private static void MatchTurn(long atUs)
    {
        Entry? turn = null;
        foreach (Entry e in Queue)
            if (e.Gate == Gate.Turn && e.Res == Resolution.None)
            {
                if (e.Item.ReceivedUs < atUs) turn = e;
                break;
            }
        if (turn is null)
        {
            GateTrace("stray kind=turn");
            return;
        }
        _barrier = null;
        ResolveOlder(turn.Seq, atUs);
        Resolve(turn, Resolution.Signal, atUs);
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
            (Entry? pending, Ledger? owed) = OldestPk(afterSeq: -1, atUs, originalOnly: true);
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
        (Entry? next, Ledger? nextOwed) = OldestPk(afterSeq: w.LastSeq, long.MaxValue, originalOnly: false);
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

    // 창마다 원래 PK 하나가 대응한다(반격은 같은 창을 쓴다). 타격 없이 닫힌 창의 꺼짐은 그 PK 연출이 끝난
    // 시점이라 여기서 공개한다. 대기로 두면 다음 신호까지 늦고(측정 4 여덟째 판 +8.2초), 장부 항목을 정산하지
    // 않으면 타격 없는 캐릭터의 결과가 뒤 PK의 타격을 대신 소비하는 연쇄가 생긴다(일곱째 판).
    private static void SettleWeak(Window w, long atUs)
    {
        (Entry? pending, Ledger? owed) = OldestPk(afterSeq: -1, atUs, originalOnly: true);
        if (owed is not null && (pending is null || owed.Seq < pending.Seq))
        {
            Unconsumed.Remove(owed);
            GateTrace("weak settle=ledger");
            return;
        }
        if (pending is null)
        {
            GateTrace("weak-orphan");
            return;
        }
        Resolve(pending, Resolution.Signal, atUs);
        pending.Win = w;
        ResolveOlder(pending.Seq, atUs);
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
            if (e.Gate != Gate.None && e.Res == Resolution.None) Resolve(e, Resolution.Late, atUs);
        }
    }

    private static void Resolve(Entry e, Resolution how, long atUs)
    {
        e.Res = how;
        e.ResolvedUs = atUs;
        if (how == Resolution.Signal)
        {
            // 화면이 이 사건에 왔으면 앞 줄의 연출은 이미 지났다(설계 전제 1). 앞 일반 줄의 모델 대기를 풀지
            // 않으면 신호를 받은 줄도 그 뒤에서 기다린다(측정 4 여덟째 판 +2.88·+3.48초).
            _forceThroughSeq = Math.Max(_forceThroughSeq, e.Seq);
            return;
        }
        // 눈을 모르는 주사위는 올 신호가 없어 장부에 올릴 것도 없다. 라운드 팁은 뒤에 받은 페이지와만 짝지어져
        // 늦게 와도 다른 줄을 먼저 풀지 않는다.
        if (e.Gate == Gate.Dice && e.Pips.Count == 0 || e.Gate is Gate.Round or Gate.Turn) return;
        // 자기 신호 없이 공개한 줄은 모두 장부에 올린다. 그 신호가 늦게 오면 다음 줄 대신 여기서 소비된다.
        Unconsumed.Add(new Ledger
        {
            Gate = e.Gate, Pips = new List<int>(e.Pips), Counter = e.Counter, Seq = e.Seq,
            ReceivedUs = e.Item.ReceivedUs,
        });
    }

    private static void GateTrace(string text)
    {
        if (TraceTiming) TimingTrace.Write($"gate {text}");
    }
}
