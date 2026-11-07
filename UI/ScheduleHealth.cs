using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AstralPartyBattleLog.Net;

namespace AstralPartyBattleLog.UI;

internal enum HealthCount
{
    Stray, Consume, WeakSettle, WeakOrphan, ExtraStrike, Outside, CounterHeld,
    StaleSignal, NameBudget, PathBudget, LineBudget,
    CandidateOverwrite, StringTableFull, AnalysisDrop,
    UnityClock, FallbackClock, FallbackMoved, HookChanges,
}

internal enum HookState { None, Installed, Failed, Disabled }

// 게임(관측)마다 스케줄러 건강 요약 1줄을 BepInEx 로그에 남긴다. TraceTiming과 무관하게 돈다.
// 관측 수명과 해석 규칙은 docs/TIMING-REPLAY-HANDOFF.md 5절.
internal static class ScheduleHealth
{
    private const int GateKinds = 4;       // dice, pk, round, turn
    private const int ResolveKinds = 4;    // signal, late, cap, budget
    private const int SignalKinds = 5;     // ScreenSignal 순서
    private const long SpeedSampleUs = 1_000_000;

    private static readonly string[] GateNames = { "dice", "pk", "round", "turn" };
    private static readonly string[] SignalNames = { "dice", "window", "hit", "round", "top" };
    private static readonly int CountKinds = Enum.GetValues(typeof(HealthCount)).Length;

    private static readonly (HealthCount Count, string Name)[] DiagFields =
    {
        (HealthCount.Stray, "stray"), (HealthCount.Consume, "consume"), (HealthCount.WeakSettle, "weak-settle"),
        (HealthCount.WeakOrphan, "weak-orphan"), (HealthCount.ExtraStrike, "extra-strike"),
        (HealthCount.Outside, "outside"), (HealthCount.CounterHeld, "counter-held"),
    };
    private static readonly (HealthCount Count, string Name)[] LossFields =
    {
        (HealthCount.NameBudget, "name"), (HealthCount.PathBudget, "path"), (HealthCount.LineBudget, "line"),
        (HealthCount.CandidateOverwrite, "cand-overwrite"), (HealthCount.StringTableFull, "str-table"),
        (HealthCount.AnalysisDrop, "analysis-drop"), (HealthCount.StaleSignal, "stale-signal"),
    };

    private sealed class Observation
    {
        public int Number;
        public int Gen;
        public bool Partial;
        public readonly long[] Created = new long[GateKinds];
        public readonly long[,] Resolved = new long[GateKinds, ResolveKinds];
        public readonly long[,] Shown = new long[GateKinds, ResolveKinds];
        public readonly long[] Open = new long[GateKinds];
        public readonly long[] Signals = new long[SignalKinds];
        public long[] Counts = Array.Empty<long>();
        public readonly SortedDictionary<int, long> Speeds = new();
        public int MaxQueue;
        public long MaxWaitUs;
        public QueueHealth QueueStart;
        public QueueHealth QueueLast;
        public bool QueueSeen;
        public bool QueueBased;
        public int QueueMaxSize;
        public int QueueMaxWait;
    }

    private static readonly object Sync = new();
    private static Action<string>? _write;
    private static Func<string>? _replayState;
    private static Observation? _active;
    private static int _numbers;
    private static long _trailing;
    private static readonly long[] RunCounts = new long[CountKinds];
    private static HookState _hook;
    private static bool _gating;
    private static long _speedAccUs;
    private static QueueHealth _queueLatest;
    private static bool _queueLatestValid;

    public static void Init(Action<string> write, Func<string>? replayState)
    {
        lock (Sync)
        {
            _write = write;
            _replayState = replayState;
        }
    }

    public static long Trailing
    {
        get { lock (Sync) return _trailing; }
    }

    // 새 판. 앞 관측이 아직 열려 있으면 요약하고 닫는다.
    public static void OnReset(int gen)
    {
        lock (Sync)
        {
            CloseLocked("reset");
            Open(gen, partial: false);
        }
    }

    // 판 시작 신호 없이 전투 진행 줄이 오면(재접속) 부분 관측으로 연다.
    public static void OnBattleInput(int gen)
    {
        lock (Sync)
        {
            if (_active is null) Open(gen, partial: true);
        }
    }

    public static void Close(string why)
    {
        lock (Sync) CloseLocked(why);
    }

    public static void RunEnd(string why)
    {
        lock (Sync)
        {
            CloseLocked(why);
            _write?.Invoke($"[health] run end={why} trailing={_trailing} "
                           + $"unobserved(stale-signal={RunCounts[(int)HealthCount.StaleSignal]}) "
                           + $"replay={_replayState?.Invoke() ?? "off"} "
                           + $"candidates={(ScreenCandidates.Enabled ? ScreenCandidates.State() : "off")}");
        }
    }

