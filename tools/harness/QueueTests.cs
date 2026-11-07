using System;
using System.Collections.Generic;
using AstralPartyBattleLog.Net;

// 게임 처리 대기열 추적기 검증.
static class QueueTests
{
    static int _fail;

    static void Check(bool ok, string label, string? detail = null)
    {
        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {label}{(detail is null ? "" : $": {detail}")}");
        if (!ok) _fail++;
    }

    static QueueObservation H(int cmd, long down) => new(QueueRead.Head, cmd, down, 1);
    static QueueObservation E => new(QueueRead.Empty);

    static void Start(int frame = 1)
    {
        QueueTracker.ResetForTest();
        QueueTracker.Configure(true);
        QueueTracker.BeginFrame(frame);
    }

    static void MakeLive()
    {
        QueueTracker.Apply(E);
    }

    static string Show(QueueSnapshot s) => $"live={s.Live} started={s.StartedThrough} done={s.DoneThrough}";

    public static int Run()
    {
        _fail = 0;
        Console.WriteLine();
        Console.WriteLine("=== 대기열 추적 ===");

        Start();
        QueueTracker.Arrive(1015, 100); QueueTracker.Arrive(1040, 99); QueueTracker.Arrive(5028, 105);
        QueueTracker.Apply(H(1040, 99));
        var s = QueueTracker.Snapshot();
        Check(s.Live && s.StartedThrough == 2 && s.DoneThrough == 1, "QT1 맨 앞과 맞는 항목 앞은 끝, 그 항목은 시작", Show(s));

        Start();
        QueueTracker.Arrive(5040, 278); QueueTracker.Arrive(1007, 273); QueueTracker.Arrive(1040, 274);
        QueueTracker.Apply(H(1007, 273));
        s = QueueTracker.Snapshot();
        Check(s.StartedThrough == 2 && s.DoneThrough == 1, "QT2 DOWNSN이 감소해도 쌍으로 맞춘다", Show(s));

        Start(10);
        QueueTracker.Arrive(1040, 1);
        QueueTracker.BeginFrame(12); QueueTracker.Apply(E);
        bool early = QueueTracker.Snapshot().DoneThrough == 0;
        QueueTracker.BeginFrame(13); QueueTracker.Apply(E);
        Check(early && QueueTracker.Snapshot().DoneThrough == 1, "QT3 빈 목록은 도착 3프레임 뒤부터 끝으로 본다");

        Start();
        QueueTracker.Arrive(1040, 5); QueueTracker.Arrive(1040, 5);
        QueueTracker.Apply(H(1040, 5));
        s = QueueTracker.Snapshot();
        Check(s.StartedThrough == 1 && s.DoneThrough == 0, "QT4 같은 쌍이 둘이면 앞 것과 맞춘다", Show(s));

        Start();
        MakeLive();
        QueueTracker.Arrive(1040, 1);
        QueueTracker.Apply(H(9999, 1));
        var h = QueueTracker.Health;
        bool off = !QueueTracker.Snapshot().Live && h.Unmatched == 1 && h.SuspendUnmatched == 1;
        QueueTracker.Apply(H(1040, 1));
        Check(off && QueueTracker.Snapshot().Live, "QT5 어긋나면 멈추고 대기 항목과 맞으면 되돌아온다");

        ReconnectCase("QT6 방 동기화", () => Check(QueueTracker.Arrive(5008, 3) == -1, "QT6 5008은 즉시 처리"), h2 => h2.SuspendReconnect);
        ReconnectCase("QT7 소켓 끊김", QueueTracker.SocketDropped, h2 => h2.SuspendSocket);

        Start();
        QueueTracker.Arrive(5022, 1); QueueTracker.Arrive(1019, 2); QueueTracker.Arrive(1002, 3); QueueTracker.Arrive(5028, 4);
        QueueTracker.Apply(E);
        var links = new List<(long Dice, long End)>();
        QueueTracker.DrainLinks(links);
        bool l1 = links.Count == 1 && links[0] == (1, 4);
        QueueTracker.Arrive(5022, 5); QueueTracker.Arrive(1026, 6);
        QueueTracker.Apply(E);
        links.Clear();
        QueueTracker.DrainLinks(links);
        bool l2 = links.Count == 1 && links[0] == (5, 5);
        QueueTracker.Arrive(5022, 7);
        QueueTracker.Apply(E);
        bool l3 = QueueTracker.Snapshot().OpenDice == 7;
        QueueTracker.Arrive(5022, 8);
        Check(l1 && l2 && l3 && QueueTracker.Snapshot().LastDrained == 7, "QT8 주사위 끝 기준은 뒤따르는 걷기나 경계 메시지", $"{l1}/{l2}/{l3}");

        Start();
        int[] immediate = { 5004, 5096, 5238, 5008, 5150, 5246, 5328, 1001, 5012 };
        bool allSkipped = true;
        foreach (int c in immediate) allSkipped &= QueueTracker.Arrive(c, 1) == -1;
        QueueTracker.NoteGroup(10, -1, 5150);
        QueueTracker.NoteGroup(11, 3, 1040);
        bool g1 = !QueueTracker.TryRefOf(10, out _, out _);
        bool g2 = QueueTracker.TryRefOf(11, out long r, out int rc) && r == 3 && rc == 1040;
        Check(allSkipped && g1 && g2, "QT9 즉시 처리 9종은 번호가 없고 그룹 표는 번호 없는 항목을 돌려주지 않는다");

        QueueTracker.ResetForTest();
        QueueTracker.Configure(false);
        bool a1 = QueueTracker.Arrive(1040, 1) == -1;
        QueueTracker.Apply(E);
        bool a2 = !QueueTracker.Snapshot().Live;
        QueueTracker.Configure(true);
        QueueTracker.Disable("x");
        QueueTracker.Configure(true);
        Check(a1 && a2 && !QueueTracker.Enabled, "QT10 꺼져 있으면 추적하지 않고 한 번 끄면 되살리지 않는다");

        Start();
        for (int i = 1; i <= 5000; i++) QueueTracker.Arrive(1040, i);
        QueueTracker.Apply(H(1040, 5000));
        s = QueueTracker.Snapshot();
        Check(QueueTracker.Health.Overflow == 904 && s.DoneThrough == 4999 && s.StartedThrough == 5000, "QT11 대기 목록 상한을 넘으면 오래된 것부터 끝으로 본다",
              $"overflow={QueueTracker.Health.Overflow} {Show(s)}");

        Start();
        for (int i = 1; i <= 30; i++) QueueTracker.Arrive(1040, i);
        QueueTracker.Apply(H(1040, 5));
        QueueTracker.BeginFrame(4); QueueTracker.Apply(E);
        s = QueueTracker.Snapshot();
        Check(s.DoneThrough == 30 && s.StartedThrough == 30, "QT12 판을 나가 목록이 비면 남은 항목이 모두 끝난다", Show(s));

        Start();
        MakeLive();
        QueueTracker.Arrive(1040, 1);
        QueueTracker.Apply(QueueObservation.Unreadable);
        bool u1 = !QueueTracker.Snapshot().Live && QueueTracker.Health.Unreadable == 1;
        QueueTracker.Apply(H(1040, 1));
        Check(u1 && QueueTracker.Snapshot().Live, "QT13 못 읽으면 멈추고 재연결 신호가 없으면 앞 항목과 맞아도 되돌아온다");

        QueueTracker.ResetForTest();
        Console.WriteLine(_fail == 0 ? "=== 대기열 추적 전부 통과 ===" : $"=== 대기열 추적 실패 {_fail}건 ===");
        return _fail;
    }

    static void ReconnectCase(string label, Action trigger, Func<QueueHealth, long> counter)
    {
        Start();
        MakeLive();
        QueueTracker.Arrive(1040, 1); QueueTracker.Arrive(5028, 2);
        trigger();
        bool t1 = !QueueTracker.Snapshot().Live;
        QueueTracker.Apply(H(5028, 2));
        bool t2 = !QueueTracker.Snapshot().Live;
        QueueTracker.Arrive(1015, 4);
        QueueTracker.BeginFrame(5);
        QueueTracker.Apply(E);
        bool t3 = !QueueTracker.Snapshot().Live;
        QueueTracker.Arrive(1040, 5);
        QueueTracker.Apply(H(1040, 5));
        Check(t1 && t2 && t3 && QueueTracker.Snapshot().Live && counter(QueueTracker.Health) == 1,
              $"{label}: 신호 뒤에 도착한 항목과 맞을 때까지 멈춘다", $"{t1}/{t2}/{t3}");
    }
}
