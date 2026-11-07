using System;
using System.Collections.Generic;
using System.Threading;
using AstralPartyBattleLog.Log;

namespace AstralPartyBattleLog.Net;

internal enum QueueRead : byte { Unreadable, Empty, Head }

internal enum QueueMode : byte { Off, Live, Frozen }

internal enum FreezeReason : byte { Unmatched, Unreadable, Reconnect, Exit, Error, Socket }

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
    public readonly QueueMode Mode;
    public readonly long StartedThrough;
    public readonly long DoneThrough;

    public QueueSnapshot(QueueMode mode, long started, long done)
    {
        Mode = mode;
        StartedThrough = started;
        DoneThrough = done;
    }
}

internal readonly struct QueueHealth
{
    public long Frames { get; init; }
    public long LiveFrames { get; init; }
    public long FrozenFrames { get; init; }
    public long Matched { get; init; }
    public long Unmatched { get; init; }
    public long Unreadable { get; init; }
    public long Overflow { get; init; }
    public long FreezeUnmatched { get; init; }
    public long FreezeUnreadable { get; init; }
    public long FreezeReconnect { get; init; }
    public long FreezeExit { get; init; }
    public long FreezeError { get; init; }
    public long FreezeSocket { get; init; }
    public int Size { get; init; }
    public int HeadWaitTicks { get; init; }
}

internal static class QueueTracker
{
    public const int EmptyTicksToResume = 6;
    public const int ArrivalMarginTicks = 3;
    public const int MaxPending = 4096;
    private const int MaxGroups = 8192;

    private readonly struct Arrival
    {
        public readonly long Num;
        public readonly int Cmd;
        public readonly long Down;
        public readonly int Tick;

        public Arrival(long num, int cmd, long down, int tick)
        {
            Num = num;
            Cmd = cmd;
            Down = down;
            Tick = tick;
        }
    }

    private static readonly object Gate = new();
    private static readonly object GroupGate = new();

    // Gate로 보호: IO 스레드(Arrive, SocketDropped)와 메인 스레드가 함께 만진다.
    private static bool _enabled;
    private static bool _disabled;
    private static QueueMode _mode;
    private static long _pubStarted;
    private static long _pubDone;
    private static long _nextNum;
    private static List<Arrival> _intake = new();
    private static List<Arrival> _swap = new();
    private static bool _awaitSync;
    private static bool _reconnectPending;
    private static long _reference;
    private static bool _clearRequested;
    private static long _freezeUnmatched, _freezeUnreadable, _freezeReconnect, _freezeExit, _freezeError, _freezeSocket;

    private static int _tick;
    private static int _frame;

    // 아래는 메인 스레드 전용이다. Apply·DrainLinks·Health 밖에서 만지지 않는다.
    private static readonly List<Arrival> Pending = new();
    private static long _started;
    private static long _done;
    private static int _emptyRun;
    private static readonly List<long> Open = new();
    private static readonly List<(long Opener, long End)> Links = new();

    private static long _frames, _liveFrames, _frozenFrames, _matched, _unmatched, _unreadable, _overflow;
    private static int _size;
    private static long _headNum;
    private static int _headStartTick;
    private static int _headWait;
    private static (int Cmd, long Down) _lastHead;
    private static bool _hasLastHead;
    private static (int Cmd, long Down) _lastUnmatched;
    private static bool _hasLastUnmatched;

    private static readonly Dictionary<long, (long Arrival, int Cmd)> Groups = new();
    private static readonly Queue<long> GroupOrder = new();

    public static bool Enabled => Volatile.Read(ref _enabled);

    internal static int GroupOrderLength
    {
        get { lock (GroupGate) return GroupOrder.Count; }
    }

    public static void Configure(bool enabled)
    {
        bool changed = false;
        lock (Gate)
        {
            _enabled = enabled && !_disabled;
            if (!_enabled) changed = TurnOff();
        }
        if (changed) TraceState(QueueMode.Off, "config");
    }