    public static void GateCreated(int gen, int gate) => Gen(gen, o =>
    {
        o.Created[gate]++;
        o.Open[gate]++;
    });

    public static void GateResolved(int gen, int gate, int how) => Gen(gen, o =>
    {
        o.Resolved[gate, how]++;
        o.Open[gate]--;
    });

    public static void GateShown(int gen, int gate, int how) => Gen(gen, o => o.Shown[gate, how]++);

    // 해결 전에 버려졌거나 모델 예약으로 넘어간 게이트.
    public static void GateLeft(int gen, int gate, HealthCount? why) => Gen(gen, o =>
    {
        o.Open[gate]--;
        if (why is { } c) o.Counts[(int)c]++;
    });

    public static void Signal(int gen, int kind) => Gen(gen, o => o.Signals[kind]++);

    public static void Count(int gen, HealthCount c) => Gen(gen, o => o.Counts[(int)c]++);

    // 판 세대에 묶이지 않는 계측(프레임 예산, 후보 버퍼). 열린 관측이 없으면 실행 전체 수에만 남는다.
    public static void Count(HealthCount c, long n = 1)
    {
        lock (Sync)
        {
            RunCounts[(int)c] += n;
            if (_active is not null) _active.Counts[(int)c] += n;
        }
    }

    public static void Hook(HookState state)
    {
        lock (Sync)
        {
            if (_hook == state) return;
            _hook = state;
            if (_active is not null) _active.Counts[(int)HealthCount.HookChanges]++;
        }
    }

    public static void Gating(bool gating)
    {
        lock (Sync)
        {
            if (_gating == gating) return;
            _gating = gating;
            if (_active is not null) _active.Counts[(int)HealthCount.HookChanges]++;
        }
    }

    public static void Tick(bool fallbackClock, long dtUs, float speed)
    {
        lock (Sync)
        {
            HealthCount clock = fallbackClock ? HealthCount.FallbackClock : HealthCount.UnityClock;
            RunCounts[(int)clock]++;
            if (_active is null) return;
            _active.Counts[(int)clock]++;
            // 프레임 수가 아니라 내부 시계 1초마다 한 번 재야 프레임률이 분포를 왜곡하지 않는다.
            for (_speedAccUs += dtUs; _speedAccUs >= SpeedSampleUs; _speedAccUs -= SpeedSampleUs)
            {
                int key = (int)Math.Round(speed * 4);
                _active.Speeds[key] = _active.Speeds.GetValueOrDefault(key) + 1;
            }
        }
    }

    public static void QueueDepth(int depth)
    {
        lock (Sync)
        {
            if (_active is not null && depth > _active.MaxQueue) _active.MaxQueue = depth;
        }
    }

    // 추적 값은 메인 스레드에서만 읽을 수 있고 판 관측은 IO 스레드에서도 닫히므로, 펌프가 최신 값을 여기 넘겨 둔다.
    public static void Queue(bool enabled, in QueueHealth health)
    {
        lock (Sync)
        {
            if (!enabled)
            {
                _queueLatestValid = false;
                if (_active is not null) _active.QueueSeen = false;
                return;
            }
            _queueLatest = health;
            _queueLatestValid = true;
            if (_active is null) return;
            Observation o = _active;
            // 추적이 꺼진 채 열린 판은 시작 값이 없으므로, 판 도중 처음 받은 값을 시작점으로 삼는다.
            if (!o.QueueBased)
            {
                o.QueueStart = health;
                o.QueueBased = true;
            }
            o.QueueSeen = true;
            o.QueueLast = health;
            o.QueueMaxSize = Math.Max(o.QueueMaxSize, health.Size);
            o.QueueMaxWait = Math.Max(o.QueueMaxWait, health.HeadWaitTicks);
        }
    }

    public static void Displayed(int gen, long waitUs) => Gen(gen, o =>
    {
        if (waitUs > o.MaxWaitUs) o.MaxWaitUs = waitUs;
    });

    private static void Gen(int gen, Action<Observation> update)
    {
        lock (Sync)
        {
            if (_active is null || _active.Gen != gen)
            {
                _trailing++;
                return;
            }
            update(_active);
        }
    }

    private static void Open(int gen, bool partial)
    {
        _active = new Observation
        {
            Number = ++_numbers, Gen = gen, Partial = partial, Counts = new long[CountKinds],
        };
        _speedAccUs = 0;
        if (_queueLatestValid)
        {
            _active.QueueStart = _queueLatest;
            _active.QueueBased = true;
        }
    }

    private static void CloseLocked(string why)
    {
        if (_active is null) return;
        Observation o = _active;
        _active = null;
        _write?.Invoke(Summarize(o, why));
    }

