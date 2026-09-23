using System;
using System.Collections.Generic;
using AstralPartyBattleLog.Log;
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

    static void Setup(ManualLogSource log, bool enabled = true, int maxLagMs = 60000)
    {
        OverlaySchedule.Init(log, enabled, maxLagMs, false);
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

        Console.WriteLine(_fail == 0 ? "=== 전부 통과 ===" : $"=== 실패 {_fail}건 ===");
        return _fail;
    }
}
