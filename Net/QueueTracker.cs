using System;
using System.Collections.Generic;
using System.Threading;
using AstralPartyBattleLog.Log;

namespace AstralPartyBattleLog.Net;

internal enum QueueRead : byte { Unreadable, Empty, Head }

internal readonly struct QueueObservation
{
    public readonly QueueRead Kind;
    public readonly int Cmd;
    public readonly long Down;
    public readonly int Size;

    public QueueObservation(QueueRead kind, int cmd = 0, long down = 0, int size = 0)
    {
        Kind = kind;
        Cmd = cmd;
        Down = down;
        Size = size;
    }

    public static readonly QueueObservation Unreadable = new(QueueRead.Unreadable);
}

internal readonly struct QueueSnapshot
{
    public readonly bool Live;
    public readonly long StartedThrough;
    public readonly long DoneThrough;
    public readonly long LastDrained;
    public readonly long OpenDice;

    public QueueSnapshot(bool live, long started, long done, long lastDrained, long openDice)
    {
        Live = live;
        StartedThrough = started;
        DoneThrough = done;
        LastDrained = lastDrained;
        OpenDice = openDice;
    }
}

internal readonly struct QueueHealth
{
    public long Frames { get; init; }
    public long LiveFrames { get; init; }
    public long Matched { get; init; }
    public long Unmatched { get; init; }
    public long SuspendReconnect { get; init; }
    public long SuspendSocket { get; init; }
    public long SuspendUnmatched { get; init; }
    public long Unreadable { get; init; }
    public long Overflow { get; init; }
    public int MaxSize { get; init; }
    public int MaxWaitFrames { get; init; }
}

// 게임이 서버 메시지를 대기열 맨 앞부터 처리한다는 사실로, 받은 메시지마다 "처리 시작/끝"을 도착 번호로 알려 준다.
// Unity·IL2CPP에 의존하지 않는다(하네스에 링크된다).
internal static class QueueTracker
{
    public const int EmptyMarginFrames = 3;
    public const int MaxPending = 4096;
    private const int MaxGroups = 8192;

    private readonly struct Arrival
    {
        public readonly long Num;
        public readonly int Cmd;
        public readonly long Down;
        public readonly int Frame;

        public Arrival(long num, int cmd, long down, int frame)
        {
            Num = num;
            Cmd = cmd;
            Down = down;
            Frame = frame;
        }
    }

    private static readonly object Gate = new();
    private static readonly object GroupGate = new();

    private static bool _enabled;
    private static bool _disabled;
    private static bool _live;
    private static int _frame;

    private static long _nextNum;
    private static List<Arrival> _intake = new();
    private static List<Arrival> _swap = new();
    private static bool _resyncPending;
    private static long _resyncPoint;
    private static long _suspendReconnect;
    private static long _suspendSocket;

    private static readonly List<Arrival> Pending = new();
    private static long _started;
    private static long _done;
    private static long _lastDrained;
    private static long _openDice;
    private static readonly List<(long Dice, long End)> Links = new();

    private static long _frames, _liveFrames, _matched, _unmatched, _suspendUnmatched, _unreadable, _overflow;
    private static int _maxSize, _maxWait;
    private static (int Cmd, long Down) _lastHead;
    private static bool _hasLastHead;
    private static (int Cmd, long Down) _lastUnmatched;
    private static bool _hasLastUnmatched;

    private static readonly Dictionary<long, (long Arrival, int Cmd, long Seq)> Groups = new();
    private static readonly Queue<(long Group, long Seq)> GroupOrder = new();
    private static long _groupSeq;

    public static bool Enabled => Volatile.Read(ref _enabled);

    public static void Configure(bool enabled)
    {
        lock (Gate)
        {
            _enabled = enabled && !_disabled;
            if (!_enabled) SetLive(false, "off");
        }
    }

    public static void Disable(string reason)
    {
        lock (Gate)
        {
            _disabled = true;
            _enabled = false;
            SetLive(false, "disabled:" + reason);
        }
    }

    private static bool IsImmediate(int cmd) => cmd switch
    {
        5004 or 5096 or 5238 or 5008 or 5150 or 5246 or 5328 or 1001 or 5012 => true,
        _ => false,
    };

    private static bool IsDiceBoundary(int cmd) => cmd switch
    {
        5022 or 1026 or 1007 or 1117 or 1015 or 1016 => true,
        _ => false,
    };

