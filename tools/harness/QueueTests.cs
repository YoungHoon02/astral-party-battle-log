using System;
using System.Collections.Generic;
using AstralPartyBattleLog.Net;

// 게임 처리 대기열 추적기 검증.
static class QueueTests
{
    static int _fail;
    static int _unity;

    static void Check(bool ok, string label, string? detail = null)
    {
        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {label}{(detail is null ? "" : $": {detail}")}");
        if (!ok) _fail++;
    }

    static QueueObservation H(int cmd, long down, int size = 1) => new(QueueRead.Head, cmd, down, size);
    static QueueObservation E => new(QueueRead.Empty);

    static void Start()
    {
        QueueTracker.ResetForTest();
        QueueTracker.Configure(true);
        _unity = 100;
        Tick();
    }

    static void Tick() => QueueTracker.BeginFrame(_unity += 7);

    static void TickTo(int target, int current)
    {
        for (int t = current; t < target; t++) Tick();
    }

    static void Empties(int n)
    {
        for (int i = 0; i < n; i++)
        {
            Tick();
            QueueTracker.Apply(E);
        }
    }

    static void MakeLive(int cmd = 1000, long down = 1)
    {
        QueueTracker.Arrive(cmd, down);
        QueueTracker.Apply(H(cmd, down));
    }

    static string Show(QueueSnapshot s) => $"mode={s.Mode} started={s.StartedThrough} done={s.DoneThrough}";

