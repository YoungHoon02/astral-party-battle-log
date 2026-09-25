using System;
using System.Collections.Generic;
using System.Linq;
using AstralPartyBattleLog.Log;
using AstralPartyBattleLog.UI;
using BepInEx.Logging;

// 건강 요약(S2)과 후보 링 버퍼(S2, 진단 전용) 검증.
static class HealthTests
{
    static int _fail;

    static void Check(bool ok, string label, string? detail = null)
    {
        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {label}{(detail is null ? "" : $": {detail}")}");
        if (!ok) _fail++;
    }

    static void Pump() => OverlaySchedule.Pump(_ => { }, _ => { }, () => { });

    static void Step(double s)
    {
        OverlaySchedule.Tick((float)s, 0f, 1f);
        Pump();
    }

    public static int Run()
    {
        _fail = 0;
        Console.WriteLine();
        Console.WriteLine("=== 건강 요약 ===");
        var log = new ManualLogSource("health");
        var lines = new List<string>();
        OverlaySchedule.Init(log, true, 60000, false);
        OverlaySchedule.Discard();
        OverlaySchedule.ForgetBanner();
        Step(0.01);
        ScheduleHealth.Init(lines.Add, null);

        OverlaySchedule.Reset();
        OverlaySchedule.Line("d", LineKind.Dice, 1, 3, 3);
        OverlaySchedule.Line("pk", LineKind.Attack, 2, 0, 0);
        Step(0.5);
        OverlaySchedule.Screen(ScreenSignal.DiceFace, 3, false, (IntPtr)1);
        OverlaySchedule.Screen(ScreenSignal.DiceFace, 4, false, (IntPtr)2);
        Step(0.01);
        Step(61.0);
        OverlaySchedule.Discard();
        Check(lines.Count == 1, "HS1 전투 이탈이 관측을 닫고 1줄을 남긴다", lines.Count == 1 ? lines[0] : $"{lines.Count}줄");
        string a = lines.FirstOrDefault() ?? "";
        Check(a.Contains("start=reset end=left") && a.Contains("dice=1 1/0/0/0 1/0/0/0 0") && a.Contains("pk=1 0/0/1/0 0/0/1/0 0"),
              "HS1 게이트 생성·해결·출력이 종류와 사유별로 남는다");
        Check(a.Contains("signals dice=2") && a.Contains("stray=1"), "HS1 신호 수와 진단 사건이 남는다");

        ScheduleHealth.Close("unload");
        OverlaySchedule.Discard();
        Check(lines.Count == 1, "HS2 뒤따르는 언로드·이탈은 닫힌 관측을 다시 쓰지 않는다");

        OverlaySchedule.Reset();
        OverlaySchedule.Reset();
        Check(lines.Count == 2 && lines[1].Contains("end=reset"), "HS3 닫히지 않은 관측은 다음 Reset이 요약하고 새 관측을 연다");
        ScheduleHealth.Close("unload");
        Check(lines.Count == 3 && lines[2].Contains("end=unload"), "HS3 언로드가 첫 종료 사건이면 그것으로 닫는다");

        OverlaySchedule.Line("t", LineKind.Turn, 1, 0, 0);
        OverlaySchedule.Discard();
        Check(lines.Count == 4 && lines[3].Contains("start=partial"), "HS4 판 시작 없이 전투 줄이 오면 부분 관측으로 명시한다");

        OverlaySchedule.Reset();
        OverlaySchedule.Line("late", LineKind.Dice, 1, 3, 5);
        Step(0.01);
        OverlaySchedule.Discard();
        long trailing = ScheduleHealth.Trailing;
        OverlaySchedule.Reset();
        Step(0.01);
        OverlaySchedule.Discard();
        Check(ScheduleHealth.Trailing > trailing && lines[^1].Contains("dice=0 0/0/0/0 0/0/0/0 0"),
              "HS5 닫힌 관측의 후행 입력은 다음 게임에 섞지 않고 따로 센다", $"trailing {trailing}→{ScheduleHealth.Trailing}");
        Check(lines[^2].Contains("dice=1 0/0/0/0 0/0/0/0 1"), "HS5 종료 때 미해결 게이트 수가 남는다");

        OverlaySchedule.Reset();
        OverlaySchedule.NoteHook(HookState.Installed);
        OverlaySchedule.NoteHook(HookState.Disabled);
        OverlaySchedule.FallBackToModel();
        OverlaySchedule.Discard();
        Check(lines[^1].Contains("hook=disabled mode=model changes=3"), "HS6 후킹 상태와 변화 횟수를 구별한다", Tail(lines[^1], "| hook"));
        OverlaySchedule.Reset();
        OverlaySchedule.Discard();
        Check(lines[^1].Contains("hook=disabled mode=model changes=0"), "HS6 현재 후킹 상태는 새 관측에 승계된다");
        Check(!OverlaySchedule.TraceTiming, "HS7 TraceTiming이 꺼져 있어도 요약이 나온다");

        RunCandidates();
        Console.WriteLine(_fail == 0 ? "=== 건강·후보 전부 통과 ===" : $"=== 건강·후보 실패 {_fail}건 ===");
        return _fail;
    }

