using System;
using System.Collections.Generic;
using System.IO;
using AstralPartyBattleLog.Log;
using AstralPartyBattleLog.Net;
using AstralPartyBattleLog.UI;
using BepInEx.Logging;

// 순차 재생 큐 검증. 시계는 Tick으로 직접 돌린다 (1틱 = 인자로 준 초).
static class Sched
{
    static readonly List<string> Out = new();
    static int _fail;

    static void Pump() => OverlaySchedule.Pump(
        s => Out.Add(s), r => Out.Add($"<page {r}>"), () => Out.Add("<clear>"));

    static void Advance(double seconds, float speed = 1f)
    {
        OverlaySchedule.Tick((float)seconds, 0f, speed);
        Pump();
    }

    static void Expect(string label, params string[] want)
    {
        bool ok = Out.Count == want.Length;
        for (int i = 0; ok && i < want.Length; i++) ok = Out[i] == want[i];
        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {label}: [{string.Join(", ", Out)}]");
        if (!ok) { _fail++; Console.WriteLine($"       기대: [{string.Join(", ", want)}]"); }
        Out.Clear();
    }

    static void Sig(ScreenSignal kind, bool on, int pip = 0, int instance = 0) =>
        OverlaySchedule.Screen(kind, pip, on, (IntPtr)instance);

    static void Setup(ManualLogSource log, bool enabled = true, int maxLagMs = 60000, bool signals = false)
    {
        OverlaySchedule.Init(log, enabled, maxLagMs, false, signals);
        OverlaySchedule.Discard();
        Advance(0.001);
        Out.Clear();
    }