    public static void Disable(string reason)
    {
        bool changed;
        lock (Gate)
        {
            _disabled = true;
            _enabled = false;
            changed = TurnOff();
        }
        if (changed) TraceState(QueueMode.Off, "disabled", reason);
    }

    private static bool TurnOff()
    {
        _intake.Clear();
        _clearRequested = true;
        return Switch(QueueMode.Off);
    }

    private static bool IsImmediate(int cmd) => cmd switch
    {
        5004 or 5096 or 5238 or 5008 or 5150 or 5246 or 5328 or 1001 or 5012 => true,
        _ => false,
    };

    private static bool IsBoundary(int cmd) => cmd switch
    {
        5022 or 1026 or 1007 or 1117 or 1015 or 1016 => true,
        _ => false,
    };

    private static bool IsOpener(int cmd) => cmd switch
    {
        5022 or 1096 or 5044 => true,
        _ => false,
    };

    public static long Arrive(int cmd, long down, int err)
    {
        if (!Enabled) return -1;
        int tick = Volatile.Read(ref _tick);
        long num = -1;
        FreezeReason? why = null;
        bool changed = false;
        lock (Gate)
        {
            if (!IsImmediate(cmd))
            {
                num = ++_nextNum;
                _intake.Add(new Arrival(num, cmd, down, tick));
            }
            if (cmd == 5008)
            {
                _awaitSync = false;
                why = FreezeReason.Reconnect;
            }
            else if (cmd == 5012) why = FreezeReason.Exit;
            else if (err != 0) why = FreezeReason.Error;
            if (why != null) changed = Signal(why.Value, raiseReference: true);
        }
        if (changed) TraceState(QueueMode.Frozen, Token(why!.Value));
        return num;
    }

    public static void SocketDropped()
    {
        if (!Enabled) return;
        bool changed;
        lock (Gate)
        {
            // 재연결 중에는 게임이 방 상태를 NONE으로 두고 전투 메시지를 화면 없이 흘려보내므로(OnlineLogic.cs:371),
            // 다음 5008이 와서 기준점이 정해질 때까지 어떤 항목도 풀어 주지 않는다.
            _awaitSync = true;
            changed = Signal(FreezeReason.Socket, raiseReference: false);
        }
        if (changed) TraceState(QueueMode.Frozen, Token(FreezeReason.Socket));
    }

    private static bool Signal(FreezeReason why, bool raiseReference)
    {
        if (raiseReference && _nextNum > _reference) _reference = _nextNum;
        _reconnectPending = true;
        if (_mode == QueueMode.Off) return false;
        switch (why)
        {
            case FreezeReason.Reconnect: _freezeReconnect++; break;
            case FreezeReason.Exit: _freezeExit++; break;
            case FreezeReason.Error: _freezeError++; break;
            case FreezeReason.Socket: _freezeSocket++; break;
        }
        return Switch(QueueMode.Frozen);
    }

    private static bool Switch(QueueMode to)
    {
        if (to == QueueMode.Frozen && _mode == QueueMode.Off) return false;
        if (_mode == to) return false;
        _mode = to;
        if (to == QueueMode.Live)
        {
            _pubStarted = _started;
            _pubDone = _done;
        }
        return true;
    }

    private static string Token(FreezeReason why) => why switch
    {
        FreezeReason.Unmatched => "unmatched",
        FreezeReason.Unreadable => "unreadable",
        FreezeReason.Reconnect => "reconnect",
        FreezeReason.Exit => "exit",
        FreezeReason.Error => "error",
        _ => "socket",
    };

    private static void TraceState(QueueMode mode, string why, string? detail = null)
    {
        if (!TimingTrace.Enabled) return;
        string name = mode switch { QueueMode.Live => "live", QueueMode.Frozen => "frozen", _ => "off" };
        string extra = detail is null ? "" : " detail=" + detail.Replace(' ', '_');
        TimingTrace.Write($"queue state={name} why={why}{extra} f={Volatile.Read(ref _frame)}");
    }

