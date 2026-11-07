using System;
using System.Collections.Generic;
using System.Threading;
using AstralPartyBattleLog.Log;
using AstralPartyBattleLog.Net;
using BepInEx.Logging;

namespace AstralPartyBattleLog.UI;

// 재생 어댑터. 기록된 입력과 내부 시각을 실제 스케줄러에 그대로 주입하고 판단은 플러그인 소스가 한다.
// 하네스에만 컴파일한다. Tick을 다시 부르지 않으므로 검증 대상은 "산출된 내부 시계에 대한 결정"이다.
internal static partial class OverlaySchedule
{
    internal readonly record struct ItemIn(
        long Id, string Signal, string Kind, long Group, int Units, int Detail, int Round, long ReceivedUs,
        int SpeedBits, int Gen, long Ref = -1, bool Result = false, bool Opener = false);

    internal readonly record struct SignalIn(
        long Id, long AtUs, int Gen, string Kind, int Pip, bool On, long InstanceId);

    internal static QueueSnapshot QueueIn(string mode, long startedThrough, long doneThrough) =>
        new((QueueMode)Array.IndexOf(QueueModeNames, mode), startedThrough, doneThrough);

    internal static class ReplayPort
    {
        private static readonly Action<string> NoLine = _ => { };
        private static readonly Action<int> NoPage = _ => { };
        private static readonly Action NoClear = () => { };

        // 재생 중에는 추적기를 부르지 않고 그 Pump에 기록된 추적 상태·끝 기준을 쓴다.
        private static QueueSnapshot _queue;
        private static readonly List<(long Opener, long End)> PumpLinks = new();

        public static IReplaySink? Current => _recording ? _sink : null;

        public static void Swap(IReplaySink? sink)
        {
            _sink = sink;
            _recording = sink is not null;
        }

        public static void Attach(IReplaySink sink)
        {
            _sink = sink;
            _recording = true;
            _readQueue = () => _queue;
            _drainLinks = into =>
            {
                into.AddRange(PumpLinks);
                int n = PumpLinks.Count;
                PumpLinks.Clear();
                return n;
            };
        }

        public static void Init(long nowUs, int speedBits, bool enabled, long maxLagUs)
        {
            Clock(nowUs, speedBits);
            OverlaySchedule.Init(new ManualLogSource("replay"), enabled, (int)(maxLagUs / 1000), false);
        }

        public static void Clock(long nowUs, int speedBits)
        {
            _nowUs = nowUs;
            _speed = BitConverter.Int32BitsToSingle(speedBits);
        }

        // 공개 Reset()을 다시 부르지 않는다. 기록된 비우기 항목이 따로 주입되므로 두 번 생기면 안 된다.
        public static void Control(bool reset, int genAfter)
        {
            _generation = genAfter;
            lock (GenOps) GenOps[genAfter] = reset ? Why.Reset : Why.Discard;
        }

        // 이 Pump가 실제로 꺼낸 입력만 넣는다. 미래 Pump 소속 입력을 미리 넣지 않는다.
        public static string? Pump(long pumpId, long nowUs, bool gated, int observedGen,
                                   IEnumerable<ItemIn> items, IEnumerable<SignalIn> signals,
                                   QueueSnapshot queue, IEnumerable<(long Opener, long End)> links)
        {
            if (_gating != gated) return $"pump {pumpId}: mode differs (recorded {(gated ? "gated" : "model")})";
            if (_generation != observedGen) return $"pump {pumpId}: generation {_generation} != recorded {observedGen}";
            _nowUs = nowUs;
            _queue = queue;
            PumpLinks.Clear();
            PumpLinks.AddRange(links);
            foreach (ItemIn i in items)
                Incoming.Enqueue(new Item(
                    (Signal)Array.IndexOf(ItemSignalNames, i.Signal), Enum.Parse<LineKind>(i.Kind, ignoreCase: true),
                    i.Group, i.Units, i.ReceivedUs, BitConverter.Int32BitsToSingle(i.SpeedBits), i.Gen, null, i.Round,
                    i.Detail, i.Id, 0, i.Ref, i.Result, i.Opener));
            foreach (SignalIn s in signals)
                Signals.Add(new PendingSignal(
                    (ScreenSignal)Array.IndexOf(ScreenNames, s.Kind), s.Pip, s.On, (IntPtr)s.InstanceId, s.AtUs, s.Gen,
                    s.Id));
            _pumpIds = pumpId - 1;
            OverlaySchedule.Pump(NoLine, NoPage, NoClear);
            return null;
        }
    }
}
