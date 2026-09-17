using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using AstralPartyBattleLog.Log;
using BepInEx.Logging;

namespace AstralPartyBattleLog.UI;

/// <summary>
/// 해석이 끝난 로그 줄을 게임 연출에 맞춰 오버레이에 늦게 내보낸다.
///
/// <b>늦추는 것은 화면 출력뿐이다.</b> 패킷 해석과 로거 상태는 수신 즉시 끝나고, 파일
/// 로그도 그때 쓰인다. 해석을 늦추면 턴·원인 문맥이 뒤섞이고 골드 짝 맞추기가 깨진다.
///
/// 소켓 스레드는 <see cref="Line"/>/<see cref="Page"/>/<see cref="Reset"/>으로 넣기만 하고,
/// 순서 계산과 출력은 메인 스레드의 <see cref="Pump"/>가 한다. Unity에 의존하지 않는다 —
/// 시각과 배속은 <see cref="Tick"/>으로 받는다.
/// 설계와 측정 절차는 <c>docs/OVERLAY-TIMING.md</c>.
/// </summary>
internal static class OverlaySchedule
{
    private enum Signal { Line, Page, Clear }

    private readonly struct Item
    {
        public readonly Signal Signal;
        public readonly LineKind Kind;
        public readonly long Group;
        public readonly long ReceivedUs;
        public readonly float Speed;
        public readonly int Generation;
        public readonly string? Text;
        public readonly int Round;

        public Item(Signal signal, LineKind kind, long group, long receivedUs, float speed,
                    int generation, string? text, int round)
        {
            Signal = signal; Kind = kind; Group = group; ReceivedUs = receivedUs;
            Speed = speed; Generation = generation; Text = text; Round = round;
        }
    }

    private readonly struct Scheduled
    {
        public readonly Item Item;
        public readonly long DueUs;

        public Scheduled(Item item, long dueUs)
        {
            Item = item;
            DueUs = dueUs;
        }
    }

    private static readonly int KindCount = Enum.GetValues(typeof(LineKind)).Length;

    /// <summary>
    /// 측정 프로필. <b>측정하지 않은 종류는 0</b>이다 — 근거 없는 값을 넣지 않는다.
    /// </summary>
    private static readonly long[] BaseDelayUs = new long[KindCount];

    /// <summary>
    /// 배속을 따르지 않는 연출. 게임은 <c>UniTask.Delay</c>를 배속 적용(DeltaTime)과
    /// 미적용(UnscaledDeltaTime) 둘 다 쓰므로 종류마다 따로 재야 한다.
    /// </summary>
    private static readonly bool[] FixedDelay = new bool[KindCount];

    private static readonly long[] LastReceivedUs = new long[KindCount];

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
    private static long _lastDueUs = long.MinValue;
    private static long _lastGroup = -1;
    private static long _lastTracedGroup = -1;

    private static bool _enabled;
    private static long _offsetUs;
    private static long _maxDelayUs;
    private static ManualLogSource? _log;

    public static bool TraceTiming { get; private set; }

    public static void Init(ManualLogSource log, bool enabled, int offsetMs, int maxDelayMs,
                            bool traceTiming, string profilePath)
    {
        _log = log;
        _enabled = enabled;
        _offsetUs = offsetMs * 1000L;
        _maxDelayUs = Math.Max(0, maxDelayMs) * 1000L;
        TraceTiming = traceTiming;
        LoadProfile(profilePath);
    }