    public static int Run()
    {
        var log = new ManualLogSource("sched");
        Console.WriteLine("=== 순차 재생 큐 ===");

        // 연출 길이(초): 카드 2.87, 제출 2.23, PK 9.45, 스킬 1.92, 주사위 2.70,
        //                판 시작 18.15, 한 칸 0.30
        // 공개 지연(초): PK 밀린 덩어리 1.94 / 쉬는 화면 4.35, 주사위 쉬는 화면 1.60

        Setup(log);
        OverlaySchedule.Line("atk", LineKind.Attack, 1, 0);
        Advance(4.3);
        Expect("P1 쉬는 화면의 PK는 타격 시점까지 기다린다 (t=4.3)");
        Advance(0.1);
        Expect("P1 t=4.4", "atk");
        OverlaySchedule.Line("skill", LineKind.Skill, 2, 0);
        Advance(5.0);
        Expect("P1 다음 사건은 앞 연출이 끝나야 한다 (t=9.4)");
        Advance(0.1);
        Expect("P1 t=9.5", "skill");

        Setup(log);
        OverlaySchedule.Line("atk", LineKind.Attack, 10, 0);
        OverlaySchedule.Line("dmg", LineKind.Hit, 10, 0);
        Advance(4.3);
        Expect("P2 PK 결과와 피해는 같은 그룹 (t=4.3)");
        Advance(0.1);
        Expect("P2 t=4.4 함께", "atk", "dmg");

        Setup(log, maxLagMs: 1000);
        OverlaySchedule.Line("a1", LineKind.Card, 1, 0);
        Advance(0.1);
        Expect("P3 카드는 바로 뜨고 커서가 2.87초 간다", "a1");
        OverlaySchedule.Line("a2", LineKind.Skill, 2, 0);
        Advance(0.95);
        Expect("P3 최대 지연이 덩어리 시작을 1.1로 자른다 (t=1.05)");
        Advance(0.1);
        Expect("P3 t=1.15", "a2");

        Setup(log);
        OverlaySchedule.Line("c0", LineKind.Card, 1, 0);
        OverlaySchedule.Line("a1", LineKind.Skill, 4, 0);
        Advance(0.2);
        Expect("P4 카드", "c0");
        // 결정 창 열림(TimeWastingS2C, 본문 14바이트). 재생 커서를 맞추는 신호다.
        OverlaySchedule.Sync(Op.TimeWasting, 14);
        OverlaySchedule.Line("c", LineKind.Card, 2, 0);
        Advance(0.001);
        Expect("P4 동기화는 밀린 줄을 내보내고 커서를 당긴다", "a1", "c");
        OverlaySchedule.Line("nxt", LineKind.Effect, 3, 0);
        Advance(2.8);
        Expect("P4 동기화 뒤에도 재생은 이어진다 (t=3.0)");
        Advance(0.1);
        Expect("P4 t=3.1", "nxt");

        Setup(log);
        OverlaySchedule.Advance(LineKind.Submit, 1, 0);
        OverlaySchedule.Line("dmg", LineKind.Hit, 2, 0);
        Advance(0.1);
        Expect("P5 숨긴 카드는 출력 없이 커서만 옮긴다");
        Advance(2.0);
        Expect("P5 제출 연출 2.23초 대기 (t=2.1)");
        Advance(0.2);
        Expect("P5 t=2.3", "dmg");

        Setup(log);
        Advance(0.001, 2f);
        OverlaySchedule.Line("a", LineKind.Attack, 1, 0);
        Advance(2.1, 2f);
        Expect("P6 2배속은 절반 (t=2.1)");
        Advance(0.1, 2f);
        Expect("P6 t=2.2", "a");

        Setup(log);
        Advance(0.001, 0f);
        OverlaySchedule.Line("z", LineKind.Attack, 1, 0);
        Advance(4.3, 0f);
        Expect("P7 비정상 배속(0)은 1.0 (t=4.3)");
        Advance(0.1, 0f);
        Expect("P7 t=4.4", "z");

        Setup(log);
        OverlaySchedule.Page(1, 1);
        OverlaySchedule.Line("turn", LineKind.Turn, 2, 0);
        Advance(0.001);
        Expect("P8 판 시작 페이지는 바로 뜬다", "<page 1>");
        Advance(18.0);
        Expect("P8 첫 사건은 판 시작 연출 18.15초를 기다린다 (t=18.0)");
        Advance(0.2);
        Expect("P8 t=18.2", "turn");

        Setup(log);
        OverlaySchedule.Page(2, 1);
        OverlaySchedule.Line("turn", LineKind.Turn, 2, 0);
        Advance(0.001);
        Expect("P9 라운드 전환 페이지는 기다리지 않는다", "<page 2>", "turn");

        Setup(log);
        // 주사위 연출은 칸 수에 비례하지 않는다(실측 R²=0.19). 칸 수가 달라도 같은 길이다.
        OverlaySchedule.Line("dice", LineKind.Dice, 1, 5);
        OverlaySchedule.Line("after", LineKind.Effect, 2, 0);
        Advance(1.55);
        Expect("P10 쉬는 화면의 주사위 줄은 굴림 1.60초 뒤 (t=1.55)");
        Advance(0.1);
        Expect("P10 t=1.65", "dice");
        Advance(0.95);
        Expect("P10 커서는 공개와 별개로 2.70초 간다 (t=2.6)");
        Advance(0.15);
        Expect("P10 t=2.75", "after");

        Setup(log);
        OverlaySchedule.Line("d1", LineKind.Dice, 1, 1);
        OverlaySchedule.Line("after", LineKind.Effect, 2, 0);
        Advance(2.6);
        Expect("P10b 한 칸도 같은 길이 (t=2.6)", "d1");
        Advance(0.15);
        Expect("P10b t=2.75", "after");

        Setup(log);
        OverlaySchedule.Line("m", LineKind.Move, 1, 5);
        OverlaySchedule.Line("after", LineKind.Effect, 2, 0);
        Advance(1.4);
        Expect("P10c 추가 이동은 여전히 칸당 0.30초 (t=1.4)", "m");
        Advance(0.15);
        Expect("P10c t=1.55", "after");

        Setup(log);
        OverlaySchedule.Line("pk", LineKind.Attack, 1, 0);
        OverlaySchedule.Line("other", LineKind.Effect, 2, 0);
        OverlaySchedule.Line("dmg", LineKind.Hit, 1, 0);
        Advance(4.4);
        Expect("P11 한계: PK 결과와 피해 사이에 다른 줄이 끼면 함께 나오지 않는다", "pk");
        Advance(5.1);
        Expect("P11 끼인 줄 뒤로 밀린다 (t=9.5)", "other", "dmg");

        Setup(log);
        OverlaySchedule.Line("old", LineKind.Attack, 1, 0);
        Advance(0.1);
        OverlaySchedule.Reset();
        OverlaySchedule.Line("new", LineKind.Immediate, 2, 0);
        Advance(0.001);
        Expect("P12 새 판: 지난 판 대기 줄 폐기 + 비우기가 먼저", "<clear>", "new");

        Setup(log);
        OverlaySchedule.Line("gone", LineKind.Attack, 1, 0);
        Advance(0.1);
        OverlaySchedule.Discard();
        Advance(2.0);
        Expect("P13 전투 이탈: 대기 줄만 버리고 비우지 않는다");

        Setup(log);
        OverlaySchedule.Line("prev", LineKind.Attack, 1, 0);
        OverlaySchedule.Reset();
        OverlaySchedule.Discard();
        OverlaySchedule.Line("next", LineKind.Immediate, 2, 0);
        Advance(0.001);
        Expect("P14 비우기 직후 이탈이 겹쳐도 비우기는 실행된다", "<clear>", "next");

        Setup(log, enabled: false);
        OverlaySchedule.Line("off1", LineKind.Attack, 1, 0);
        OverlaySchedule.Line("off2", LineKind.Skill, 2, 0);
        Advance(0.001);
        Expect("P15 동기화를 끄면 즉시 표시", "off1", "off2");

        Setup(log);
        OverlaySchedule.Line("h", LineKind.Attack, 1, 0);
        for (int i = 0; i < 13; i++) { OverlaySchedule.Tick(3.0f, 0.333f, 1f); Pump(); }
        Expect("P16 긴 끊김은 한 프레임 상한만큼만 흐른다 (t=4.33)");
        OverlaySchedule.Tick(3.0f, 0.333f, 1f);
        Pump();
        Expect("P16 열네 번째 끊김 뒤(t=4.66)", "h");

        Setup(log);
        OverlaySchedule.Tick(-1f, 0f, 1f);
        OverlaySchedule.Line("fb", LineKind.Immediate, 1, 0);
        System.Threading.Thread.Sleep(20);
        OverlaySchedule.Tick(-1f, 0f, 1f);
        Pump();
        Expect("P17 Unity 시각 실패 시 실시간 시계로 계속 흐른다", "fb");

        // 밀린 덩어리(봇 연속 차례)는 이미 화면보다 늦으므로 공개 지연을 늘리지 않는다.
        Setup(log);
        OverlaySchedule.Line("c", LineKind.Card, 1, 0);
        OverlaySchedule.Line("pk", LineKind.Attack, 2, 0);
        Advance(0.001);
        Expect("P18 카드", "c");
        Advance(4.75);
        Expect("P18 밀린 PK는 기존 공개 1.94초 (t=4.75, 2.87+1.94=4.81)");
        Advance(0.1);
        Expect("P18 t=4.85", "pk");

        Setup(log);
        OverlaySchedule.Line("c", LineKind.Card, 1, 0);
        OverlaySchedule.Line("d", LineKind.Dice, 2, 3);
        Advance(0.001);
        Expect("P19 카드", "c");
        Advance(2.8);
        Expect("P19 밀린 주사위는 덩어리 시작에 뜬다 (t=2.8)");
        Advance(0.1);
        Expect("P19 t=2.9", "d");

        Setup(log);
        Advance(0.2);
        OverlaySchedule.Sync(Op.TimeWasting, 14);
        Advance(0.001);
        OverlaySchedule.Line("d", LineKind.Dice, 2, 4);
        Advance(1.55);
        Expect("P20 동기화 직후(화면이 쉼) 주사위는 1.60초 뒤 (t=1.75)");
        Advance(0.1);
        Expect("P20 t=1.85", "d");

        // 본인 주사위는 결정 창 닫힘(5308, 9바이트)이 수 ms 뒤에 따라온다(측정 4 셋째 판).
        Setup(log);
        OverlaySchedule.Line("d", LineKind.Dice, 1, 10);
        Advance(0.01);
        OverlaySchedule.Sync(Op.TimeWasting, 9);
        Advance(0.001);
        Expect("P21 동기화가 바로 뒤따라도 주사위는 수신 뒤 1.60초 전에는 안 뜬다");
        Advance(1.55);
        Expect("P21 t=1.56");
        Advance(0.1);
        Expect("P21 t=1.66", "d");

        Setup(log);
        OverlaySchedule.Line("pk", LineKind.Attack, 1, 0);
        OverlaySchedule.Line("dmg", LineKind.Hit, 1, 0);
        Advance(1.0);
        OverlaySchedule.Sync(Op.TimeWasting, 14);
        OverlaySchedule.Line("c", LineKind.Card, 2, 0);
        Advance(0.001);
        Expect("P21b 동기화도 PK 결과를 수신 뒤 4.35초 전에는 내보내지 않고, 뒤 줄은 순서를 지킨다");
        Advance(3.3);
        Expect("P21b t=4.3");
        Advance(0.1);
        Expect("P21b t=4.4", "pk", "dmg", "c");

        // 커서보다 조금 늦게 시작하는 PK는 "밀린 덩어리"라도 수신 뒤 1.94초면 쉬는 화면보다 이르다.
        Setup(log);
        OverlaySchedule.Line("c", LineKind.Card, 1, 0);
        Advance(2.0);
        Expect("P22 카드", "c");
        OverlaySchedule.Line("pk", LineKind.Attack, 2, 0);
        Advance(4.3);
        Expect("P22 조금 밀린 PK도 수신 뒤 4.35초 전에는 안 뜬다 (t=6.3)");
        Advance(0.1);
        Expect("P22 t=6.4", "pk");

        RunScreenSignals(log);

        Console.WriteLine(_fail == 0 ? "=== 전부 통과 ===" : $"=== 실패 {_fail}건 ===");
        return _fail;
    }

