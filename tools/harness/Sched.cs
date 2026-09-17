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

        // 연출 길이(초): 카드 2.87, 제출 2.23, PK 9.45(공개 1.94), 스킬 1.92,
        //                판 시작 18.15, 한 칸 0.30

        Setup(log);
        OverlaySchedule.Line("atk", LineKind.Attack, 1, 0);
        Advance(1.9);
        Expect("P1 PK는 공개 시점까지 기다린다 (t=1.9)");
        Advance(0.1);
        Expect("P1 t=2.0", "atk");
        OverlaySchedule.Line("skill", LineKind.Skill, 2, 0);
        Advance(7.0);
        Expect("P1 다음 사건은 앞 연출이 끝나야 한다 (t=9.0)");
        Advance(0.5);
        Expect("P1 t=9.5", "skill");

        Setup(log);
        OverlaySchedule.Line("atk", LineKind.Attack, 10, 0);
        OverlaySchedule.Line("dmg", LineKind.Hit, 10, 0);
        Advance(1.9);
        Expect("P2 PK 결과와 피해는 같은 그룹 (t=1.9)");
        Advance(0.1);
        Expect("P2 t=2.0 함께", "atk", "dmg");

        Setup(log, maxLagMs: 1000);
        OverlaySchedule.Line("a1", LineKind.Attack, 1, 0);
        Advance(0.1);
        OverlaySchedule.Line("a2", LineKind.Attack, 2, 0);
        Advance(1.85);
        Expect("P3 최대 지연이 덩어리 시작을 자른다 (t=1.95)", "a1");
        Advance(1.1);
        Expect("P3 t=3.05", "a2");

        Setup(log);
        OverlaySchedule.Line("a1", LineKind.Attack, 1, 0);
        Advance(0.2);
        OverlaySchedule.Sync();
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
        Advance(0.9, 2f);
        Expect("P6 2배속은 절반 (t=0.9)");
        Advance(0.1, 2f);
        Expect("P6 t=1.0", "a");

        Setup(log);
        Advance(0.001, 0f);
        OverlaySchedule.Line("z", LineKind.Attack, 1, 0);
        Advance(1.9, 0f);
        Expect("P7 비정상 배속(0)은 1.0 (t=1.9)");
        Advance(0.1, 0f);
        Expect("P7 t=2.0", "z");

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
        OverlaySchedule.Line("dice", LineKind.Dice, 1, 5);
        OverlaySchedule.Line("after", LineKind.Effect, 2, 0);
        Advance(0.001);
        Expect("P10 주사위 줄은 바로 뜨고 칸 수만큼 커서가 간다", "dice");
        Advance(1.4);
        Expect("P10 다섯 칸 = 1.5초 (t=1.4)");
        Advance(0.15);
        Expect("P10 t=1.55", "after");

        Setup(log);
        OverlaySchedule.Line("pk", LineKind.Attack, 1, 0);
        OverlaySchedule.Line("other", LineKind.Effect, 2, 0);
        OverlaySchedule.Line("dmg", LineKind.Hit, 1, 0);
        Advance(2.0);
        Expect("P11 한계: PK 결과와 피해 사이에 다른 줄이 끼면 함께 나오지 않는다", "pk");
        Advance(7.5);
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
        for (int i = 0; i < 5; i++) { OverlaySchedule.Tick(3.0f, 0.333f, 1f); Pump(); }
        Expect("P16 긴 끊김은 한 프레임 상한만큼만 흐른다 (t=1.67)");
        OverlaySchedule.Tick(3.0f, 0.333f, 1f);
        Pump();
        Expect("P16 여섯 번째 끊김 뒤(t=2.0)", "h");

        Setup(log);
        OverlaySchedule.Tick(-1f, 0f, 1f);
        OverlaySchedule.Line("fb", LineKind.Immediate, 1, 0);
        System.Threading.Thread.Sleep(20);
        OverlaySchedule.Tick(-1f, 0f, 1f);
        Pump();
        Expect("P17 Unity 시각 실패 시 실시간 시계로 계속 흐른다", "fb");

        Console.WriteLine(_fail == 0 ? "=== 전부 통과 ===" : $"=== 실패 {_fail}건 ===");
        return _fail;
    }
}
