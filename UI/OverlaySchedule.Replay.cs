using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using AstralPartyBattleLog.Log;

namespace AstralPartyBattleLog.UI;

// Diagnostics.Replay: 스케줄러 API 경계의 입력과 결정을 JSONL로 남긴다. 표시 정책에는 관여하지 않는다.
// 스키마·완전성 규칙은 docs/TIMING-REPLAY-HANDOFF.md 4절, 재생기는 tools/harness/Replay.cs.
internal static partial class OverlaySchedule
{
    // 결정 로직(게이트·모델·장부·장벽·상한)을 바꾸면 올린다. 재생기는 모르는 버전을 거부한다.
    public const int SchedulerVersion = 2;
    public const int ReplayFormat = 1;
    private const long Null = ReplayRecord.Null;
    private const int MaxInstances = 65536;

    private enum Rec : byte
    {
        Header, Init, Tick, Item, Control, Signal, Env, PumpBegin, Take, SignalTake, Decision, Diagnostic, PumpEnd, Mark,
    }

    private enum Act : byte { Schedule, Transfer, Resolve, Release, Drop }

    private enum Why : byte
    {
        Model, Sync, OwnSignal, LaterSignal, WindowClose, Cap, Budget, GroupFollow, CounterSync, Fallback,
        Stale, Reset, Discard,
    }

    private enum Env : byte { HookSuccess, HookFail, HookDisabled, Fallback, ForgetBanner }

    private enum Diag : byte
    {
        Stray, Consume, WeakSettle, WeakOrphan, ExtraStrike, Outside, CounterHeld, BannerLearned, StaleSignal,
    }

    private static readonly string[] RecNames =
    {
        "header", "init", "tick", "item", "control", "signal", "env", "pump-begin", "take", "signal-take",
        "decision", "diagnostic", "pump-end", "mark",
    };
    private static readonly string[] ItemSignalNames = { "line", "page", "clear", "sync", "advance" };
    private static readonly string[] ScreenNames = { "dice-face", "window", "hit", "round-tip", "top-tip" };
    private static readonly string[] ActNames = { "schedule", "transfer", "resolve", "release", "drop" };
    private static readonly string[] WhyNames =
    {
        "model", "sync", "own-signal", "later-signal", "window-close", "cap", "budget", "group-follow",
        "counter-sync", "fallback", "stale", "reset", "discard",
    };
    private static readonly string[] EnvNames = { "hook-success", "hookfail", "hook-disabled", "fallback", "forget-banner" };
    private static readonly string[] DiagNames =
    {
        "stray", "consume", "weak-settle", "weak-orphan", "extra-strike", "outside", "counter-held",
        "banner-learned", "stale-signal",
    };
    private static readonly string[] OutputNames = { "none", "line", "page", "clear" };

    private static IReplaySink? _sink;
    private static bool _recording;
    // 스케줄러 상태를 한 번이라도 바꿨으면 기록 시작이 clean이 아니다.
    private static bool _touched;

    // 종료 때 진행 중인 계측 호출이 끝나기를 기다리려고 센다. 한 호출 안에서는 스레드별 깊이로 중첩을 푼다.
    private static int _recActive;
    private static int _recClosing;
    [ThreadStatic] private static int _recDepth;
    [ThreadStatic] private static bool _recLive;

    private static long _itemIds, _signalIds, _controlIds, _pumpIds;
    private static long _emitted, _instanceIds;
    private static int _shutdown;
    private static long _pumpId = Null;
    private static long _cause = Null;

    // 세대 변경(Reset/Discard)과 처리 구간(Pump/FallBackToModel)의 겹침. 한 번 겹치면 파일 끝까지 남는다.
    private static int _changing;
    private static long _changes;
    private static int _overlap;
    private static long _sectionChanges;
    private static bool _sectionOverlap;

    private static readonly Dictionary<IntPtr, long> Instances = new();
    private static readonly Dictionary<int, Why> GenOps = new();

    public static long NowUs => Volatile.Read(ref _nowUs);

    public static int Generation => Volatile.Read(ref _generation);

    public static bool Recording => _recording;

    // 플러그인 로드 직후, 스케줄러를 부르기 전에 한 번 부른다. 실행 중 켜기는 지원하지 않는다.
    public static void StartReplay(IReplaySink sink)
    {
        _sink = sink;
        _recording = true;
        Emit(new ReplayRecord
        {
            Type = (byte)Rec.Header, A = ReplayFormat, B = SchedulerVersion, C = _touched ? 1 : 0,
        });
    }