    public static long Arrive(int cmd, long down)
    {
        if (!Enabled) return -1;
        if (cmd == 5008)
        {
            Trigger("reconnect");
            return -1;
        }
        if (IsImmediate(cmd)) return -1;
        int frame = Volatile.Read(ref _frame);
        lock (Gate)
        {
            long num = ++_nextNum;
            _intake.Add(new Arrival(num, cmd, down, frame));
            return num;
        }
    }

    public static void SocketDropped()
    {
        if (Enabled) Trigger("socket");
    }

    // 재연결 동안 게임은 받은 메시지를 대기열 밖에 모았다가 나중에 한꺼번에 넣는다.
    // 그 사이 빈 목록을 끝으로 오해하지 않도록, 신호 뒤에 도착한 항목과 맞을 때까지 규칙을 멈춘다.
    private static void Trigger(string why)
    {
        lock (Gate)
        {
            _resyncPending = true;
            _resyncPoint = _nextNum;
            SetLive(false, why);
        }
    }

    public static void NoteGroup(long group, long arrival, int cmd)
    {
        lock (GroupGate)
        {
            long seq = ++_groupSeq;
            Groups[group] = (arrival, cmd, seq);
            GroupOrder.Enqueue((group, seq));
            if (Groups.Count <= MaxGroups) return;
            int drop = Groups.Count / 2;
            while (drop > 0 && GroupOrder.Count > 0)
            {
                var (g, s) = GroupOrder.Dequeue();
                if (Groups.TryGetValue(g, out var v) && v.Seq == s)
                {
                    Groups.Remove(g);
                    drop--;
                }
            }
        }
    }

    public static bool TryRefOf(long group, out long reference, out int cmd)
    {
        lock (GroupGate)
        {
            if (Groups.TryGetValue(group, out var v) && v.Arrival >= 0)
            {
                reference = v.Arrival;
                cmd = v.Cmd;
                return true;
            }
        }
        reference = 0;
        cmd = 0;
        return false;
    }

    public static void BeginFrame(int frame) => Volatile.Write(ref _frame, frame);

    public static QueueSnapshot Snapshot() => new(Volatile.Read(ref _live), _started, _done, _lastDrained, _openDice);

    public static int DrainLinks(List<(long Dice, long End)> into)
    {
        int n = Links.Count;
        if (n == 0) return 0;
        into.AddRange(Links);
        Links.Clear();
        return n;
    }

    public static QueueHealth Health
    {
        get
        {
            lock (Gate)
            {
                return new QueueHealth
                {
                    Frames = _frames,
                    LiveFrames = _liveFrames,
                    Matched = _matched,
                    Unmatched = _unmatched,
                    SuspendReconnect = _suspendReconnect,
                    SuspendSocket = _suspendSocket,
                    SuspendUnmatched = _suspendUnmatched,
                    Unreadable = _unreadable,
                    Overflow = _overflow,
                    MaxSize = _maxSize,
                    MaxWaitFrames = _maxWait,
                };
            }
        }
    }

    public static void Apply(in QueueObservation obs)
    {
        if (!Enabled) return;
        int frame = Volatile.Read(ref _frame);
        _frames++;
        if (obs.Size > _maxSize) _maxSize = obs.Size;

        Drain();

        switch (obs.Kind)
        {
            case QueueRead.Unreadable:
                _unreadable++;
                SetLive(false, "unreadable", frame);
                break;
            case QueueRead.Empty:
                OnEmpty(frame);
                break;
            default:
                OnHead(obs, frame);
                break;
        }

        if (Volatile.Read(ref _live)) _liveFrames++;
    }

    private static void Drain()
    {
        lock (Gate)
        {
            (_intake, _swap) = (_swap, _intake);
        }
        foreach (var a in _swap)
        {
            // 방 속도 2에서는 몬스터 주사위 연출을 건너뛰어 눈이 걷기로만 드러나므로, 주사위 줄의 끝은 뒤따르는 걷기의 끝이다.
            if (_openDice != 0)
            {
                if (a.Cmd == 5028)
                {
                    Links.Add((_openDice, a.Num));
                    _openDice = 0;
                }
                else if (IsDiceBoundary(a.Cmd))
                {
                    Links.Add((_openDice, _openDice));
                    _openDice = 0;
                }
            }
            if (a.Cmd == 5022) _openDice = a.Num;
            Pending.Add(a);
            _lastDrained = a.Num;
        }
        _swap.Clear();

        if (Pending.Count > MaxPending)
        {
            int drop = Pending.Count - MaxPending;
            _overflow += drop;
            Finish(drop, CurrentFrame());
        }
    }

