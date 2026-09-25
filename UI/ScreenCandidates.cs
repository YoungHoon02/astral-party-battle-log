using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AstralPartyBattleLog.UI;

internal enum GapKind { Late, Cap, Budget, Weak, Stray, WeakOrphan }

// Diagnostics.ScreenProbe 전용. 화면 신호가 빠졌거나 짝이 없던 구간에 켜지고 꺼진 오브젝트 경로를 모아
// 신호 후보로 보고한다. 보고는 후보 선정 근거일 뿐 의미의 증명이 아니며 자동으로 채택하지 않는다.
// 구간 정의와 한계는 docs/TIMING-REPLAY-HANDOFF.md 6절.
internal static class ScreenCandidates
{
    public const int RingSize = 16384;
    public const int MaxStrings = 4096;
    public const int MaxTableChars = 256 * 1024;
    public const int MaxPathChars = 200;
    public const long StrayWindowUs = 3_000_000;
    // 상한(60초)으로 풀린 구간도 담도록 조금 넉넉하게 둔다. 넘으면 뒤쪽만 보고 잘렸다고 표시한다.
    public const long MaxSpanUs = 90_000_000;
    public const int MaxJobs = 32;
    public const int MaxEventsPerJob = 16;
    public const int ScanPerFrame = 4096;
    public const int ReportsPerFrame = 2;
    public const int TopCandidates = 3;
    public const int MaxMarks = 1024;
    public const byte NoTag = byte.MaxValue;

    private static readonly string[] KnownNames = { "dice", "window", "hit", "round", "top" };

    private struct Slot
    {
        public int Str;
        public long AtUs;
        public bool On;
        public byte Tag;
    }

    private sealed class Tally
    {
        public int On, Off;
        public long FirstUs;
    }

    private sealed class Job
    {
        public long From, To;
        public readonly List<string> Events = new();
        public int ExtraEvents;
        public bool Started, Done;
        public long Pos;
        public int Entries;
        public bool FrontLost, Overrun, Budget, Boundary, Clipped;
        public readonly Dictionary<int, Tally> Counts = new();
        public readonly int[] Known = new int[KnownNames.Length];
    }

    private static readonly Slot[] Ring = new Slot[RingSize];
    private static long _written;

    private static readonly Dictionary<string, int> Ids = new(StringComparer.Ordinal);
    private static readonly List<string?> Strings = new();
    private static readonly List<int> Refs = new();
    private static readonly Stack<int> Free = new();
    private static int _chars;

    private static readonly List<Job> Jobs = new();
    private static readonly Queue<long> BudgetMarks = new();
    private static readonly Queue<long> BoundaryMarks = new();
    private static long _marksLostBeforeUs = long.MinValue;
    private static int _lastGeneration = int.MinValue;
    private static int _events;
    private static Action<string>? _report;

    private static long _overwrites, _reportedOverwrites, _tableFull, _jobDrops;

    public static bool Enabled { get; private set; }

    public static void Init(Action<string> report)
    {
        _report = report;
        Array.Clear(Ring);
        _written = 0;
        Ids.Clear();
        Strings.Clear();
        Refs.Clear();
        Free.Clear();
        _chars = 0;
        Jobs.Clear();
        BudgetMarks.Clear();
        BoundaryMarks.Clear();
        _marksLostBeforeUs = _lastBudgetUs = long.MinValue;
        _lastGeneration = int.MinValue;
        _overwrites = _reportedOverwrites = _tableFull = _jobDrops = 0;
        Enabled = true;
    }

    public static void Disable() => Enabled = false;

    public static string State() =>
        $"ring={Math.Min(_written, RingSize)}/{RingSize} strings={Ids.Count}/{MaxStrings} chars={_chars} "
        + $"jobs={Jobs.Count} overwrites={_overwrites} tableFull={_tableFull} jobDrops={_jobDrops}";

    // 전환 시점에 이미 마스킹한 경로를 받는다. 원시 포인터는 받지 않는다.
    public static void Transition(string path, long atUs, bool on, byte tag)
    {
        if (!Enabled) return;
        if (path.Length > MaxPathChars) path = "~" + path.Substring(path.Length - (MaxPathChars - 1));
        if (!Ids.TryGetValue(path, out int id))
        {
            if (Ids.Count >= MaxStrings || _chars + path.Length > MaxTableChars)
            {
                _tableFull++;
                ScheduleHealth.Count(HealthCount.StringTableFull);
                Budget(atUs);
                return;
            }
            id = Allocate(path);
        }

        long index = _written++;
        ref Slot slot = ref Ring[index % RingSize];
        if (index >= RingSize)
        {
            _overwrites++;
            Release(slot.Str);
        }
        slot = new Slot { Str = id, AtUs = atUs, On = on, Tag = tag };
        Refs[id]++;
    }