    // Pump 구간 경계를 알려 줘야 임시 end가 구간 가운데에 붙지 않는다.
    public static ReplayWriter NewReplayWriter(Stream output, Action<string> warn) =>
        new(output, FormatRecord, warn, sectionOpen: (byte)Rec.PumpBegin, sectionClose: (byte)Rec.PumpEnd,
            liveOverlap: () => Volatile.Read(ref _overlap) != 0);

    public static string ReplayState() => _sink is ReplayWriter w
        ? $"dropped={w.Dropped} io={(w.IoFailed ? "failed" : "ok")} stopped={(w.Stopped ? 1 : 0)} "
          + $"overlap={Volatile.Read(ref _overlap)} maxQueue={w.MaxQueueDepth} bytes={w.BytesWritten}"
        : _recording ? "memory" : "off";

    // 언로드는 기존처럼 대기 줄을 버린다. 프로세스 종료에서는 세대를 바꾸지 않는다 — 메인 스레드의 Pump와
    // 겹쳐 정상 종료 기록이 모두 경합으로 거부되기 때문이다.
    public static void Shutdown(bool unload)
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;
        ScheduleHealth.Close(unload ? "unload" : "exit");
        if (unload) Discard();
        if (_recording)
        {
            Interlocked.Exchange(ref _recClosing, 1);
            bool quiet = SpinWait.SpinUntil(() => Volatile.Read(ref _recActive) == 0, 1000);
            if (_sink is ReplayWriter w
                && !w.Close(quiet ? (unload ? "unload" : "normal") : "incomplete", Volatile.Read(ref _overlap) != 0, 2000))
                TimingTrace.Warn("[replay] could not write the final end in time; the file keeps its last checkpoint");
        }
        ScheduleHealth.RunEnd(unload ? "unload" : "exit");
    }

    public static void NoteHook(HookState state)
    {
        ScheduleHealth.Hook(state);
        if (!_recording) return;
        Env op = state switch
        {
            HookState.Installed => Env.HookSuccess,
            HookState.Failed => Env.HookFail,
            _ => Env.HookDisabled,
        };
        EmitEnv(op);
    }

    private static bool RecEnter()
    {
        if (_recDepth++ > 0) return _recLive;
        Interlocked.Increment(ref _recActive);
        _recLive = Volatile.Read(ref _recClosing) == 0;
        if (!_recLive) Interlocked.Decrement(ref _recActive);
        return _recLive;
    }

    private static void RecExit()
    {
        if (--_recDepth > 0) return;
        if (_recLive) Interlocked.Decrement(ref _recActive);
        _recLive = false;
    }

    private static void Emit(ReplayRecord r)
    {
        Interlocked.Increment(ref _emitted);
        if (_recDepth > 0)
        {
            if (_recLive) _sink!.Add(r);
            return;
        }
        if (RecEnter()) _sink!.Add(r);
        RecExit();
    }

    // 변경 쪽은 진행 중 표시를 먼저 올리고 횟수를 센다. 처리 쪽은 횟수를 먼저 읽고 진행 중 표시를 읽는다.
    // 그래야 처리 시작 직후에 시작한 변경을 시작 시점 검사에서 놓쳐도 종료 시점 횟수 비교에서 잡힌다.
    // 변경끼리 겹쳐도(소켓 스레드 Reset과 메인 스레드 Discard) 기록 순서와 세대 순서가 어긋날 수 있어 경합으로 둔다.
    private static long BeginChange()
    {
        if (Interlocked.Increment(ref _changing) > 1) Interlocked.Exchange(ref _overlap, 1);
        Interlocked.Increment(ref _changes);
        return Interlocked.Increment(ref _controlIds);
    }

    private static void EndChange() => Interlocked.Decrement(ref _changing);

    private static void BeginProcessing()
    {
        _sectionChanges = Interlocked.Read(ref _changes);
        _sectionOverlap = Interlocked.CompareExchange(ref _changing, 0, 0) != 0;
    }

    private static bool EndProcessing()
    {
        if (Interlocked.Read(ref _changes) != _sectionChanges || Interlocked.CompareExchange(ref _changing, 0, 0) != 0)
            _sectionOverlap = true;
        if (_sectionOverlap) Interlocked.Exchange(ref _overlap, 1);
        return _sectionOverlap;
    }

    private static void BeginPump(long now)
    {
        RecEnter();
        BeginProcessing();
        _pumpId = ++_pumpIds;
        _cause = Null;
        Emit(new ReplayRecord
        {
            Type = (byte)Rec.PumpBegin, A = _pumpId, B = now, C = _gating ? 0 : 1, D = Volatile.Read(ref _generation),
        });
    }

    private static void EndPump()
    {
        bool overlap = EndProcessing();
        Emit(new ReplayRecord
        {
            Type = (byte)Rec.PumpEnd, A = _pumpId, B = Volatile.Read(ref _generation), C = overlap ? 1 : 0,
        });
        _pumpId = Null;
        RecExit();
    }

    private static void Control(long controlId, Why op, int genAfter)
    {
        lock (GenOps) GenOps[genAfter] = op;
        Emit(new ReplayRecord
        {
            Type = (byte)Rec.Control, A = controlId, B = op == Why.Reset ? 0 : 1, C = genAfter - 1, D = genAfter,
            E = Volatile.Read(ref _nowUs),
        });
    }

    private static void RecordItem(in Item item, long parent) => Emit(new ReplayRecord
    {
        Type = (byte)Rec.Item, A = item.Id, B = (long)item.Signal, C = (long)item.Kind, D = item.Group, E = item.Units,
        F = item.Detail, G = item.Round, H = item.ReceivedUs, I = Bits(item.Speed), J = item.Generation, K = parent,
    });

    // 포인터는 기록용 번호로 바꾼다. 같은 포인터는 끝까지 같은 번호다(재사용을 새 객체로 판정하지 않는다).
    // 0은 배너 미학습 판정(IntPtr.Zero 비교)과 뜻이 겹쳐 그대로 0으로 둔다.
    private static void RecordSignal(in PendingSignal s)
    {
        if (!Instances.TryGetValue(s.Instance, out long id))
        {
            if (Instances.Count >= MaxInstances)
            {
                (_sink as ReplayWriter)?.Abort("instance table full");
                return;
            }
            Instances[s.Instance] = id = s.Instance == IntPtr.Zero ? 0 : ++_instanceIds;
        }
        Emit(new ReplayRecord
        {
            Type = (byte)Rec.Signal, A = s.Id, B = s.AtUs, C = s.Generation, D = (long)s.Kind, E = s.Pip,
            F = s.On ? 1 : 0, G = id,
        });
    }

    private static void EmitEnv(Env op) => Emit(new ReplayRecord
    {
        Type = (byte)Rec.Env, A = (long)op, B = Volatile.Read(ref _nowUs),
    });

    private static void Take(in Item item)
    {
        if (_recording) Emit(new ReplayRecord { Type = (byte)Rec.Take, A = _pumpId, B = item.Id });
    }

    private static void TakeSignal(in PendingSignal s)
    {
        if (_recording) Emit(new ReplayRecord { Type = (byte)Rec.SignalTake, A = _pumpId, B = s.Id });
    }

    private static void Decide(Act act, in Item item, Why why, long cause = Null, long leader = Null,
                               long due = Null, long floor = Null, long resolved = Null, long released = Null)
    {
        if (!_recording) return;
        long output = act != Act.Release ? 0 : item.Signal switch
        {
            Signal.Line => 1,
            Signal.Page => 2,
            Signal.Clear => 3,
            _ => 0,
        };
        Emit(new ReplayRecord
        {
            Type = (byte)Rec.Decision, A = _pumpId, B = item.Id, C = cause, D = leader, E = (long)act, F = (long)why,
            G = due, H = floor, I = resolved, J = released, K = output,
        });
    }

    // 폐기 사유는 그 줄의 세대를 끝낸 변경이다. 기록에 없으면(중간 시작) stale로 둔다.
    private static void Drop(in Item item)
    {
        if (!_recording) return;
        Why why;
        lock (GenOps) why = GenOps.TryGetValue(item.Generation + 1, out Why op) ? op : Why.Stale;
        Decide(Act.Drop, item, why);
    }

    private static void DropEntry(Entry e)
    {
        Drop(e.Item);
        if (e.Gate != Gate.None && e.Res == Resolution.None) ScheduleHealth.GateLeft(e.Item.Generation, GateIndex(e.Gate), null);
    }

    private static void Note(Diag kind, long itemRef = Null)
    {
        HealthCount? count = kind switch
        {
            Diag.Stray => HealthCount.Stray,
            Diag.Consume => HealthCount.Consume,
            Diag.WeakSettle => HealthCount.WeakSettle,
            Diag.WeakOrphan => HealthCount.WeakOrphan,
            Diag.ExtraStrike => HealthCount.ExtraStrike,
            Diag.Outside => HealthCount.Outside,
            Diag.CounterHeld => HealthCount.CounterHeld,
            _ => null,
        };
        if (count is { } c) ScheduleHealth.Count(_gateGeneration, c);
        if (_recording)
            Emit(new ReplayRecord { Type = (byte)Rec.Diagnostic, A = _pumpId, B = (long)kind, C = itemRef, D = _cause });
    }

    private static int GateIndex(Gate g) => (int)g - 1;

    private static int HealthResolution(Why why) => why switch
    {
        Why.LaterSignal => 1,
        Why.Cap => 2,
        Why.Budget => 3,
        _ => 0,
    };

    private static long Bits(float f) => BitConverter.SingleToInt32Bits(f);

    internal static void FormatRecord(StringBuilder sb, ReplayRecord r)
    {
        var rec = (Rec)r.Type;
        sb.Append("\"type\":\"").Append(RecNames[r.Type]).Append('"');
        switch (rec)
        {
            case Rec.Header:
                Num(sb, "format", r.A);
                Num(sb, "schedulerVersion", r.B);
                Str(sb, "start", r.C == 0 ? "clean" : "mid");
                break;
            case Rec.Init:
                Num(sb, "nowUs", r.A);
                Num(sb, "speedBits", r.B);
                Bool(sb, "enabled", r.C);
                Num(sb, "maxLagUs", r.D);
                Bool(sb, "gating", r.E);
                Num(sb, "gen", r.F);
                break;
            case Rec.Tick:
                Num(sb, "nowUs", r.A);
                Num(sb, "speedBits", r.B);
                Str(sb, "clock", r.C == 0 ? "unity" : "fallback");
                break;
            case Rec.Item:
                Num(sb, "itemId", r.A);
                Str(sb, "signal", ItemSignalNames[r.B]);
                Str(sb, "kind", ((LineKind)r.C).ToString().ToLowerInvariant());
                Num(sb, "group", r.D);
                Num(sb, "units", r.E);
                Num(sb, "detail", r.F);
                Num(sb, "round", r.G);
                Num(sb, "receivedUs", r.H);
                Num(sb, "speedBits", r.I);
                Num(sb, "gen", r.J);
                Num(sb, "parentControlId", r.K);
                break;
            case Rec.Control:
                Num(sb, "controlId", r.A);
                Str(sb, "op", r.B == 0 ? "reset" : "discard");
                Num(sb, "genBefore", r.C);
                Num(sb, "genAfter", r.D);
                Num(sb, "nowUs", r.E);
                break;
            case Rec.Signal:
                Num(sb, "signalId", r.A);
                Num(sb, "atUs", r.B);
                Num(sb, "gen", r.C);
                Str(sb, "kind", ScreenNames[r.D]);
                Num(sb, "pip", r.E);
                Bool(sb, "on", r.F);
                Num(sb, "instanceId", r.G);
                break;
            case Rec.Env:
                Str(sb, "op", EnvNames[r.A]);
                Num(sb, "nowUs", r.B);
                break;
            case Rec.PumpBegin:
                Num(sb, "pumpId", r.A);
                Num(sb, "nowUs", r.B);
                Str(sb, "mode", r.C == 0 ? "gated" : "model");
                Num(sb, "observedGen", r.D);
                break;
            case Rec.Take:
                Num(sb, "pumpId", r.A);
                Num(sb, "itemRef", r.B);
                break;
            case Rec.SignalTake:
                Num(sb, "pumpId", r.A);
                Num(sb, "signalRef", r.B);
                break;
            case Rec.Decision:
                Num(sb, "pumpId", r.A);
                Num(sb, "itemRef", r.B);
                Num(sb, "causeRef", r.C);
                Num(sb, "leaderItemRef", r.D);
                Str(sb, "action", ActNames[r.E]);
                Str(sb, "reason", WhyNames[r.F]);
                Num(sb, "dueUs", r.G);
                Num(sb, "floorUs", r.H);
                Num(sb, "resolvedUs", r.I);
                Num(sb, "releasedUs", r.J);
                Str(sb, "output", OutputNames[r.K]);
                break;
            case Rec.Diagnostic:
                Num(sb, "pumpId", r.A);
                Str(sb, "kind", DiagNames[r.B]);
                Num(sb, "itemRef", r.C);
                Num(sb, "signalRef", r.D);
                break;
            case Rec.PumpEnd:
                Num(sb, "pumpId", r.A);
                Num(sb, "observedGen", r.B);
                Bool(sb, "overlap", r.C);
                break;
            case Rec.Mark:
                Num(sb, "nowUs", r.A);
                break;
        }
        sb.Append('}');
    }

    private static void Num(StringBuilder sb, string name, long value)
    {
        sb.Append(",\"").Append(name).Append("\":");
        if (value == Null) sb.Append("null");
        else sb.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void Str(StringBuilder sb, string name, string value) =>
        sb.Append(",\"").Append(name).Append("\":\"").Append(value).Append('"');

    private static void Bool(StringBuilder sb, string name, long value) =>
        sb.Append(",\"").Append(name).Append("\":").Append(value != 0 ? "true" : "false");
}