    public static int Run()
    {
        _fail = 0;
        Console.WriteLine();
        Console.WriteLine("=== 대기열 추적 ===");

        Start();
        QueueTracker.Arrive(1015, 100); QueueTracker.Arrive(1040, 99); QueueTracker.Arrive(5028, 105);
        QueueTracker.Apply(H(1040, 99));
        var s = QueueTracker.Snapshot();
        Check(s.Mode == QueueMode.Live && s.StartedThrough == 2 && s.DoneThrough == 1, "QT1 맨 앞과 맞는 항목 앞은 끝, 그 항목은 시작", Show(s));

        Start();
        QueueTracker.Arrive(5040, 278); QueueTracker.Arrive(1007, 273); QueueTracker.Arrive(1040, 274);
        QueueTracker.Apply(H(1007, 273));
        s = QueueTracker.Snapshot();
        Check(s.StartedThrough == 2 && s.DoneThrough == 1, "QT2 DOWNSN이 감소해도 쌍으로 맞춘다", Show(s));

        Start();
        TickTo(10, 1);
        QueueTracker.Arrive(1040, 1);
        bool five = true;
        for (int t = 11; t <= 15; t++)
        {
            Tick();
            QueueTracker.Apply(E);
            five &= QueueTracker.Snapshot().DoneThrough == 0;
        }
        Tick();
        QueueTracker.Apply(E);
        Check(five && QueueTracker.Snapshot().DoneThrough == 1 && QueueTracker.Snapshot().Mode == QueueMode.Live,
              "QT3 빈 목록 6틱 연속이면 도착 3틱 이상 지난 항목이 끝난다");

        Start();
        QueueTracker.Arrive(1040, 1);
        Empties(3);
        Tick(); QueueTracker.Apply(H(9999, 1));
        Empties(5);
        bool broke = QueueTracker.Snapshot().DoneThrough == 0;
        Empties(1);
        Check(broke && QueueTracker.Snapshot().DoneThrough == 1, "QT3b 중간에 H가 끼면 연속 수가 0으로 돌아간다");

        Start();
        QueueTracker.Arrive(1040, 5); QueueTracker.Arrive(1040, 5);
        QueueTracker.Apply(H(1040, 5));
        s = QueueTracker.Snapshot();
        Check(s.StartedThrough == 1 && s.DoneThrough == 0, "QT4 같은 쌍이 둘이면 앞 것과 맞춘다", Show(s));

        Start();
        MakeLive();
        QueueTracker.Arrive(1040, 2);
        QueueTracker.Apply(H(9999, 1));
        var h = QueueTracker.Health;
        bool froze = QueueTracker.Snapshot().Mode == QueueMode.Frozen && h.Unmatched == 1 && h.FreezeUnmatched == 1;
        QueueTracker.Apply(H(1040, 2));
        Check(froze && QueueTracker.Snapshot().Mode == QueueMode.Live, "QT5 어긋나면 멈추고 대기 항목과 맞으면 되돌아온다");

        Start();
        QueueTracker.Arrive(1040, 1); QueueTracker.Arrive(1040, 2); QueueTracker.Arrive(5028, 3);
        QueueTracker.Apply(H(1040, 1));
        QueueTracker.Apply(H(1040, 2));
        bool imm = QueueTracker.Arrive(5008, 3) == -1;
        var s0 = QueueTracker.Snapshot();
        bool r1 = imm && s0.Mode == QueueMode.Frozen && s0.StartedThrough == 2 && s0.DoneThrough == 1;
        QueueTracker.Apply(H(5028, 3));
        var s1 = QueueTracker.Snapshot();
        bool r2 = s1.Mode == QueueMode.Frozen && s1.StartedThrough == 2 && s1.DoneThrough == 1;
        QueueTracker.Arrive(1015, 4);
        QueueTracker.Apply(H(1015, 4));
        var s2 = QueueTracker.Snapshot();
        bool r3 = s2.Mode == QueueMode.Live && s2.StartedThrough == 4 && s2.DoneThrough == 3;
        Check(r1 && r2 && r3 && QueueTracker.Health.FreezeReconnect == 1, "QT6 5008 뒤에는 기준점 뒤 항목과 맞을 때까지 경계가 멈춘다", $"{r1}/{r2}/{r3}");

        Start();
        QueueTracker.Arrive(1040, 1); QueueTracker.Arrive(1040, 2); QueueTracker.Arrive(5028, 3);
        QueueTracker.Apply(H(1040, 1));
        QueueTracker.SocketDropped();
        bool k1 = QueueTracker.Snapshot().Mode == QueueMode.Frozen;
        QueueTracker.Apply(H(1040, 2));
        QueueTracker.Arrive(1040, 5);
        QueueTracker.Apply(H(1040, 5));
        bool k2 = QueueTracker.Snapshot().Mode == QueueMode.Frozen;
        QueueTracker.Arrive(5008, 9);
        QueueTracker.Apply(H(1040, 5));
        bool k3 = QueueTracker.Snapshot().Mode == QueueMode.Frozen;
        QueueTracker.Arrive(1015, 6);
        QueueTracker.Apply(H(1015, 6));
        Check(k1 && k2 && k3 && QueueTracker.Snapshot().Mode == QueueMode.Live && QueueTracker.Health.FreezeSocket == 1,
              "QT7 소켓 끊김 뒤에는 5008 뒤에 도착한 항목과 맞아야 풀린다", $"{k1}/{k2}/{k3}");

        Start();
        QueueTracker.Arrive(5022, 1); QueueTracker.Arrive(1096, 2); QueueTracker.Arrive(1002, 3); QueueTracker.Arrive(5028, 4);
        QueueTracker.Arrive(5044, 5); QueueTracker.Arrive(1026, 6);
        QueueTracker.Arrive(1096, 7); QueueTracker.Arrive(1007, 8);
        QueueTracker.Arrive(5022, 9); QueueTracker.Arrive(5022, 10);
        QueueTracker.Apply(E);
        var links = new List<(long Opener, long End)>();
        int n1 = QueueTracker.DrainLinks(links);
        bool l1 = n1 == 5 && links.Count == 5 && links[0] == (1, 4) && links[1] == (2, 4) && links[2] == (5, 5) && links[3] == (7, 7) && links[4] == (9, 9);
        links.Clear();
        bool l2 = QueueTracker.DrainLinks(links) == 0;
        QueueTracker.Arrive(5028, 11);
        QueueTracker.Apply(E);
        QueueTracker.DrainLinks(links);
        bool l3 = links.Count == 1 && links[0] == (10, 11);
        Check(l1 && l2 && l3, "QT8 끝 기준 표대로 열린 메시지를 닫고 새로 생긴 것만 돌려준다", $"{l1}/{l2}/{l3}");

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
        bool a2 = QueueTracker.Snapshot().Mode == QueueMode.Off;
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
        Empties(6);
        s = QueueTracker.Snapshot();
        Check(s.DoneThrough == 30 && s.StartedThrough == 30, "QT12 판을 나가 목록이 6틱 연속 비면 남은 항목이 모두 끝난다", Show(s));

        Start();
        MakeLive();
        QueueTracker.Arrive(1040, 2);
        QueueTracker.Apply(QueueObservation.Unreadable);
        var su = QueueTracker.Snapshot();
        bool u1 = su.Mode == QueueMode.Frozen && su.StartedThrough == 1 && su.DoneThrough == 0
                  && QueueTracker.Health.Unreadable == 1 && QueueTracker.Health.FreezeUnreadable == 1;
        QueueTracker.Apply(H(1040, 2));
        su = QueueTracker.Snapshot();
        Check(u1 && su.Mode == QueueMode.Live && su.StartedThrough == 2, "QT13 못 읽으면 경계를 멈추고 맞는 맨 앞이 오면 되돌아온다", Show(su));

        Start();
        QueueTracker.Arrive(1040, 1); QueueTracker.Arrive(1040, 2); QueueTracker.Arrive(1040, 3);
        QueueTracker.Apply(H(1040, 1));
        QueueTracker.Arrive(5008, 0);
        QueueTracker.Apply(H(1040, 2));
        QueueTracker.Apply(H(1040, 3));
        var sf = QueueTracker.Snapshot();
        bool f1 = sf.Mode == QueueMode.Frozen && sf.StartedThrough == 1 && sf.DoneThrough == 0;
        QueueTracker.Arrive(1040, 9);
        QueueTracker.Apply(H(1040, 9));
        sf = QueueTracker.Snapshot();
        Check(f1 && sf.Mode == QueueMode.Live && sf.StartedThrough == 4 && sf.DoneThrough == 3, "QT14 멈춘 동안에도 안의 경계는 나아가고 되돌아오면 현재 값을 낸다", Show(sf));

        Start();
        MakeLive();
        bool e0 = QueueTracker.Arrive(5012, 0) == -1;
        bool e1 = e0 && QueueTracker.Snapshot().Mode == QueueMode.Frozen && QueueTracker.Health.FreezeExit == 1;
        Start();
        MakeLive();
        long errNum = QueueTracker.Arrive(1040, 7, 3);
        bool e2 = errNum == 2 && QueueTracker.Snapshot().Mode == QueueMode.Frozen && QueueTracker.Health.FreezeError == 1;
        QueueTracker.Apply(H(1040, 7));
        bool e3 = QueueTracker.Snapshot().Mode == QueueMode.Frozen;
        QueueTracker.Arrive(1040, 8);
        QueueTracker.Apply(H(1040, 8));
        Check(e1 && e2 && e3 && QueueTracker.Snapshot().Mode == QueueMode.Live, "QT15 퇴장과 오류 프레임은 멈추고 오류 프레임 번호가 기준점이다", $"{e1}/{e2}/{e3}");

        Start();
        MakeLive();
        QueueTracker.Apply(H(9999, 1));
        Empties(5);
        bool q1 = QueueTracker.Snapshot().Mode == QueueMode.Frozen;
        Empties(1);
        bool q2 = QueueTracker.Snapshot().Mode == QueueMode.Live;
        QueueTracker.Apply(QueueObservation.Unreadable);
        Empties(6);
        bool q3 = QueueTracker.Snapshot().Mode == QueueMode.Live;
        QueueTracker.Arrive(5008, 0);
        Empties(12);
        bool q4 = QueueTracker.Snapshot().Mode == QueueMode.Frozen;
        Check(q1 && q2 && q3 && q4, "QT16 어긋남·못 읽음은 빈 목록 6틱으로 풀리고 재연결 신호는 풀리지 않는다", $"{q1}/{q2}/{q3}/{q4}");

        Start();
        Tick();
        QueueTracker.Arrive(1040, 1); QueueTracker.Arrive(1040, 2);
        TickTo(7, 2);
        QueueTracker.Apply(H(1040, 1, 4));
        Tick();
        QueueTracker.Apply(H(1040, 1, 4));
        var hh = QueueTracker.Health;
        bool w1 = hh.HeadWaitTicks == 5 && hh.Size == 4 && hh.Matched == 2;
        Tick();
        QueueTracker.Apply(E);
        bool w2 = QueueTracker.Health.HeadWaitTicks == 0 && QueueTracker.Health.Size == 0;
        Tick();
        QueueTracker.Apply(H(9999, 1));
        hh = QueueTracker.Health;
        Check(w1 && w2 && hh.Frames == 4 && hh.LiveFrames == 3 && hh.FrozenFrames == 1, "QT17 건강 값: 틱 수, 맨 앞 대기 틱, 크기", $"{w1}/{w2} {hh.Frames}/{hh.LiveFrames}/{hh.FrozenFrames}");

        Start();
        MakeLive();
        QueueTracker.Apply(H(9999, 1));
        QueueTracker.Arrive(5008, 0);
        Check(QueueTracker.Health.FreezeUnmatched == 1 && QueueTracker.Health.FreezeReconnect == 1, "QT18 이미 멈춘 상태에서 온 신호도 센다");

        QueueTracker.ResetForTest();
        for (int i = 0; i < 20000; i++) QueueTracker.NoteGroup(1, i, 1040);
        bool o1 = QueueTracker.GroupOrderLength == 1;
        for (int g = 2; g <= 8192; g++) QueueTracker.NoteGroup(g, g, 1040);
        bool o2 = QueueTracker.TryRefOf(1, out _, out _);
        QueueTracker.NoteGroup(8193, 8193, 1040);
        bool o3 = !QueueTracker.TryRefOf(1, out _, out _) && !QueueTracker.TryRefOf(4096, out _, out _)
                  && QueueTracker.TryRefOf(4097, out _, out _) && QueueTracker.TryRefOf(8193, out _, out _);
        Check(o1 && o2 && o3 && QueueTracker.GroupOrderLength == 4097, "QT19 같은 그룹을 되풀이해 적어도 순서 큐가 늘지 않고 상한에서 오래된 절반이 지워진다", $"{o1}/{o2}/{o3}");

        Start();
        MakeLive();
        QueueTracker.Disable("x");
        Check(QueueTracker.Snapshot().Mode == QueueMode.Off && !QueueTracker.Enabled, "QT20 맞음에서 끄면 꺼짐이 된다");

        Start();
        QueueTracker.Arrive(1040, 1); QueueTracker.Arrive(1040, 2);
        QueueTracker.Arrive(5008, 0);
        QueueTracker.Apply(H(1040, 1));
        bool z1 = QueueTracker.Snapshot().Mode == QueueMode.Off;
        QueueTracker.Arrive(1040, 3);
        QueueTracker.Apply(H(1040, 3));
        Check(z1 && QueueTracker.Snapshot().Mode == QueueMode.Live, "QT21 꺼짐에서 받은 재연결 신호도 기준점이 되어 첫 맞음은 그 뒤 항목이어야 한다");

        Start();
        QueueTracker.Arrive(1040, 1);
        QueueTracker.Configure(false);
        QueueTracker.Configure(true);
        QueueTracker.Apply(H(1040, 1));
        Check(QueueTracker.Snapshot().Mode == QueueMode.Off && QueueTracker.Health.Unmatched == 1, "QT22 껐다 켜면 도착 대기분과 대기 목록이 비워진다");

        QueueTracker.ResetForTest();
        Console.WriteLine(_fail == 0 ? "=== 대기열 추적 전부 통과 ===" : $"=== 대기열 추적 실패 {_fail}건 ===");
        return _fail;
    }
}