    // 프레임 예산 초과로 전환을 놓친 시각. 이 시각을 품은 구간은 불완전으로 보고한다.
    public static void Budget(long atUs)
    {
        if (atUs == _lastBudgetUs) return;
        _lastBudgetUs = atUs;
        Mark(BudgetMarks, atUs);
    }

    public static void Boundary(long atUs) => Mark(BoundaryMarks, atUs);

    private static long _lastBudgetUs = long.MinValue;

    public static void Gap(GapKind kind, string what, long fromUs, long toUs)
    {
        if (!Enabled || toUs < fromUs) return;
        string label = $"{Name(kind)}:{what}#{++_events}";
        bool clipped = toUs - fromUs > MaxSpanUs;
        if (clipped) fromUs = toUs - MaxSpanUs;

        // 아직 훑지 않은 겹치는 구간과는 합쳐 한 번만 훑는다. 원래 사건 번호는 남긴다.
        foreach (Job j in Jobs)
        {
            if (j.Started || fromUs > j.To || toUs < j.From) continue;
            long from = Math.Min(j.From, fromUs), to = Math.Max(j.To, toUs);
            if (to - from > MaxSpanUs) continue;
            j.From = from;
            j.To = to;
            j.Clipped |= clipped;
            AddEvent(j, label);
            return;
        }
        if (Jobs.Count >= MaxJobs)
        {
            _jobDrops++;
            ScheduleHealth.Count(HealthCount.AnalysisDrop);
            return;
        }
        var job = new Job { From = fromUs, To = toUs, Clipped = clipped };
        AddEvent(job, label);
        Jobs.Add(job);
    }

    // 메인 스레드에서 프레임마다. 끝난 구간만 예산 안에서 훑는다.
    public static void Pump(long nowUs, int generation)
    {
        if (!Enabled) return;
        if (generation != _lastGeneration)
        {
            if (_lastGeneration != int.MinValue) Boundary(nowUs);
            _lastGeneration = generation;
        }

        if (_overwrites != _reportedOverwrites)
        {
            ScheduleHealth.Count(HealthCount.CandidateOverwrite, _overwrites - _reportedOverwrites);
            _reportedOverwrites = _overwrites;
        }

        int budget = ScanPerFrame;
        int reports = 0;
        for (int i = 0; i < Jobs.Count && budget > 0 && reports < ReportsPerFrame;)
        {
            Job j = Jobs[i];
            if (j.To >= nowUs)
            {
                i++;
                continue;
            }
            if (!j.Started) Start(j);
            budget -= Scan(j, budget);
            if (!j.Done)
            {
                i++;
                continue;
            }
            _report?.Invoke(Report(j));
            Finish(j);
            Jobs.RemoveAt(i);
            reports++;
        }
        Trim(BudgetMarks, nowUs);
        Trim(BoundaryMarks, nowUs);
    }

    private static int Allocate(string path)
    {
        int id;
        if (Free.Count > 0)
        {
            id = Free.Pop();
            Strings[id] = path;
            Refs[id] = 0;
        }
        else
        {
            id = Strings.Count;
            Strings.Add(path);
            Refs.Add(0);
        }
        Ids[path] = id;
        _chars += path.Length;
        return id;
    }

    // 링과 분석 작업이 모두 놓은 문자열은 번호째 회수한다.
    private static void Release(int id)
    {
        if (--Refs[id] > 0) return;
        string path = Strings[id]!;
        Ids.Remove(path);
        _chars -= path.Length;
        Strings[id] = null;
        Free.Push(id);
    }

    private static void AddEvent(Job j, string label)
    {
        if (j.Events.Count < MaxEventsPerJob) j.Events.Add(label);
        else j.ExtraEvents++;
    }

    private static void Start(Job j)
    {
        j.Started = true;
        long oldest = Math.Max(0, _written - RingSize);
        long lo = oldest, hi = _written;
        while (lo < hi)
        {
            long mid = lo + (hi - lo) / 2;
            if (Ring[mid % RingSize].AtUs < j.From) lo = mid + 1;
            else hi = mid;
        }
        j.Pos = lo;
        // 링이 한 바퀴 돈 뒤 가장 오래된 항목이 구간 시작보다 늦으면 그 앞부분은 덮어써졌다.
        if (oldest > 0 && lo == oldest && Ring[oldest % RingSize].AtUs > j.From) j.FrontLost = true;
        j.Budget = Contains(BudgetMarks, j.From, j.To) || _marksLostBeforeUs >= j.From;
        j.Boundary = Contains(BoundaryMarks, j.From, j.To);
    }