    public static void NoteGroup(long group, long arrival, int cmd)
    {
        lock (GroupGate)
        {
            bool known = Groups.ContainsKey(group);
            Groups[group] = (arrival, cmd);
            if (known) return;
            GroupOrder.Enqueue(group);
            if (Groups.Count <= MaxGroups) return;
            int drop = Groups.Count / 2;
            while (drop-- > 0 && GroupOrder.Count > 0)
                Groups.Remove(GroupOrder.Dequeue());
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

    public static void BeginFrame(int unityFrame)
    {
        Volatile.Write(ref _tick, _tick + 1);
        Volatile.Write(ref _frame, unityFrame);
    }

    public static QueueSnapshot Snapshot()
    {
        lock (Gate) return new QueueSnapshot(_mode, _pubStarted, _pubDone);
    }

    public static int DrainLinks(List<(long Opener, long End)> into)
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
            // 멈춤 사유 수는 IO 스레드도 올리므로 잠금 안에서 읽고, 나머지는 메인 스레드 전용이다.
            lock (Gate)
            {
                return new QueueHealth
                {
                    Frames = _frames,
                    LiveFrames = _liveFrames,
                    FrozenFrames = _frozenFrames,
                    Matched = _matched,
                    Unmatched = _unmatched,
                    Unreadable = _unreadable,
                    Overflow = _overflow,
                    FreezeUnmatched = _freezeUnmatched,
                    FreezeUnreadable = _freezeUnreadable,
                    FreezeReconnect = _freezeReconnect,
                    FreezeExit = _freezeExit,
                    FreezeError = _freezeError,
                    FreezeSocket = _freezeSocket,
                    Size = _size,
                    HeadWaitTicks = _headWait,
                };
            }
        }
    }

    public static void Apply(in QueueObservation obs)
    {
        if (!Enabled) return;
        int tick = Volatile.Read(ref _tick);
        int frame = Volatile.Read(ref _frame);
        _frames++;
        _size = obs.Kind == QueueRead.Head ? obs.Size : 0;

        Drain(frame);

        switch (obs.Kind)
        {
            case QueueRead.Unreadable:
                OnUnreadable();
                break;
            case QueueRead.Empty:
                OnEmpty(tick, frame);
                break;
            default:
                OnHead(obs, tick, frame);
                break;
        }

        lock (Gate)
        {
            if (_mode == QueueMode.Live)
            {
                _pubStarted = _started;
                _pubDone = _done;
                _liveFrames++;
            }
            else if (_mode == QueueMode.Frozen) _frozenFrames++;
        }
    }

    private static void Drain(int frame)
    {
        bool clear;
        lock (Gate)
        {
            clear = _clearRequested;
            _clearRequested = false;
            (_intake, _swap) = (_swap, _intake);
        }
        if (clear) ClearInternal();

        foreach (var a in _swap)
        {
            // 열린 메시지의 끝은 뒤따르는 걷기의 끝이다. 5022는 방 속도 2에서 몬스터 주사위 연출을 건너뛰어 눈이 걷기로만 드러나고,
            // 1096은 처리기가 효과를 저장만 하고 걷기가 칸마다 보여 주며, 5044는 이동량이 걷기로 드러난다.
            if (Open.Count > 0)
            {
                if (a.Cmd == 5028) CloseAll(a.Num, frame);
                else if (IsBoundary(a.Cmd)) CloseAll(-1, frame);
            }
            if (IsOpener(a.Cmd)) Open.Add(a.Num);
            Pending.Add(a);
        }
        _swap.Clear();

        if (Pending.Count > MaxPending)
        {
            int drop = Pending.Count - MaxPending;
            _overflow += drop;
            Finish(drop);
        }
    }

    private static void CloseAll(long end, int frame)
    {
        foreach (long opener in Open)
        {
            long e = end >= 0 ? end : opener;
            Links.Add((opener, e));
            if (TimingTrace.Enabled) TimingTrace.Write($"queue link seq={opener} end={e} f={frame}");
        }
        Open.Clear();
    }

    private static void ClearInternal()
    {
        Pending.Clear();
        Open.Clear();
        _emptyRun = 0;
        _headNum = 0;
        _headWait = 0;
        _hasLastHead = false;
        _hasLastUnmatched = false;
    }