    private static string Summarize(Observation o, string why)
    {
        var sb = new StringBuilder(640);
        sb.Append("[health] game#").Append(o.Number)
          .Append(" start=").Append(o.Partial ? "partial" : "reset")
          .Append(" end=").Append(why)
          .Append(" gates(made res:s/l/c/b shown:s/l/c/b open)");
        for (int g = 0; g < GateKinds; g++)
        {
            sb.Append(' ').Append(GateNames[g]).Append('=').Append(o.Created[g]).Append(' ');
            Row(sb, o.Resolved, g);
            sb.Append(' ');
            Row(sb, o.Shown, g);
            sb.Append(' ').Append(o.Open[g]);
        }
        sb.Append(" | signals");
        for (int k = 0; k < SignalKinds; k++) sb.Append(' ').Append(SignalNames[k]).Append('=').Append(o.Signals[k]);
        sb.Append(" | diag");
        Fields(sb, o.Counts, DiagFields);
        sb.Append(" | hook=").Append(_hook.ToString().ToLowerInvariant())
          .Append(" mode=").Append(_gating ? "screen" : "model")
          .Append(" changes=").Append(o.Counts[(int)HealthCount.HookChanges])
          .Append(" moved=").Append(o.Counts[(int)HealthCount.FallbackMoved]);
        sb.Append(" | loss");
        Fields(sb, o.Counts, LossFields);
        // 게임을 끌 때 종료 처리가 오지 않아 run end 줄은 실게임에서 거의 찍히지 않는다. 관측 밖 입력은 실행 누계로
        // 여기 함께 남긴다. writer 상태는 잠정이고 최종 상태는 기록 파일의 end에 남는다.
        sb.Append(" run-trailing=").Append(_trailing)
          .Append(" replay=").Append(_replayState?.Invoke() ?? "off").Append("(provisional)");
        sb.Append(" | clock unity=").Append(o.Counts[(int)HealthCount.UnityClock])
          .Append(" fallback=").Append(o.Counts[(int)HealthCount.FallbackClock])
          .Append(" speed");
        if (o.Speeds.Count == 0) sb.Append(" -");
        foreach (KeyValuePair<int, long> s in o.Speeds)
            sb.Append(' ').Append((s.Key / 4f).ToString("0.##", CultureInfo.InvariantCulture)).Append("x=").Append(s.Value);
        sb.Append(" maxQueue=").Append(o.MaxQueue)
          .Append(" maxWait=").Append(o.MaxWaitUs / 1000).Append("ms");
        AppendQueue(sb, o);
        return sb.ToString();
    }

    private static void AppendQueue(StringBuilder sb, Observation o)
    {
        if (!o.QueueSeen)
        {
            sb.Append(" | queue off");
            return;
        }
        QueueHealth a = o.QueueStart, b = o.QueueLast;
        long frames = b.Frames - a.Frames;
        sb.Append(" | queue live=").Append(Percent(b.LiveFrames - a.LiveFrames, frames))
          .Append("% frozen=").Append(Percent(b.FrozenFrames - a.FrozenFrames, frames))
          .Append("% matched=").Append(Grew(a.Matched, b.Matched))
          .Append(" unmatched=").Append(Grew(a.Unmatched, b.Unmatched))
          .Append(" unread=").Append(Grew(a.Unreadable, b.Unreadable))
          .Append(" overflow=").Append(Grew(a.Overflow, b.Overflow))
          .Append(" freeze=").Append(Grew(a.FreezeUnmatched, b.FreezeUnmatched))
          .Append('/').Append(Grew(a.FreezeUnreadable, b.FreezeUnreadable))
          .Append('/').Append(Grew(a.FreezeReconnect, b.FreezeReconnect))
          .Append('/').Append(Grew(a.FreezeExit, b.FreezeExit))
          .Append('/').Append(Grew(a.FreezeError, b.FreezeError))
          .Append('/').Append(Grew(a.FreezeSocket, b.FreezeSocket))
          .Append(" maxsize=").Append(o.QueueMaxSize)
          .Append(" maxwait=").Append(o.QueueMaxWait).Append('t');
    }

    // 판 도중 추적이 다시 시작되어 누계가 줄면 음수 대신 0으로 둔다.
    private static long Grew(long from, long to) => Math.Max(0, to - from);

    private static long Percent(long part, long whole) =>
        whole <= 0 ? 0 : (long)Math.Round(100.0 * Math.Max(0, part) / whole, MidpointRounding.AwayFromZero);

    private static void Row(StringBuilder sb, long[,] table, int gate)
    {
        for (int r = 0; r < ResolveKinds; r++)
        {
            if (r > 0) sb.Append('/');
            sb.Append(table[gate, r]);
        }
    }

    private static void Fields(StringBuilder sb, long[] counts, (HealthCount Count, string Name)[] fields)
    {
        foreach ((HealthCount c, string name) in fields) sb.Append(' ').Append(name).Append('=').Append(counts[(int)c]);
    }
}
