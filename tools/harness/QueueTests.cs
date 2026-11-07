using System;
using System.Collections.Generic;
using AstralPartyBattleLog.Log;
using AstralPartyBattleLog.Net;
using BepInEx.Logging;

// 게임 처리 대기열 추적기 검증.
static class QueueTests
{
    static int _fail;
    static int _unity;

    // BepInEx 6의 보간 문자열 LogInfo는 등록된 리스너가 그 수준을 듣지 않으면 문자열을 만들지도 않는다.
    sealed class LevelListener : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.All;
        public void LogEvent(object sender, LogEventArgs eventArgs) { }
        public void Dispose() { }
    }

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
        A(cmd, down);
        QueueTracker.Apply(H(cmd, down));
    }

    static long A(int cmd, long down, int err = 0) => QueueTracker.Arrive(cmd, down, err);

    static string Show(QueueSnapshot s) => $"mode={s.Mode} started={s.StartedThrough} done={s.DoneThrough}";

    static BattleLogger NewLogger(ManualLogSource log) =>
        new(log, null, false, new HashSet<int>(), false, true, NameTable.Load("", _ => { }));

    public static int Run()
    {
        _fail = 0;
        Console.WriteLine();
        Console.WriteLine("=== 대기열 추적 ===");

        Start();
        A(1015, 100); A(1040, 99); A(5028, 105);
        QueueTracker.Apply(H(1040, 99));
        var s = QueueTracker.Snapshot();
        Check(s.Mode == QueueMode.Live && s.StartedThrough == 2 && s.DoneThrough == 1, "QT1 맨 앞과 맞는 항목 앞은 끝, 그 항목은 시작", Show(s));

        Start();
        A(5040, 278); A(1007, 273); A(1040, 274);
        QueueTracker.Apply(H(1007, 273));
        s = QueueTracker.Snapshot();
        Check(s.StartedThrough == 2 && s.DoneThrough == 1, "QT2 DOWNSN이 감소해도 쌍으로 맞춘다", Show(s));

        Start();
        TickTo(10, 1);
        A(1040, 1);
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
        A(1040, 1);
        Empties(3);
        Tick(); QueueTracker.Apply(H(9999, 1));
        Empties(5);
        bool broke = QueueTracker.Snapshot().DoneThrough == 0;
        Empties(1);
        Check(broke && QueueTracker.Snapshot().DoneThrough == 1, "QT3b 중간에 H가 끼면 연속 수가 0으로 돌아간다");

        Start();
        Empties(6);
        A(1040, 1);
        bool m1 = true;
        for (int i = 0; i < 2; i++)
        {
            Tick();
            QueueTracker.Apply(E);
            m1 &= QueueTracker.Snapshot().DoneThrough == 0;
        }
        Tick();
        QueueTracker.Apply(E);
        Check(m1 && QueueTracker.Snapshot().DoneThrough == 1, "QT3c 도착 여유는 정확히 3틱이다");

        Start();
        A(1040, 1);
        Empties(3);
        Tick(); QueueTracker.Apply(QueueObservation.Unreadable);
        Empties(5);
        bool broke2 = QueueTracker.Snapshot().DoneThrough == 0;
        Empties(1);
        Check(broke2 && QueueTracker.Snapshot().DoneThrough == 1, "QT3d 못 읽은 프레임도 빈 목록 연속 수를 0으로 돌린다");

        Start();
        A(1040, 5); A(1040, 5);
        QueueTracker.Apply(H(1040, 5));
        s = QueueTracker.Snapshot();
        Check(s.StartedThrough == 1 && s.DoneThrough == 0, "QT4 같은 쌍이 둘이면 앞 것과 맞춘다", Show(s));

        Start();
        MakeLive();
        A(1040, 2);
        QueueTracker.Apply(H(9999, 1));
        var h = QueueTracker.Health;
        bool froze = QueueTracker.Snapshot().Mode == QueueMode.Frozen && h.Unmatched == 1 && h.FreezeUnmatched == 1;
        QueueTracker.Apply(H(1040, 2));
        Check(froze && QueueTracker.Snapshot().Mode == QueueMode.Live, "QT5 어긋나면 멈추고 대기 항목과 맞으면 되돌아온다");

        Start();
        A(1040, 1); A(1040, 2); A(5028, 3);
        QueueTracker.Apply(H(1040, 1));
        QueueTracker.Apply(H(1040, 2));
        bool imm = A(5008, 3) == -1;
        var s0 = QueueTracker.Snapshot();
        bool r1 = imm && s0.Mode == QueueMode.Frozen && s0.StartedThrough == 2 && s0.DoneThrough == 1;
        QueueTracker.Apply(H(5028, 3));
        var s1 = QueueTracker.Snapshot();
        bool r2 = s1.Mode == QueueMode.Frozen && s1.StartedThrough == 2 && s1.DoneThrough == 1;
        A(1015, 4);
        QueueTracker.Apply(H(1015, 4));
        var s2 = QueueTracker.Snapshot();
        bool r3 = s2.Mode == QueueMode.Live && s2.StartedThrough == 4 && s2.DoneThrough == 3;
        Check(r1 && r2 && r3 && QueueTracker.Health.FreezeReconnect == 1, "QT6 5008 뒤에는 기준점 뒤 항목과 맞을 때까지 경계가 멈춘다", $"{r1}/{r2}/{r3}");

        Start();
        A(1040, 1); A(1040, 2); A(5028, 3);
        QueueTracker.Apply(H(1040, 1));
        QueueTracker.SocketDropped();
        bool k1 = QueueTracker.Snapshot().Mode == QueueMode.Frozen;
        QueueTracker.Apply(H(1040, 2));
        A(1040, 5);
        QueueTracker.Apply(H(1040, 5));
        bool k2 = QueueTracker.Snapshot().Mode == QueueMode.Frozen;
        A(5008, 9);
        QueueTracker.Apply(H(1040, 5));
        bool k3 = QueueTracker.Snapshot().Mode == QueueMode.Frozen;
        A(1015, 6);
        QueueTracker.Apply(H(1015, 6));
        Check(k1 && k2 && k3 && QueueTracker.Snapshot().Mode == QueueMode.Live && QueueTracker.Health.FreezeSocket == 1,
              "QT7 소켓 끊김 뒤에는 5008 뒤에 도착한 항목과 맞아야 풀린다", $"{k1}/{k2}/{k3}");

        Start();
        A(5022, 1); A(1096, 2); A(1002, 3); A(5028, 4);
        A(5044, 5); A(1026, 6);
        A(1096, 7); A(1007, 8);
        A(5022, 9); A(5022, 10);
        QueueTracker.Apply(E);
        var links = new List<(long Opener, long End)>();
        int n1 = QueueTracker.DrainLinks(links);
        bool l1 = n1 == 5 && links.Count == 5 && links[0] == (1, 4) && links[1] == (2, 4) && links[2] == (5, 5) && links[3] == (7, 7) && links[4] == (9, 9);
        links.Clear();
        bool l2 = QueueTracker.DrainLinks(links) == 0;
        A(5028, 11);
        QueueTracker.Apply(E);
        QueueTracker.DrainLinks(links);
        bool l3 = links.Count == 1 && links[0] == (10, 11);
        Check(l1 && l2 && l3, "QT8 끝 기준 표대로 열린 메시지를 닫고 새로 생긴 것만 돌려준다", $"{l1}/{l2}/{l3}");

        Start();
        int[] immediate = { 5004, 5096, 5238, 5008, 5150, 5246, 5328, 1001, 5012 };
        bool allSkipped = true;
        foreach (int c in immediate) allSkipped &= A(c, 1) == -1;
        QueueTracker.NoteGroup(10, -1, 5150);
        QueueTracker.NoteGroup(11, 3, 1040);
        bool g1 = !QueueTracker.TryRefOf(10, out _, out _);
        bool g2 = QueueTracker.TryRefOf(11, out long r, out int rc) && r == 3 && rc == 1040;
        Check(allSkipped && g1 && g2, "QT9 즉시 처리 9종은 번호가 없고 그룹 표는 번호 없는 항목을 돌려주지 않는다");

        QueueTracker.ResetForTest();
        QueueTracker.Configure(false);
        bool a1 = A(1040, 1) == -1;
        QueueTracker.Apply(E);
        bool a2 = QueueTracker.Snapshot().Mode == QueueMode.Off;
        QueueTracker.Configure(true);
        QueueTracker.Disable("x");
        QueueTracker.Configure(true);
        Check(a1 && a2 && !QueueTracker.Enabled, "QT10 꺼져 있으면 추적하지 않고 한 번 끄면 되살리지 않는다");

        Start();
        for (int i = 1; i <= 5000; i++) A(1040, i);
        QueueTracker.Apply(H(1040, 5000));
        s = QueueTracker.Snapshot();
        Check(QueueTracker.Health.Overflow == 904 && s.DoneThrough == 4999 && s.StartedThrough == 5000, "QT11 대기 목록 상한을 넘으면 오래된 것부터 끝으로 본다",
              $"overflow={QueueTracker.Health.Overflow} {Show(s)}");

        Start();
        for (int i = 1; i <= 30; i++) A(1040, i);
        QueueTracker.Apply(H(1040, 5));
        Empties(6);
        s = QueueTracker.Snapshot();
        Check(s.DoneThrough == 30 && s.StartedThrough == 30, "QT12 판을 나가 목록이 6틱 연속 비면 남은 항목이 모두 끝난다", Show(s));

        Start();
        MakeLive();
        A(1040, 2);
        QueueTracker.Apply(QueueObservation.Unreadable);
        var su = QueueTracker.Snapshot();
        bool u1 = su.Mode == QueueMode.Frozen && su.StartedThrough == 1 && su.DoneThrough == 0
                  && QueueTracker.Health.Unreadable == 1 && QueueTracker.Health.FreezeUnreadable == 1;
        QueueTracker.Apply(H(1040, 2));
        su = QueueTracker.Snapshot();
        Check(u1 && su.Mode == QueueMode.Live && su.StartedThrough == 2, "QT13 못 읽으면 경계를 멈추고 맞는 맨 앞이 오면 되돌아온다", Show(su));

        Start();
        A(1040, 1); A(1040, 2); A(1040, 3);
        QueueTracker.Apply(H(1040, 1));
        A(5008, 0);
        QueueTracker.Apply(H(1040, 2));
        QueueTracker.Apply(H(1040, 3));
        var sf = QueueTracker.Snapshot();
        bool f1 = sf.Mode == QueueMode.Frozen && sf.StartedThrough == 1 && sf.DoneThrough == 0;
        A(1040, 9);
        QueueTracker.Apply(H(1040, 9));
        sf = QueueTracker.Snapshot();
        Check(f1 && sf.Mode == QueueMode.Live && sf.StartedThrough == 4 && sf.DoneThrough == 3, "QT14 멈춘 동안에도 안의 경계는 나아가고 되돌아오면 현재 값을 낸다", Show(sf));

        Start();
        MakeLive();
        bool e0 = A(5012, 0) == -1;
        bool e1 = e0 && QueueTracker.Snapshot().Mode == QueueMode.Frozen && QueueTracker.Health.FreezeExit == 1;
        Start();
        MakeLive();
        long errNum = A(1040, 7, 3);
        bool e2 = errNum == 2 && QueueTracker.Snapshot().Mode == QueueMode.Frozen && QueueTracker.Health.FreezeError == 1;
        QueueTracker.Apply(H(1040, 7));
        bool e3 = QueueTracker.Snapshot().Mode == QueueMode.Frozen;
        A(1040, 8);
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
        A(5008, 0);
        Empties(12);
        bool q4 = QueueTracker.Snapshot().Mode == QueueMode.Frozen;
        Check(q1 && q2 && q3 && q4, "QT16 어긋남·못 읽음은 빈 목록 6틱으로 풀리고 재연결 신호는 풀리지 않는다", $"{q1}/{q2}/{q3}/{q4}");

        Start();
        Tick();
        A(1040, 1); A(1040, 2);
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
        Tick();
        A(1040, 1);
        TickTo(7, 2);
        QueueTracker.Apply(H(1040, 1));
        Tick(); QueueTracker.Apply(QueueObservation.Unreadable);
        bool y1 = QueueTracker.Health.HeadWaitTicks == 0;
        Tick(); QueueTracker.Apply(H(1040, 1));
        Tick(); QueueTracker.Apply(H(9999, 1));
        Tick(); QueueTracker.Apply(H(1040, 1));
        Check(y1 && QueueTracker.Health.HeadWaitTicks == 5, "QT17b 맨 앞이 한 프레임 안 읽혀도 같은 항목이면 처음 맞은 틱을 유지한다");

        Start();
        MakeLive();
        QueueTracker.Apply(H(9999, 1));
        A(5008, 0);
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
        A(1040, 1); A(1040, 2);
        A(5008, 0);
        QueueTracker.Apply(H(1040, 1));
        bool z1 = QueueTracker.Snapshot().Mode == QueueMode.Off;
        A(1040, 3);
        QueueTracker.Apply(H(1040, 3));
        Check(z1 && QueueTracker.Snapshot().Mode == QueueMode.Live, "QT21 꺼짐에서 받은 재연결 신호도 기준점이 되어 첫 맞음은 그 뒤 항목이어야 한다");

        Start();
        A(1040, 1);
        QueueTracker.Apply(E);
        QueueTracker.Configure(false);
        QueueTracker.Configure(true);
        QueueTracker.Apply(H(1040, 1));
        Check(QueueTracker.Snapshot().Mode == QueueMode.Off && QueueTracker.Health.Unmatched == 1, "QT22 껐다 켜면 도착 대기분과 대기 목록이 비워진다");

        Start();
        MakeLive();
        QueueTracker.SocketDropped();
        QueueTracker.Configure(false);
        QueueTracker.Configure(true);
        A(1040, 2);
        QueueTracker.Apply(H(1040, 2));
        bool c1 = QueueTracker.Snapshot().Mode == QueueMode.Off;
        A(5008, 0);
        QueueTracker.Apply(H(1040, 2));
        bool c2 = QueueTracker.Snapshot().Mode == QueueMode.Off;
        A(1040, 3);
        QueueTracker.Apply(H(1040, 3));
        Check(c1 && c2 && QueueTracker.Snapshot().Mode == QueueMode.Live, "QT23 소켓이 끊긴 채 껐다 켜도 5008과 그 뒤 항목 없이는 켜지지 않는다", $"{c1}/{c2}");

        Start();
        var logger = NewLogger(new ManualLogSource("queue"));
        logger.OnFrame(new FrameHeader(Op.MatchSuccess, 0, 0, 21), Array.Empty<byte>());
        logger.OnFrame(new FrameHeader(5028, 0, 0, 22), Array.Empty<byte>());
        logger.OnFrame(new FrameHeader(5004, 0, 0, 23), Array.Empty<byte>());
        logger.OnFrame(new FrameHeader(Op.UpdateHeroAttr, 0, 0, 24), Array.Empty<byte>());
        bool lq1 = QueueTracker.TryRefOf(1, out long lr1, out int lc1) && lr1 == 1 && lc1 == Op.MatchSuccess;
        bool lq2 = QueueTracker.TryRefOf(2, out long lr2, out int lc2) && lr2 == 2 && lc2 == 5028;
        bool lq3 = !QueueTracker.TryRefOf(3, out _, out _);
        bool lq4 = QueueTracker.TryRefOf(4, out long lr4, out int lc4) && lr4 == 3 && lc4 == Op.UpdateHeroAttr;
        QueueTracker.Apply(H(5028, 22));
        var wsnap = QueueTracker.Snapshot();
        Check(lq1 && lq2 && lq3 && lq4 && wsnap.Mode == QueueMode.Live && wsnap.StartedThrough == 2 && wsnap.DoneThrough == 1,
              "QT24 로거는 본문 전에 돌아가는 프레임도 도착 번호를 받아 그룹에 남긴다", $"{lq1}/{lq2}/{lq3}/{lq4} {Show(wsnap)}");

        // 분석 도구(probe_queue.py)가 이 줄 모양에 기댄다.
        Start();
        var traceLog = new ManualLogSource("queue-trace");
        var traced = new List<string>();
        traceLog.LogEvent += (_, e) => traced.Add(e.Data?.ToString() ?? "");
        var levels = new LevelListener();
        BepInEx.Logging.Logger.Listeners.Add(levels);
        TimingTrace.Init(traceLog, true);
        var tracer = NewLogger(traceLog);
        tracer.OnFrame(new FrameHeader(5004, 0, 0, 31), Array.Empty<byte>());
        tracer.OnFrame(new FrameHeader(5028, 0, 0, 32), Array.Empty<byte>());
        tracer.OnFrame(new FrameHeader(Op.UpdateHeroAttr, 0, 0, 33), Array.Empty<byte>());
        TimingTrace.Init(traceLog, false);
        BepInEx.Logging.Logger.Listeners.Remove(levels);
        var shape = new List<string>();
        bool tracePinned = true;
        foreach (string line in traced)
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"frame cmd=(\d+) .* seq=(\d+|-) grp=(\d+)$");
            if (!m.Success) continue;
            int tc = int.Parse(m.Groups[1].Value);
            string ts = m.Groups[2].Value;
            long tg = long.Parse(m.Groups[3].Value);
            shape.Add($"{tc}/{ts}/{tg}");
            bool known = QueueTracker.TryRefOf(tg, out long tr, out int trc);
            tracePinned &= ts == "-" ? !known : known && tr == long.Parse(ts) && trc == tc;
        }
        tracePinned &= string.Join(" ", shape) == "5004/-/1 5028/1/2 1040/2/3";
        Check(tracePinned, "QT25 frame 기록 줄은 도착 번호(seq)와 그룹(grp)을 붙이고 즉시 처리는 seq=-로 쓴다", string.Join(" ", shape));

        Start();
        Empties(6);
        QueueTracker.BeginRead();
        long rn = A(1040, 1);
        Tick();
        QueueTracker.Apply(E);
        Tick();
        QueueTracker.Apply(E);
        bool rdA = QueueTracker.Snapshot().DoneThrough == 0;
        QueueTracker.EndRead();
        Empties(2);
        bool rdB = QueueTracker.Snapshot().DoneThrough == 0;
        Empties(1);
        bool rdC = QueueTracker.Snapshot().DoneThrough == 1;
        Check(rn == 1 && rdA && rdB && rdC, "QT26 한 번의 읽기를 다 넘기기 전에 빈 목록 틱이 지나도 도착은 읽기가 끝난 뒤부터 센다", $"{rn}/{rdA}/{rdB}/{rdC}");

        Start();
        long nowUs = 1_000_000;
        QueueTracker.ClockUs = () => nowUs;
        const long tickUs = 4_167;
        void Step() { nowUs += tickUs; Tick(); QueueTracker.Apply(E); }
        for (int i = 0; i < 6; i++) Step();
        A(1040, 1);
        for (int i = 0; i < 3; i++) Step();
        bool wcA = QueueTracker.Snapshot().DoneThrough == 0;
        while (nowUs - 1_000_000 - 6 * tickUs < QueueTracker.ArrivalMarginUs - tickUs) Step();
        bool wcB = QueueTracker.Snapshot().DoneThrough == 0;
        Step();
        bool wcC = QueueTracker.Snapshot().DoneThrough == 1;
        Check(wcA && wcB && wcC, "QT27 빈 목록 규칙은 3틱 뒤에도 확정 뒤 100ms가 지나야 풀어 준다", $"{wcA}/{wcB}/{wcC}");

        Start();
        QueueTracker.BeginRead();
        A(1040, 1);
        QueueTracker.Configure(false);
        QueueTracker.EndRead();
        QueueTracker.Configure(true);
        Empties(10);
        bool dd = QueueTracker.Snapshot().DoneThrough == 0;
        QueueTracker.Apply(H(1040, 1));
        Check(dd && QueueTracker.Health.Unmatched == 1, "QT28 읽는 중에 꺼지면 읽기를 끝내도 대기열에 남는 것이 없다", $"{dd}/{QueueTracker.Health.Unmatched}");

        QueueTracker.ResetForTest();
        Console.WriteLine(_fail == 0 ? "=== 대기열 추적 전부 통과 ===" : $"=== 대기열 추적 실패 {_fail}건 ===");
        return _fail;
    }
}