    // 실제 패킷 → BattleLogger → MirrorTagged → 게이트 → 화면 신호까지 한 경로로 확인한다.
    static void RunLoggerTags(ManualLogSource log)
    {
        const long Char = 100, Monster = 200;
        var names = NameTable.Load(Path.Combine(Path.GetTempPath(), "apbl-harness-no-names.tsv"), _ => { });
        var lg = new BattleLogger(log, null, false, new HashSet<int>(), false, true, names);
        var tags = new List<string>();
        lg.MirrorTagged = (s, k, g, u, d) =>
        {
            if (k is LineKind.Dice or LineKind.Attack) tags.Add($"{k}:{d}");
            OverlaySchedule.Line(k.ToString(), k, g, u, d);
        };

        Setup(log, signals: true);
        lg.OnFrame(new FrameHeader(Op.RunningGame, 0, 0, 0), Frame.Msg(1, Frame.Cat(
            Frame.Fix64(1, 77),
            Frame.Msg(9, Frame.Cat(Frame.Fix64(1, Char), Frame.Varint(6, 0), Frame.Msg(10, Frame.Fix64(2, 1001)))),
            Frame.Msg(10, Frame.Cat(Frame.Fix64(1, Monster), Frame.Msg(10, Frame.Fix64(2, 5001)))))));
        Advance(0.001);
        Out.Clear();

        void Dice(long pid, params int[] pips)
        {
            var parts = new List<byte[]>();
            foreach (int p in pips) parts.Add(Frame.Varint(1, (ulong)p));
            parts.Add(Frame.Fix64(3, pid));
            lg.OnFrame(new FrameHeader(Op.ThrowDice, 0, 0, 0), Frame.Cat(parts.ToArray()));
        }
        void Battle(long attacker, long defender, bool counter) =>
            lg.OnFrame(new FrameHeader(Op.Battle, 0, 0, 0), Frame.Msg(1, Frame.Cat(
                Frame.Fix64(1, 1), Frame.Msg(2, Frame.Fix64(1, attacker)), Frame.Msg(3, Frame.Fix64(1, defender)),
                Frame.Varint(5, 1), Frame.Varint(8, counter ? 1UL : 0UL))));

        Dice(Char, 6);
        Dice(Monster, 4);
        Dice(Char, 3, 2);
        Battle(Char, Monster, counter: false);
        Battle(Monster, Char, counter: true);
        bool ok = string.Join(",", tags) == "Dice:6,Dice:0,Dice:203,Attack:0,Attack:1";
        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} L1 로거가 캐릭터 주사위 눈(두 개는 3+2 → 203)·몬스터 0·반격 여부를 넘긴다: "
                          + $"[{string.Join(", ", tags)}]");
        if (!ok) _fail++;

        Advance(1.0);
        Expect("L2 로거 경로로 들어온 결과는 신호 전에는 공개하지 않는다");
        Sig(ScreenSignal.DiceFace, false, 6);
        Advance(0.001);
        Expect("L2 캐릭터 주사위 눈 신호로 공개", "Dice");
        Sig(ScreenSignal.DiceFace, false, 3);
        Advance(0.001);
        Expect("L2 두 개 주사위는 눈 하나만 꺼져서는 공개하지 않고, 앞의 몬스터 주사위만 늦은 경로로", "Dice");
        Sig(ScreenSignal.DiceFace, false, 2);
        Advance(0.001);
        Expect("L2 두 눈이 모두 꺼지면 공개", "Dice");
        Sig(ScreenSignal.Window, true);
        Advance(3.0);
        Sig(ScreenSignal.Hit, true, instance: 1);
        Advance(0.001);
        Expect("L2 PK 타격에 공개", "Attack");
        Sig(ScreenSignal.Hit, false, instance: 1);
        Advance(7.0);
        Sig(ScreenSignal.Hit, true, instance: 2);
        Advance(0.001);
        Expect("L2 반격 표시로 같은 창의 다음 타격에 공개", "Attack");
    }

    // docs/SIGNAL-GATING.md 6절 검증 계획
    static void RunScreenSignals(ManualLogSource log)
    {
        Console.WriteLine("=== 화면 신호 공개 ===");
        const LineKind Dice = LineKind.Dice, Pk = LineKind.Attack;

        Setup(log, signals: true);
        OverlaySchedule.Line("d1", Dice, 1, 3, 6);
        OverlaySchedule.Line("d2", Dice, 2, 3, 6);
        Advance(1.0);
        Expect("G1 신호 전에는 주사위를 공개하지 않는다");
        Sig(ScreenSignal.DiceFace, false, 6);
        Advance(0.001);
        Expect("G1 같은 눈 첫 신호", "d1");
        Advance(1.0);
        Sig(ScreenSignal.DiceFace, false, 6);
        Advance(0.001);
        Expect("G1 같은 눈 둘째 신호", "d2");

        Setup(log, signals: true);
        OverlaySchedule.Line("d1", Dice, 1, 3, 3);
        OverlaySchedule.Line("d2", Dice, 2, 5, 5);
        Advance(0.5);
        Sig(ScreenSignal.DiceFace, false, 5);
        Advance(0.001);
        Expect("G2 앞 주사위 신호가 빠지면 뒤 신호에 함께 공개", "d1", "d2");
        OverlaySchedule.Line("d3", Dice, 3, 3, 3);
        Advance(0.5);
        Sig(ScreenSignal.DiceFace, false, 3);
        Advance(0.001);
        Expect("G2 늦게 온 앞 신호는 장부에서 소비되어 다음 주사위를 공개하지 않는다");
        Sig(ScreenSignal.DiceFace, false, 3);
        Advance(0.001);
        Expect("G2 제 신호", "d3");

        Setup(log, signals: true);
        OverlaySchedule.Line("pk", Pk, 1, 0, 0);
        OverlaySchedule.Line("dmg", LineKind.Hit, 1, 0);
        OverlaySchedule.Line("after", LineKind.Effect, 2, 0);
        Sig(ScreenSignal.Window, true);
        Advance(3.0);
        Sig(ScreenSignal.Hit, true, instance: 1);
        Sig(ScreenSignal.Hit, true, instance: 2);
        Sig(ScreenSignal.Hit, true, instance: 3);
        Advance(0.001);
        Expect("G3 연타(겹친 인스턴스 3개)는 스트라이크 하나, 피해 줄은 함께", "pk", "dmg");
        Advance(1.0);
        Expect("G3 뒤 줄은 창 꺼짐까지 기다린다");
        Sig(ScreenSignal.Hit, false, instance: 1);
        Sig(ScreenSignal.Hit, false, instance: 2);
        Sig(ScreenSignal.Hit, false, instance: 3);
        Sig(ScreenSignal.Window, false);
        Advance(0.001);
        Expect("G3 창 꺼짐 뒤", "after");

        Setup(log, signals: true);
        OverlaySchedule.Line("pk1", Pk, 1, 0, 0);
        OverlaySchedule.Line("pk2", Pk, 2, 0, 0);
        Sig(ScreenSignal.Window, true);
        Advance(3.0);
        Sig(ScreenSignal.Hit, true, instance: 1);
        Sig(ScreenSignal.Hit, true, instance: 2);
        Advance(0.001);
        Expect("G4 연타 둘째 인스턴스는 다음 PK를 공개하지 않는다", "pk1");
        Sig(ScreenSignal.Hit, false, instance: 1);
        Sig(ScreenSignal.Hit, false, instance: 2);
        Advance(1.0);
        Sig(ScreenSignal.Hit, true, instance: 3);
        Advance(0.001);
        Expect("G4 반격이 아닌 다음 PK는 두 번째 스트라이크로 공개하지 않는다");
        Sig(ScreenSignal.Hit, false, instance: 3);
        Sig(ScreenSignal.Window, false);
        Sig(ScreenSignal.Window, true);
        Advance(3.0);
        Sig(ScreenSignal.Hit, true, instance: 4);
        Advance(0.001);
        Expect("G4 다음 창의 첫 스트라이크", "pk2");

        Setup(log, signals: true);
        OverlaySchedule.Line("pk", Pk, 1, 0, 0);
        OverlaySchedule.Line("ctr", Pk, 2, 0, 1);
        Sig(ScreenSignal.Window, true);
        Advance(3.0);
        Sig(ScreenSignal.Hit, true, instance: 1);
        Advance(0.001);
        Expect("G5 원래 PK는 첫 스트라이크", "pk");
        Sig(ScreenSignal.Hit, false, instance: 1);
        Advance(7.0);
        Expect("G5 반격은 아직");
        Sig(ScreenSignal.Hit, true, instance: 2);
        Advance(0.001);
        Expect("G5 반격은 같은 창의 다음 스트라이크", "ctr");

        Setup(log, signals: true);
        OverlaySchedule.Line("pk", Pk, 1, 0, 0);
        Sig(ScreenSignal.Window, true);
        Advance(3.0);
        Sig(ScreenSignal.Hit, true, instance: 1);
        Advance(0.001);
        Expect("G6 원래 PK", "pk");
        Sig(ScreenSignal.Hit, false, instance: 1);
        Advance(7.0);
        Sig(ScreenSignal.Hit, true, instance: 2);
        Advance(0.001);
        Sig(ScreenSignal.Hit, false, instance: 2);
        OverlaySchedule.Line("ctr", Pk, 2, 0, 1);
        OverlaySchedule.Line("d", Dice, 3, 4, 4);
        Advance(1.0);
        Expect("G6 스트라이크보다 늦게 받은 반격은 그 스트라이크로 공개하지 않는다");
        Sig(ScreenSignal.Window, false);
        Advance(0.001);
        Expect("G6 창이 닫혀도 공개하지 않는다");
        Sig(ScreenSignal.DiceFace, false, 4);
        Advance(0.001);
        Expect("G6 뒤 사건의 신호에 늦게 공개", "ctr", "d");

        Setup(log, signals: true);
        OverlaySchedule.Line("old", Pk, 1, 0, 0);
        OverlaySchedule.Line("d", Dice, 2, 2, 2);
        Advance(0.5);
        Sig(ScreenSignal.DiceFace, false, 2);
        Advance(0.001);
        Expect("G7 앞 PK는 뒤 주사위 신호로 늦게 공개(장부 등록)", "old", "d");
        OverlaySchedule.Line("pk", Pk, 3, 0, 0);
        OverlaySchedule.Line("ctr", Pk, 4, 0, 1);
        Advance(0.5);
        Sig(ScreenSignal.Window, true);
        Advance(3.0);
        Sig(ScreenSignal.Hit, true, instance: 1);
        Advance(0.001);
        Expect("G7 첫 스트라이크는 장부 항목에 소비");
        Sig(ScreenSignal.Hit, false, instance: 1);
        Advance(7.0);
        Sig(ScreenSignal.Hit, true, instance: 2);
        Advance(0.001);
        Expect("G7 그 창의 두 번째 스트라이크는 다음 결과를 공개하지 않는다");
        Sig(ScreenSignal.Hit, false, instance: 2);
        Sig(ScreenSignal.Window, false);
        Sig(ScreenSignal.Window, true);
        Advance(3.0);
        Sig(ScreenSignal.Hit, true, instance: 3);
        Advance(0.001);
        Expect("G7 다음 창에서 제 순서로", "pk");

        Setup(log, signals: true);
        OverlaySchedule.Line("pk", Pk, 1, 0, 0);
        Sig(ScreenSignal.Window, true);
        Advance(10.0);
        Sig(ScreenSignal.Window, false);
        Advance(0.001);
        Expect("G8 결과 수신 뒤 10초 유지된 타격 없는 창은 공개하지 않는다");
        OverlaySchedule.Line("d", Dice, 2, 5, 5);
        Advance(0.5);
        Sig(ScreenSignal.DiceFace, false, 5);
        Advance(0.001);
        Expect("G8 뒤 사건의 신호에 늦게 공개", "pk", "d");

        Setup(log, signals: true);
        OverlaySchedule.Line("pk", Pk, 1, 0, 0);
        Advance(3.0);
        Sig(ScreenSignal.Window, false);
        Sig(ScreenSignal.Hit, true, instance: 1);
        Advance(0.001);
        Expect("G9 켜짐 없는 꺼짐과 창 밖 타격은 무시");

        Setup(log, signals: true);
        Sig(ScreenSignal.Window, true);
        Advance(5.0);
        OverlaySchedule.Line("pk", Pk, 1, 0, 0);
        Advance(2.7);
        Sig(ScreenSignal.Hit, true, instance: 1);
        Advance(0.001);
        Expect("G10 수신보다 먼저 열린 창도 수신 뒤 첫 스트라이크로 공개", "pk");

        Setup(log, signals: true);
        Advance(0.001, 2f);
        OverlaySchedule.Line("pk", Pk, 1, 0, 0);
        Sig(ScreenSignal.Window, true);
        Advance(0.5, 2f);
        Sig(ScreenSignal.Hit, true, instance: 1);
        Advance(0.001, 2f);
        Expect("G11 2배속, 켜짐 뒤 0.5초의 타격도 공개(시간 문턱 없음)", "pk");

        Setup(log, signals: true);
        OverlaySchedule.Line("d", Dice, 1, 2, 2);
        Advance(0.1);
        OverlaySchedule.Sync(Op.TimeWasting, 9);
        OverlaySchedule.Sync(Op.TimeWasting, 14);
        OverlaySchedule.Sync(5036, 14);
        Advance(1.0);
        Expect("G12 동기화는 신호 대기 주사위를 내보내지 않는다");
        Sig(ScreenSignal.DiceFace, false, 2);
        Advance(0.001);
        Expect("G12 신호", "d");

        Setup(log, maxLagMs: 1000, signals: true);
        OverlaySchedule.Line("d", Dice, 1, 6, 6);
        OverlaySchedule.Line("turn", LineKind.Turn, 2, 0);
        OverlaySchedule.Sync(Op.TimeWasting, 14);
        OverlaySchedule.Line("card", LineKind.Card, 3, 0);
        OverlaySchedule.Line("eff", LineKind.Effect, 4, 0);
        Advance(10.0);
        Expect("G13 주사위 장벽 뒤 줄은 보류");
        Sig(ScreenSignal.DiceFace, false, 6);
        Advance(0.001);
        Expect("G13 주사위만 공개", "d");
        Advance(1.5);
        Expect("G13 뒤 줄은 앵커(신호+1.56초) 전에는 안 뜬다 — 동기화·지연 상한도 못 당긴다 (t=11.50)");
        Advance(0.1);
        Expect("G13 앵커에서 재예약 (t=11.60)", "turn", "card");
        Advance(0.9);
        Expect("G13 한꺼번에 몰리지 않는다 (t=12.50)");
        Advance(0.1);
        Expect("G13 지연 상한(1초)은 수신이 아니라 앵커부터 센다 (t=12.60)", "eff");

        Setup(log, signals: true);
        OverlaySchedule.Line("pk", Pk, 1, 0, 0);
        Advance(59.9);
        Expect("G14 상한 전");
        Advance(0.2);
        Expect("G14 상한 60초에 공개(장부 등록)", "pk");
        OverlaySchedule.Line("pk2", Pk, 2, 0, 0);
        Sig(ScreenSignal.Window, true);
        Advance(3.0);
        Sig(ScreenSignal.Hit, true, instance: 1);
        Advance(0.001);
        Expect("G14 상한 공개 뒤 늦은 타격은 장부에서 소비");
        Sig(ScreenSignal.Hit, false, instance: 1);
        Sig(ScreenSignal.Window, false);
        Sig(ScreenSignal.Window, true);
        Advance(3.0);
        Sig(ScreenSignal.Hit, true, instance: 2);
        Advance(0.001);
        Expect("G14 다음 PK는 제 타격에", "pk2");

        Setup(log, signals: true);
        OverlaySchedule.Line("pk", Pk, 1, 0, 0);
        Sig(ScreenSignal.Window, true);
        Advance(1.0);
        OverlaySchedule.Discard();
        Advance(0.001);
        Sig(ScreenSignal.Hit, true, instance: 1);
        Sig(ScreenSignal.Hit, false, instance: 1);
        Advance(0.001);
        Expect("G15 이탈하면 대기 줄·창을 버린다");
        OverlaySchedule.Line("pk2", Pk, 2, 0, 0);
        Sig(ScreenSignal.Window, true);
        Advance(3.0);
        Sig(ScreenSignal.Hit, true, instance: 2);
        Advance(0.001);
        Expect("G15 새 세대는 정상", "pk2");

        Setup(log, signals: true);
        OverlaySchedule.Line("pk1", Pk, 1, 0, 0);
        OverlaySchedule.Line("pk2", Pk, 2, 0, 0);
        Sig(ScreenSignal.Window, true);
        Advance(3.0);
        Sig(ScreenSignal.Hit, true, instance: 1);
        Advance(0.001);
        Expect("G17 첫 PK", "pk1");
        Sig(ScreenSignal.Window, false);
        Sig(ScreenSignal.Window, true);
        Advance(3.0);
        Sig(ScreenSignal.Hit, true, instance: 2);
        Advance(0.001);
        Expect("G17 타격 꺼짐을 놓쳐도 다음 창의 첫 타격은 스트라이크", "pk2");

        Setup(log, signals: true);
        OverlaySchedule.Line("single", Dice, 1, 5, 5);
        OverlaySchedule.Line("double", Dice, 2, 7, 205);
        Advance(1.0);
        Sig(ScreenSignal.DiceFace, false, 5);
        Advance(0.001);
        Expect("G18 같은 눈은 수신 순서상 앞의 한 개짜리 주사위가 먼저", "single");
        Advance(1.0);
        Sig(ScreenSignal.DiceFace, false, 5);
        Sig(ScreenSignal.DiceFace, false, 2);
        Advance(0.001);
        Expect("G18 두 개 주사위(5+2)는 두 눈이 모두 꺼지면 공개", "double");

        Setup(log, signals: true);
        OverlaySchedule.Line("double", Dice, 1, 7, 205);
        OverlaySchedule.Line("after", Dice, 2, 4, 4);
        Advance(0.5);
        Sig(ScreenSignal.DiceFace, false, 5);
        Advance(0.001);
        Expect("G18 눈 하나만 꺼진 두 개 주사위는 기다린다");
        Sig(ScreenSignal.DiceFace, false, 4);
        Advance(0.001);
        Expect("G18 뒤 주사위 신호가 오면 늦은 경로로 함께", "double", "after");
        OverlaySchedule.Line("next", Dice, 3, 2, 2);
        Advance(0.5);
        Sig(ScreenSignal.DiceFace, false, 2);
        Advance(0.001);
        Expect("G18 못 받은 눈(2)은 장부에 남아 늦은 신호를 소비한다");
        Sig(ScreenSignal.DiceFace, false, 2);
        Advance(0.001);
        Expect("G18 제 신호", "next");

        // 측정 4 다섯째 판 재생: 원래 PK와 반격 사이의 5036이 창 장벽에 걸려 반격이 창 종료까지 밀렸다.
        Setup(log, signals: true);
        OverlaySchedule.Line("pk", Pk, 1, 0, 0);
        OverlaySchedule.Line("pkhit", LineKind.Hit, 1, 0);
        Sig(ScreenSignal.Window, true);
        Advance(3.0);
        Sig(ScreenSignal.Hit, true, instance: 1);
        Advance(0.001);
        Expect("G19 원래 PK는 첫 스트라이크", "pk", "pkhit");
        Sig(ScreenSignal.Hit, false, instance: 1);
        OverlaySchedule.Sync(5036, 9);
        Advance(2.0);
        OverlaySchedule.Line("ctr", Pk, 2, 0, 1);
        OverlaySchedule.Line("ctrhit", LineKind.Hit, 2, 0);
        OverlaySchedule.Line("after", LineKind.Skill, 3, 0);
        Advance(3.0);
        Expect("G19 반격은 아직");
        Sig(ScreenSignal.Hit, true, instance: 2);
        Advance(0.001);
        Expect("G19 5036을 넘어 반격·피해를 타격 시점에", "ctr", "ctrhit");
        Sig(ScreenSignal.Hit, false, instance: 2);
        Advance(1.5);
        Expect("G19 뒤 줄은 창 장벽이 유지돼 기다린다");
        Sig(ScreenSignal.Window, false);
        Advance(0.001);
        Expect("G19 창 종료 뒤 뒤 줄", "after");

        Setup(log, signals: true);
        OverlaySchedule.Line("pk", Pk, 1, 0, 0);
        Sig(ScreenSignal.Window, true);
        Advance(3.0);
        Sig(ScreenSignal.Hit, true, instance: 1);
        Advance(0.001);
        Expect("G20 원래 PK", "pk");
        Sig(ScreenSignal.Hit, false, instance: 1);
        OverlaySchedule.Line("card", LineKind.Card, 2, 0);
        OverlaySchedule.Line("ctr", Pk, 3, 0, 1);
        Advance(5.0);
        Sig(ScreenSignal.Hit, true, instance: 2);
        Advance(0.001);
        Expect("G20 앞에 보이는 줄이 있으면 이번 규칙으로 당기지 않는다");
        Sig(ScreenSignal.Window, false);
        Advance(0.001);
        Expect("G20 창 종료 뒤 기존대로", "card", "ctr");

        // 측정 4 일곱째 판: 타격 없는 캐릭터의 PK가 장부에 남아 뒤 PK 타격을 계속 소비했다.
        void WeakWindow()
        {
            Sig(ScreenSignal.Window, true);
            Advance(3.0);
            Sig(ScreenSignal.Window, false);
            Advance(0.001);
        }
        void HitWindow(int instance)
        {
            Sig(ScreenSignal.Window, true);
            Advance(3.0);
            Sig(ScreenSignal.Hit, true, instance: instance);
            Advance(0.001);
        }

        Setup(log, signals: true);
        OverlaySchedule.Line("p1", Pk, 1, 0, 0);
        OverlaySchedule.Line("p2", Pk, 2, 0, 0);
        WeakWindow();
        WeakWindow();
        Expect("W1 타격 없는 창 2개는 결과를 공개하지 않는다");
        OverlaySchedule.Line("d", Dice, 3, 3, 3);
        Advance(0.5);
        Sig(ScreenSignal.DiceFace, false, 3);
        Advance(0.001);
        Expect("W1 뒤 주사위 신호에 늦게 공개", "p1", "p2", "d");
        OverlaySchedule.Line("p3", Pk, 4, 0, 0);
        HitWindow(1);
        Expect("W1 약한 창에 정산된 결과는 장부가 없어 다음 PK 타격이 바로 공개", "p3");

        Setup(log, signals: true);
        OverlaySchedule.Line("p1", Pk, 1, 0, 0);
        OverlaySchedule.Line("p2", Pk, 2, 0, 0);
        WeakWindow();
        HitWindow(1);
        Expect("W2 타격은 약한 창에 정산된 결과를 건너뛰어 다음 PK에 (앞 결과는 늦은 경로)", "p1", "p2");
        Sig(ScreenSignal.Window, false);
        OverlaySchedule.Line("p3", Pk, 3, 0, 0);
        HitWindow(2);
        Expect("W2 장부 소비 없이 다음 PK", "p3");

        Setup(log, signals: true);
        WeakWindow();
        OverlaySchedule.Line("pk", Pk, 1, 0, 0);
        HitWindow(1);
        Expect("W3 결과 없는 약한 창은 뒤에 받은 결과를 미리 정산하지 않는다", "pk");

        Setup(log, signals: true);
        OverlaySchedule.Line("pk", Pk, 1, 0, 0);
        Advance(60.1);
        Expect("W4 상한 공개(장부 등록)", "pk");
        WeakWindow();
        OverlaySchedule.Line("pk2", Pk, 2, 0, 0);
        HitWindow(1);
        Expect("W4 약한 창이 장부 항목을 정산하면 다음 타격은 소비되지 않는다", "pk2");

        Setup(log, signals: true);
        OverlaySchedule.Line("pk", Pk, 1, 0, 0);
        OverlaySchedule.Line("ctr", Pk, 2, 0, 1);
        HitWindow(1);
        Expect("W5 원래 PK", "pk");
        Sig(ScreenSignal.Hit, false, instance: 1);
        Advance(7.0);
        Sig(ScreenSignal.Hit, true, instance: 2);
        Advance(0.001);
        Expect("W5 반격은 같은 창의 다음 타격", "ctr");
        Sig(ScreenSignal.Hit, false, instance: 2);
        Sig(ScreenSignal.Window, false);
        OverlaySchedule.Line("pk3", Pk, 3, 0, 0);
        HitWindow(3);
        Expect("W5 반격이 창을 따로 소비하지 않아 다음 PK는 제 타격에", "pk3");

        Setup(log, signals: true);
        OverlaySchedule.Line("ctr", Pk, 1, 0, 1);
        WeakWindow();
        OverlaySchedule.Line("pk", Pk, 2, 0, 0);
        HitWindow(1);
        Expect("W6 약한 창은 반격 결과를 정산하지 않는다 — 원래 PK 후보에서도 빠진다", "ctr", "pk");

        RunLoggerTags(log);

        Setup(log, signals: false);
        OverlaySchedule.Line("d", Dice, 1, 3, 3);
        Advance(1.55);
        Expect("G16 신호를 끄면 기존 모델(쉬는 화면 1.60초)");
        Advance(0.1);
        Expect("G16 t=1.65", "d");
    }
}