    private static int Scan(Job j, int budget)
    {
        long oldest = Math.Max(0, _written - RingSize);
        if (j.Pos < oldest)
        {
            j.Overrun = true;
            j.Pos = oldest;
        }
        int used = 0;
        while (used < budget)
        {
            // 구간이 이미 지났으므로 앞으로 들어올 전환은 모두 구간 뒤다.
            if (j.Pos >= _written)
            {
                j.Done = true;
                break;
            }
            Slot s = Ring[j.Pos % RingSize];
            if (s.AtUs > j.To)
            {
                j.Done = true;
                break;
            }
            used++;
            j.Pos++;
            j.Entries++;
            if (s.Tag != NoTag)
            {
                if (s.Tag < j.Known.Length) j.Known[s.Tag]++;
                continue;
            }
            if (!j.Counts.TryGetValue(s.Str, out Tally? t))
            {
                t = new Tally { FirstUs = s.AtUs };
                j.Counts[s.Str] = t;
                Refs[s.Str]++;
            }
            if (s.On) t.On++;
            else t.Off++;
        }
        return used;
    }

    private static void Finish(Job j)
    {
        foreach (int id in j.Counts.Keys) Release(id);
        j.Counts.Clear();
    }

    // 구간에서 1~2회만 바뀐 경로를 먼저 올린다. 켜짐과 꺼짐 횟수를 따로 적는다.
    private static string Report(Job j)
    {
        var sb = new StringBuilder(256);
        sb.Append("[candidates] events=").Append(string.Join(',', j.Events));
        if (j.ExtraEvents > 0) sb.Append("+").Append(j.ExtraEvents);
        sb.Append(" span=").Append(((j.To - j.From) / 1000).ToString(CultureInfo.InvariantCulture)).Append("ms")
          .Append(" entries=").Append(j.Entries).Append(" known");
        for (int k = 0; k < KnownNames.Length; k++) sb.Append(' ').Append(KnownNames[k]).Append('=').Append(j.Known[k]);

        var flags = new List<string>();
        if (j.FrontLost) flags.Add("front-overwritten");
        if (j.Overrun) flags.Add("overrun");
        if (j.Budget) flags.Add("budget");
        if (j.Clipped) flags.Add("clipped");
        bool incomplete = flags.Count > 0;
        if (j.Boundary) flags.Add("boundary");
        sb.Append(incomplete ? " incomplete" : " complete");
        if (flags.Count > 0) sb.Append(" flags=").Append(string.Join(',', flags));

        var picks = new List<KeyValuePair<int, Tally>>();
        foreach (KeyValuePair<int, Tally> c in j.Counts)
            if (c.Value.On + c.Value.Off <= 2) picks.Add(c);
        picks.Sort((a, b) =>
        {
            int n = (a.Value.On + a.Value.Off).CompareTo(b.Value.On + b.Value.Off);
            return n != 0 ? n : a.Value.FirstUs.CompareTo(b.Value.FirstUs);
        });

        if (picks.Count == 0)
        {
            string why = j.Entries == 0 ? "no transitions recorded in the span"
                : j.Counts.Count == 0 ? "only known signal objects changed"
                : "every path changed 3+ times";
            sb.Append(" top=none (").Append(why).Append(incomplete ? "; span incomplete, absence not proven)" : ")");
            return sb.ToString();
        }
        sb.Append(" top=");
        for (int i = 0; i < picks.Count && i < TopCandidates; i++)
        {
            Tally t = picks[i].Value;
            if (i > 0) sb.Append(" | ");
            sb.Append(Strings[picks[i].Key]).Append(" on").Append(t.On).Append("/off").Append(t.Off)
              .Append(" +").Append(((t.FirstUs - j.From) / 1000).ToString(CultureInfo.InvariantCulture)).Append("ms");
        }
        if (picks.Count > TopCandidates) sb.Append(" (+").Append(picks.Count - TopCandidates).Append(')');
        return sb.ToString();
    }

    private static void Mark(Queue<long> marks, long atUs)
    {
        if (!Enabled) return;
        if (marks.Count >= MaxMarks) _marksLostBeforeUs = Math.Max(_marksLostBeforeUs, marks.Dequeue());
        marks.Enqueue(atUs);
    }

    private static bool Contains(Queue<long> marks, long from, long to)
    {
        foreach (long t in marks)
            if (t >= from && t <= to) return true;
        return false;
    }

    // 대기 중인 구간이 볼 수 있는 가장 이른 시각보다 오래된 표식은 버린다.
    private static void Trim(Queue<long> marks, long nowUs)
    {
        long keep = nowUs - MaxSpanUs - StrayWindowUs;
        foreach (Job j in Jobs) keep = Math.Min(keep, j.From);
        while (marks.Count > 0 && marks.Peek() < keep) marks.Dequeue();
    }

    private static string Name(GapKind kind) => kind switch
    {
        GapKind.Late => "late",
        GapKind.Cap => "cap",
        GapKind.Budget => "budget",
        GapKind.Weak => "weak",
        GapKind.Stray => "stray",
        _ => "weak-orphan",
    };
}