    private static void LoadProfile(string path)
    {
        Array.Clear(BaseDelayUs, 0, BaseDelayUs.Length);
        Array.Clear(FixedDelay, 0, FixedDelay.Length);
        if (!File.Exists(path))
        {
            _log?.LogInfo("No overlay timing profile; overlay lines are only kept in order.");
            return;
        }

        int loaded = 0;
        try
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] parts = line.Split('\t');
                if (parts.Length < 2
                    || !Enum.TryParse(parts[0].Trim(), ignoreCase: true, out LineKind kind)
                    || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms)
                    || ms < 0)
                {
                    _log?.LogWarning($"Ignored overlay timing line: '{raw}'");
                    continue;
                }
                BaseDelayUs[(int)kind] = ms * 1000L;
                FixedDelay[(int)kind] = parts.Length > 2
                    && parts[2].Trim().Equals("fixed", StringComparison.OrdinalIgnoreCase);
                loaded++;
            }
            _log?.LogInfo($"Loaded {loaded} overlay timing entries from {path}");
        }
        catch (Exception e)
        {
            Array.Clear(BaseDelayUs, 0, BaseDelayUs.Length);
            Array.Clear(FixedDelay, 0, FixedDelay.Length);
            _log?.LogWarning($"Could not read overlay timing profile; no delay will be applied: {e.Message}");
        }
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

    public static void Line(string text, LineKind kind, long group) =>
        Push(Signal.Line, kind, group, text, 0);

    public static void Page(int round, long group) =>
        Push(Signal.Page, LineKind.Round, group, null, round);

    public static void Reset()
    {
        Interlocked.Increment(ref _generation);
        // 비우기도 큐를 탄다. 따로 보내면 이미 꺼내 둔 지난 판 줄이 비운 뒤에 붙을 수 있다.
        Push(Signal.Clear, LineKind.Immediate, -1, null, 0);
    }

    /// <summary>
    /// 전투를 벗어났다. 대기 중인 줄만 버리고 화면은 그대로 둔다 — 결과 화면에서도
    /// 이미 보인 로그는 볼 수 있어야 한다.
    /// </summary>
    public static void Discard() => Interlocked.Increment(ref _generation);

    private static void Push(Signal signal, LineKind kind, long group, string? text, int round) =>
        Incoming.Enqueue(new Item(signal, kind, group, Volatile.Read(ref _nowUs),
                                  Volatile.Read(ref _speed), Volatile.Read(ref _generation),
                                  text, round));

    public static void Pump(Action<string> line, Action<int> page, Action clear)
    {
        long now = _nowUs;

        while (Incoming.TryDequeue(out Item item))
        {
            if (IsStale(item)) continue;
            LastReceivedUs[(int)item.Kind] = item.ReceivedUs;
            Waiting.Enqueue(new Scheduled(item, DueOf(item)));
        }

        while (Waiting.Count > 0)
        {
            Scheduled head = Waiting.Peek();
            if (IsStale(head.Item))
            {
                Waiting.Dequeue();
                continue;
            }
            if (head.DueUs > now && Waiting.Count <= MaxWaiting) break;

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
    /// 기한 = <c>수신 시각 + 종류별 지연</c>이되, 세 가지로 보정한다.
    /// <list type="number">
    /// <item><b>같은 그룹은 첫 줄의 기한을 그대로 쓴다.</b> 한 패킷에서 나온 머리줄과 효과
    ///   줄이 흩어지면 안 된다.</item>
    /// <item><b>앞 줄의 기한보다 이르지 않다.</b> 지연이 짧은 종류가 긴 종류를 추월하면
    ///   로그 순서가 뒤집힌다. 받은 순서대로 꺼내므로 결과적으로 앞 줄을 기다린다.</item>
    /// <item><b>수신 뒤 최대 지연을 넘지 않는다.</b> 앞 줄 하나가 길게 밀려도 뒤 줄이
    ///   무한정 끌려가지 않는다. 이때만 2번이 깨질 수 있는데, 꺼내는 순서가 FIFO라
    ///   화면 순서는 그대로다.</item>
    /// </list>
    /// 배속은 넣을 때의 값을 쓴다. 기다리는 도중 배속이 바뀌어도 다시 계산하지 않는다.
    /// </summary>
    private static long DueOf(Item item)
    {
        if (item.Generation != _scheduleGeneration)
        {
            _scheduleGeneration = item.Generation;
            _lastDueUs = long.MinValue;
            _lastGroup = -1;
        }

        long due;
        if (item.Group >= 0 && item.Group == _lastGroup)
        {
            due = _lastDueUs;
        }
        else
        {
            due = Math.Max(item.ReceivedUs + DelayOf(item), _lastDueUs);
            due = Math.Min(due, item.ReceivedUs + _maxDelayUs);
            _lastGroup = item.Group;
        }

        _lastDueUs = Math.Max(_lastDueUs, due);
        return due;
    }

    private static long DelayOf(Item item)
    {
        if (!_enabled || item.Signal == Signal.Clear) return 0;

        int k = (int)item.Kind;
        long delay = BaseDelayUs[k];
        if (delay > 0 && !FixedDelay[k]) delay = (long)(delay / item.Speed);
        if (item.Kind != LineKind.Immediate) delay += _offsetUs;
        return Math.Clamp(delay, 0, _maxDelayUs);
    }

    private static void Release(Scheduled s, long now, Action<string> line, Action<int> page, Action clear)
    {
        Item item = s.Item;
        switch (item.Signal)
        {
            case Signal.Line: line(item.Text!); break;
            case Signal.Page: page(item.Round); break;
            case Signal.Clear: clear(); break;
        }

        if (!TraceTiming || item.Group == _lastTracedGroup) return;
        _lastTracedGroup = item.Group;
        _log?.LogInfo($"[timing] show kind={item.Kind.ToString().ToLowerInvariant()} planned={(s.DueUs - item.ReceivedUs) / 1000}ms "
                      + $"waited={(now - item.ReceivedUs) / 1000}ms speed={item.Speed:0.##}");
    }

    /// <summary>
    /// 측정용 표시. 화면에서 연출이 끝나는 순간 키를 누르면, 종류별로 마지막 줄을 받은 뒤
    /// 얼마나 지났는지 남긴다. 그 값이 그 종류의 지연 후보다.
    /// </summary>
    public static void Mark()
    {
        long now = _nowUs;
        var sb = new StringBuilder();
        sb.Append($"[timing] mark speed={Volatile.Read(ref _speed):0.##}");
        for (int k = 0; k < KindCount; k++)
        {
            long received = LastReceivedUs[k];
            if (received <= 0 || now - received > 20_000_000) continue;
            sb.Append($" {((LineKind)k).ToString().ToLowerInvariant()}={(now - received) / 1000}ms");
        }
        _log?.LogInfo(sb.ToString());
    }
}