    private static int CurrentFrame() => Volatile.Read(ref _frame);

    // 앞 count개를 끝난 것으로 치고 목록에서 뺀다. 아직 시작으로 안 친 항목의 대기 프레임도 여기서 잰다.
    private static void Finish(int count, int frame)
    {
        if (count <= 0) return;
        long last = Pending[count - 1].Num;
        MarkStarted(last, frame);
        if (last > _done) _done = last;
        Pending.RemoveRange(0, count);
    }

    private static void MarkStarted(long through, int frame)
    {
        if (through <= _started) return;
        foreach (var e in Pending)
        {
            if (e.Num > through) break;
            if (e.Num <= _started) continue;
            int wait = frame - e.Frame;
            if (wait > _maxWait) _maxWait = wait;
        }
        _started = through;
    }

    private static void OnEmpty(int frame)
    {
        _hasLastHead = false;
        _hasLastUnmatched = false;
        int n = 0;
        while (n < Pending.Count && frame - Pending[n].Frame >= EmptyMarginFrames) n++;
        if (n > 0)
        {
            Finish(n, frame);
            if (TimingTrace.Enabled) TimingTrace.Write($"queue empty done={_done} f={frame}");
        }
        lock (Gate)
        {
            if (!_resyncPending) SetLive(true, "empty", frame);
        }
    }

    private static void OnHead(in QueueObservation obs, int frame)
    {
        int idx = -1;
        for (int i = 0; i < Pending.Count; i++)
        {
            if (Pending[i].Cmd == obs.Cmd && Pending[i].Down == obs.Down)
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
        {
            _unmatched++;
            if (TimingTrace.Enabled && (!_hasLastUnmatched || _lastUnmatched != (obs.Cmd, obs.Down)))
                TimingTrace.Write($"queue unmatched cmd={obs.Cmd} down={obs.Down} size={obs.Size} f={frame}");
            _lastUnmatched = (obs.Cmd, obs.Down);
            _hasLastUnmatched = true;
            _hasLastHead = false;
            SetLive(false, "unmatched", frame);
            return;
        }

        _matched++;
        _hasLastUnmatched = false;
        var head = Pending[idx];
        if (TimingTrace.Enabled && (!_hasLastHead || _lastHead != (obs.Cmd, obs.Down)))
            TimingTrace.Write($"queue head seq={head.Num} cmd={obs.Cmd} down={obs.Down} size={obs.Size} f={frame}");
        _lastHead = (obs.Cmd, obs.Down);
        _hasLastHead = true;

        Finish(idx, frame);
        MarkStarted(head.Num, frame);

        lock (Gate)
        {
            if (_resyncPending && head.Num <= _resyncPoint) return;
            _resyncPending = false;
            SetLive(true, "match", frame);
        }
    }

    private static void SetLive(bool live, string why, int frame = -1)
    {
        lock (Gate)
        {
            if (_live == live) return;
            Volatile.Write(ref _live, live);
            if (!live)
            {
                switch (why)
                {
                    case "reconnect": _suspendReconnect++; break;
                    case "socket": _suspendSocket++; break;
                    case "unmatched": _suspendUnmatched++; break;
                }
            }
            if (TimingTrace.Enabled)
                TimingTrace.Write($"queue live={(live ? 1 : 0)} why={why} f={(frame >= 0 ? frame : CurrentFrame())}");
        }
    }

    internal static void ResetForTest()
    {
        lock (Gate)
        {
            _enabled = false;
            _disabled = false;
            _live = false;
            _frame = 0;
            _nextNum = 0;
            _intake.Clear();
            _swap.Clear();
            _resyncPending = false;
            _resyncPoint = 0;
            _suspendReconnect = 0;
            _suspendSocket = 0;
            Pending.Clear();
            _started = 0;
            _done = 0;
            _lastDrained = 0;
            _openDice = 0;
            Links.Clear();
            _frames = _liveFrames = _matched = _unmatched = _suspendUnmatched = _unreadable = _overflow = 0;
            _maxSize = 0;
            _maxWait = 0;
            _hasLastHead = false;
            _hasLastUnmatched = false;
        }
        lock (GroupGate)
        {
            Groups.Clear();
            GroupOrder.Clear();
            _groupSeq = 0;
        }
    }
}