    static string Tail(string s, string from)
    {
        int i = s.IndexOf(from, StringComparison.Ordinal);
        return i < 0 ? s : s.Substring(i, Math.Min(60, s.Length - i));
    }

    static void RunCandidates()
    {
        Console.WriteLine();
        Console.WriteLine("=== 후보 링 버퍼 ===");
        var reports = new List<string>();
        ScreenCandidates.Init(reports.Add);
        const byte Untagged = ScreenCandidates.NoTag;

        long t = 1_000_000;
        ScreenCandidates.Transition("Root/Busy", t, true, Untagged);
        ScreenCandidates.Transition("Root/Rare", t + 100_000, true, Untagged);
        for (int i = 0; i < 6; i++) ScreenCandidates.Transition("Root/Busy", t + 200_000 + i * 1000, i % 2 == 0, Untagged);
        ScreenCandidates.Transition("BattleShow(Clone)", t + 300_000, true, (byte)ScreenSignal.Window);
        ScreenCandidates.Transition("Root/Rare", t + 400_000, false, Untagged);
        ScreenCandidates.Gap(GapKind.Late, "dice", t, t + 500_000);
        ScreenCandidates.Gap(GapKind.Weak, "pk", t + 450_000, t + 600_000);
        ScreenCandidates.Pump(t + 550_000, 1);
        Check(reports.Count == 0, "CB1 구간이 끝나기 전에는 훑지 않는다");
        ScreenCandidates.Pump(t + 700_000, 1);
        string r1 = reports.FirstOrDefault() ?? "";
        Check(reports.Count == 1 && r1.Contains("late:dice#") && r1.Contains("weak:pk#"), "CB1 겹치는 구간은 합치되 원래 사건 번호를 남긴다", r1);
        Check(r1.Contains("top=Root/Rare on1/off1") && !r1.Contains("Root/Busy on"), "CB1 1~2회 바뀐 경로를 켜짐·꺼짐 따로 올리고 잦은 경로는 뺀다");
        Check(r1.Contains("window=1") && r1.Contains(" complete"), "CB1 알려진 신호는 후보가 아니라 known으로 센다");

        reports.Clear();
        ScreenCandidates.Init(reports.Add);
        for (int i = 0; i < ScreenCandidates.RingSize * 2; i++) ScreenCandidates.Transition("Root/Spam", 10_000 + i, i % 2 == 0, Untagged);
        Check(ScreenCandidates.State().Contains("strings=1/"), "CB2 링이 덮어쓴 경로 문자열은 회수된다", ScreenCandidates.State());
        ScreenCandidates.Gap(GapKind.Cap, "pk", 10_000, 10_000 + ScreenCandidates.RingSize * 2);
        ScreenCandidates.Pump(10_000 + ScreenCandidates.RingSize * 2 + 1, 1);
        for (int k = 0; k < 10 && reports.Count == 0; k++) ScreenCandidates.Pump(10_000 + ScreenCandidates.RingSize * 2 + 2 + k, 1);
        Check(reports.Count == 1 && reports[0].Contains("incomplete") && reports[0].Contains("front-overwritten"),
              "CB2 구간 앞부분을 덮어썼으면 불완전으로 표시한다", reports.FirstOrDefault());

        reports.Clear();
        ScreenCandidates.Init(reports.Add);
        ScreenCandidates.Transition("Root/Other", 2_000_000, true, Untagged);
        ScreenCandidates.Transition("Root/Other", 2_000_100, false, Untagged);
        ScreenCandidates.Transition("Root/Other", 2_000_200, true, Untagged);
        ScreenCandidates.Budget(2_100_000);
        ScreenCandidates.Gap(GapKind.Late, "dice", 2_000_000, 2_200_000);
        ScreenCandidates.Pump(2_300_000, 1);
        Check(reports.Count == 1 && reports[0].Contains("flags=budget") && reports[0].Contains("absence not proven"),
              "CB3 예산 초과가 낀 구간은 신호 없음으로 단정하지 않고 없는 이유를 적는다", reports.FirstOrDefault());

        reports.Clear();
        ScreenCandidates.Init(reports.Add);
        for (int i = 0; i < 100; i++) ScreenCandidates.Gap(GapKind.Stray, "signal", 5_000_000 + i * 1_000_000, 5_000_000 + i * 1_000_000 + 10);
        Check(ScreenCandidates.State().Contains($"jobs={ScreenCandidates.MaxJobs}") && ScreenCandidates.State().Contains("jobDrops=68"),
              "CB4 대량 동시 구간은 작업 상한에서 버리고 수를 센다", ScreenCandidates.State());
        ScreenCandidates.Pump(1_000_000_000, 1);
        Check(reports.Count == ScreenCandidates.ReportsPerFrame, "CB4 프레임당 보고 수를 제한한다", $"{reports.Count}줄");
        for (int k = 0; k < 40; k++) ScreenCandidates.Pump(1_000_000_000 + k, 1);
        Check(reports.Count == ScreenCandidates.MaxJobs && ScreenCandidates.State().Contains("jobs=0"), "CB4 남은 작업은 다음 프레임들에 이어서 보고한다");
        Check(reports[0].Contains("no transitions recorded"), "CB4 후보가 없으면 없는 이유를 보고한다", reports[0]);

        reports.Clear();
        ScreenCandidates.Init(reports.Add);
        for (int i = 0; i < ScreenCandidates.MaxStrings + 100; i++) ScreenCandidates.Transition($"Root/Unique{i}", 7_000_000 + i, true, Untagged);
        Check(ScreenCandidates.State().Contains($"strings={ScreenCandidates.MaxStrings}/") && ScreenCandidates.State().Contains("tableFull=100"),
              "CB5 문자열 표가 차면 새 후보를 버리고 수를 센다", ScreenCandidates.State());
        ScreenCandidates.Init(reports.Add);
        ScreenCandidates.Transition(new string('x', 500), 8_000_000, true, Untagged);
        Check(ScreenCandidates.State().Contains($"chars={ScreenCandidates.MaxPathChars} "), "CB5 경로 하나의 길이도 상한으로 자른다",
              ScreenCandidates.State());

        reports.Clear();
        ScreenCandidates.Init(reports.Add);
        ScreenCandidates.Transition("Root/Keep", 9_000_000, true, Untagged);
        for (int i = 0; i < ScreenCandidates.ScanPerFrame; i++) ScreenCandidates.Transition("Root/Fill", 9_000_001 + i, i % 2 == 0, Untagged);
        ScreenCandidates.Gap(GapKind.Late, "dice", 9_000_000, 9_000_000 + ScreenCandidates.ScanPerFrame + 10);
        ScreenCandidates.Pump(9_100_000, 1);
        Check(reports.Count == 0, "CB6 한 프레임 검색량을 넘으면 다음 프레임으로 넘긴다");
        for (int i = 0; i < ScreenCandidates.RingSize; i++) ScreenCandidates.Transition("Root/Later", 9_200_000 + i, true, Untagged);
        ScreenCandidates.Pump(9_200_000 + ScreenCandidates.RingSize + 1, 1);
        Check(reports.Count == 1 && reports[0].Contains("overrun") && reports[0].Contains("Root/Keep on1/off0"),
              "CB6 분석 중 덮어써져도 이미 센 경로 문자열은 유지되고 불완전으로 표시한다", reports.FirstOrDefault());

        reports.Clear();
        ScreenCandidates.Init(reports.Add);
        ScreenCandidates.Transition("Root/A", 1_000, true, Untagged);
        ScreenCandidates.Pump(2_000, 5);
        ScreenCandidates.Pump(3_000, 6);
        ScreenCandidates.Gap(GapKind.Late, "pk", 1_000, 4_000);
        ScreenCandidates.Pump(5_000, 6);
        Check(reports.Count == 1 && reports[0].Contains("boundary"), "CB7 판 경계를 넘은 구간을 구분한다", reports.FirstOrDefault());
        ScreenCandidates.Disable();
    }
}