    private static void Finish(int count)
    {
        if (count <= 0) return;
        long last = Pending[count - 1].Num;
        if (last > _started) _started = last;
        if (last > _done) _done = last;
        Pending.RemoveRange(0, count);
    }

    private static void Freeze(FreezeReason why)
    {
        bool changed;
        lock (Gate)
        {
            if (_mode == QueueMode.Live)
            {
                if (why == FreezeReason.Unmatched) _freezeUnmatched++;
                else _freezeUnreadable++;
            }
            changed = Switch(QueueMode.Frozen);
        }
        if (changed) TraceState(QueueMode.Frozen, Token(why));
    }

    private static void ResetHead()
    {
        _headWait = 0;
        _hasLastHead = false;
    }

    private static void OnUnreadable()
    {
        _unreadable++;
        _emptyRun = 0;
        ResetHead();
        _hasLastUnmatched = false;
        Freeze(FreezeReason.Unreadable);
    }

    private static void OnEmpty(int tick, int frame)
    {
        ResetHead();
        _headNum = 0;
        _hasLastUnmatched = false;
        if (++_emptyRun < EmptyTicksToResume) return;

        int n = 0;
        while (n < Pending.Count && tick - Pending[n].Tick >= ArrivalMarginTicks) n++;
        if (n > 0)
        {
            Finish(n);
            if (TimingTrace.Enabled) TimingTrace.Write($"queue empty done={_done} f={frame}");
        }

        bool changed;
        lock (Gate)
        {
            changed = !_reconnectPending && Switch(QueueMode.Live);
        }
        if (changed) TraceState(QueueMode.Live, "empty");
    }

    private static void OnHead(in QueueObservation obs, int tick, int frame)
    {
        _emptyRun = 0;
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
            ResetHead();
            Freeze(FreezeReason.Unmatched);
            return;
        }

        _matched++;
        _hasLastUnmatched = false;
        var head = Pending[idx];
        if (TimingTrace.Enabled && (!_hasLastHead || _lastHead != (obs.Cmd, obs.Down)))
            TimingTrace.Write($"queue head seq={head.Num} cmd={obs.Cmd} down={obs.Down} size={obs.Size} f={frame}");
        _lastHead = (obs.Cmd, obs.Down);
        _hasLastHead = true;
        if (_headNum != head.Num)
        {
            _headNum = head.Num;
            _headStartTick = tick;
        }
        _headWait = _headStartTick - head.Tick;

        Finish(idx);
        if (head.Num > _started) _started = head.Num;

        bool changed;
        lock (Gate)
        {
            if (_reconnectPending)
            {
                if (_awaitSync || head.Num <= _reference) return;
                _reconnectPending = false;
            }
            changed = Switch(QueueMode.Live);
        }
        if (changed) TraceState(QueueMode.Live, "match");
    }

    internal static void ResetForTest()
    {
        lock (Gate)
        {
            _enabled = false;
            _disabled = false;
            _mode = QueueMode.Off;
            _pubStarted = 0;
            _pubDone = 0;
            _nextNum = 0;
            _intake.Clear();
            _swap.Clear();
            _awaitSync = false;
            _reconnectPending = false;
            _reference = 0;
            _clearRequested = false;
            _freezeUnmatched = _freezeUnreadable = _freezeReconnect = _freezeExit = _freezeError = _freezeSocket = 0;
            _tick = 0;
            _frame = 0;
            Pending.Clear();
            _started = 0;
            _done = 0;
            Open.Clear();
            Links.Clear();
            _emptyRun = 0;
            _frames = _liveFrames = _frozenFrames = _matched = _unmatched = _unreadable = _overflow = 0;
            _size = 0;
            _headNum = 0;
            _headStartTick = 0;
            _headWait = 0;
            _hasLastHead = false;
            _hasLastUnmatched = false;
        }
        lock (GroupGate)
        {
            Groups.Clear();
            GroupOrder.Clear();
        }
    }
}
