using System;
using System.Collections.Generic;
using System.IO;
using AstralPartyBattleLog.Log;
using AstralPartyBattleLog.Net;
using BepInEx.Logging;

// 합성 프레임으로 디코더 경로를 확인한다. 게임 없이 도는 검증용이다.
static class Frame
{
    public static byte[] Tag(int field, int wire)
    {
        var o = new List<byte>();
        ulong t = ((ulong)field << 3) | (uint)wire;
        while (true) { byte b = (byte)(t & 0x7F); t >>= 7; if (t != 0) b |= 0x80; o.Add(b); if (t == 0) break; }
        return o.ToArray();
    }
    public static byte[] Fix32(int f, int v) => Cat(Tag(f, 5), BitConverter.GetBytes(v));
    public static byte[] Fix64(int f, long v) => Cat(Tag(f, 1), BitConverter.GetBytes(v));
    public static byte[] Varint(int f, ulong v)
    {
        var o = new List<byte>(Tag(f, 0));
        while (true) { byte b = (byte)(v & 0x7F); v >>= 7; if (v != 0) b |= 0x80; o.Add(b); if (v == 0) break; }
        return o.ToArray();
    }
    public static byte[] Msg(int f, byte[] body)
    {
        var o = new List<byte>(Tag(f, 2));
        ulong n = (ulong)body.Length;
        while (true) { byte b = (byte)(n & 0x7F); n >>= 7; if (n != 0) b |= 0x80; o.Add(b); if (n == 0) break; }
        o.AddRange(body); return o.ToArray();
    }
    public static byte[] Cat(params byte[][] parts)
    {
        var o = new List<byte>(); foreach (var p in parts) o.AddRange(p); return o.ToArray();
    }
}

class Program
{
    const long SkillId = 777;

    static byte[] BuffMsg(long uid, int buffId, long srcKind, long srcId) =>
        Frame.Cat(Frame.Fix64(1, uid), Frame.Fix32(2, buffId),
                  Frame.Msg(50, Frame.Cat(Frame.Varint(1, (ulong)srcKind), Frame.Fix64(2, srcId))));

    // HeroAttrEffect { 1:playerId, 6:HeroBuffChangeS2C{1:pid,2:Buff,3:op} }
    static byte[] SkillEffect(long target, long uid) =>
        Frame.Msg(4, Frame.Cat(
            Frame.Fix64(1, target),
            Frame.Msg(6, Frame.Cat(
                Frame.Fix64(1, target),
                Frame.Msg(2, BuffMsg(uid, 5, 1, SkillId)),
                Frame.Varint(3, 1)))));   // Oper.Insert

    static byte[] HpEffect(long target, int ori, int curr, int change, int real, int realHp, int max) =>
        Frame.Msg(4, Frame.Cat(
            Frame.Fix64(1, target),
            Frame.Msg(3, Frame.Cat(
                Frame.Fix64(1, target), Frame.Fix32(2, change), Frame.Fix32(3, ori),
                Frame.Fix32(4, curr), Frame.Fix32(5, real), Frame.Fix32(6, realHp),
                Frame.Fix32(7, max)))));

    static byte[] Cause(long source, long id) =>
        Frame.Msg(2, Frame.Cat(Frame.Varint(1, (ulong)source), Frame.Fix64(3, id)));

    static void Main(string[] args)
    {
        // 재생 기록 검증용 자식 프로세스 모드(sched / scenario / replay). 사람이 직접 재생할 때도 쓴다:
        //   harness replay <기록.jsonl> [--lenient]
        //   harness compare <기록.jsonl>   (다른 스케줄러 버전의 기록에 현재 규칙을 돌려 공개 시각 차이를 본다)
        if (args.Length > 0) Environment.Exit(ReplayTests.ChildMain(args));

        string dir = Path.GetTempPath() + "apbl-h";
        Directory.CreateDirectory(dir);
        string namesPath = Path.Combine(dir, "names.tsv");
        File.WriteAllText(namesPath, "skill\t777\t훔치기\ncard\t42\t폭탄\nbuff\t4200\t폭탄\nbuff\t4300\t광폭\nbuff\t10006\t【표식】\n"
            + "monster\t3187\t도둑\nmonster\t3190\t타락한 봉황\nmonster\t3191\t마법 찻주전자\n"
            + "character\t1001\t패니\ncharacter\t1002\t루루\n", new System.Text.UTF8Encoding(false));
        var names = NameTable.Load(namesPath, _ => { });

        var log = new ManualLogSource("h");
        var lines = new List<string>();

        var logger = new BattleLogger(log, null, false, new HashSet<int>(), false, true, names);
        logger.Mirror = (s, k, g, u, _) => lines.Add(Palette.Strip(s));

        void Run(string label, byte[] body)
        {
            lines.Clear();
            logger.OnFrame(new FrameHeader(Op.UpdateHeroAttr, 0, 0, 0), body);
            Console.WriteLine($"--- {label} ---");
            if (lines.Count == 0) Console.WriteLine("   (출력 없음)");
            foreach (var l in lines) Console.WriteLine("   " + l);
            Console.WriteLine();
        }

        // A. 원인이 스킬 + 같은 스킬 버프 하나 → 사건이 남아야 한다 (회귀 검사)
        Run("A 스킬 원인 + 같은 스킬 버프 1개 (효과 줄 없음)",
            Frame.Cat(Frame.Fix64(1, 200), Cause(1, SkillId), SkillEffect(200, 9001)));

        // B. 같은 스킬·같은 대상이 두 번 → 한 줄
        Run("B 같은 스킬·같은 대상 2개",
            Frame.Cat(Frame.Fix64(1, 200), Cause(1, SkillId),
                      SkillEffect(200, 9002), SkillEffect(200, 9003)));

        // C. 같은 스킬·다른 대상 → 각각
        Run("C 같은 스킬·다른 대상 2개",
            Frame.Cat(Frame.Fix64(1, 200), Cause(1, SkillId),
                      SkillEffect(200, 9004), SkillEffect(300, 9005)));

        // D. 최대 체력 회복 → 걸러져야 한다
        Run("D 최대 체력 회복 (ori==curr==max, real=+2)",
            Frame.Cat(Frame.Fix64(1, 400), Cause(7, 0), HpEffect(400, 9, 9, 2, 2, 9, 9)));

        // E. 평범한 피해 → 남아야 한다
        Run("E 평범한 피해 (10→7, real=-3)",
            Frame.Cat(Frame.Fix64(1, 401), Cause(7, 0), HpEffect(401, 10, 7, -3, -3, 7, 10)));

        // F. 전후 같은데 최대 체력이 아님 → 미확정이라 남긴다
        Run("F 전후 동일·최대 체력 아님 (5→5/10, real=+2)",
            Frame.Cat(Frame.Fix64(1, 402), Cause(7, 0), HpEffect(402, 5, 5, 2, 2, 5, 10)));

        // G. 액티브 사용 직후의 메아리 → 스킬 사용만 남고 버프 줄은 사라진다
        lines.Clear();
        // UseEffectCardS2C { 1:pid, 8:useSkill, 9:skillId }
        logger.OnFrame(new FrameHeader(Op.UseEffectCard, 0, 0, 0), Frame.Cat(Frame.Fix64(1, 500), Frame.Varint(8, 1), Frame.Fix64(9, SkillId)));
        logger.OnFrame(new FrameHeader(Op.UpdateHeroAttr, 0, 0, 0), Frame.Cat(Frame.Fix64(1, 500), Cause(1, SkillId), SkillEffect(500, 9100)));
        Console.WriteLine("--- G 액티브 사용 → 메아리 ---");
        foreach (var l in lines) Console.WriteLine("   " + l);
        Console.WriteLine();

        // H. 카드 제출: 같은 uid 반복은 한 번, 다른 uid는 각각
        lines.Clear();
        void Card(long pid, int uid) => logger.OnFrame(new FrameHeader(Op.BattleUseCard, 0, 0, 0), Frame.Cat(Frame.Fix64(1, pid), Frame.Fix32(2, uid)));
        logger.OnFrame(new FrameHeader(Op.ActionStartNotify, 0, 0, 0), Frame.Fix64(1, 600));
        Card(600, 11); Card(600, 11); Card(600, 12);
        logger.OnFrame(new FrameHeader(Op.ActionStartNotify, 0, 0, 0), Frame.Fix64(1, 600));   // 행동이 바뀌면 초기화
        Card(600, 11);
        Console.WriteLine("--- H 카드 제출 (uid 11, 11, 12 | 행동 전환 | 11) ---");
        foreach (var l in lines) Console.WriteLine("   " + l);
        Console.WriteLine();

        // I. 로거가 줄마다 종류·그룹을 제대로 붙이는가
        var tagged = new List<string>();
        logger.Mirror = (s, k, g, u, _) => tagged.Add($"{k}#{g}u{u} {Palette.Strip(s).Trim()}");
        logger.MirrorNewPage = (r, g) => tagged.Add($"Page#{g} R{r}");
        // PK 결과 (Battle {1: {1:id, 2:atk{1:pid}, 3:def{1:pid}, 5:isEnd}})
        logger.OnFrame(new FrameHeader(Op.Battle, 0, 0, 0), Frame.Msg(1, Frame.Cat(
            Frame.Fix64(1, 1), Frame.Msg(2, Frame.Fix64(1, 700)), Frame.Msg(3, Frame.Fix64(1, 701)),
            Frame.Varint(5, 1))));
        // PK가 원인인 HP 변화 두 줄
        logger.OnFrame(new FrameHeader(Op.UpdateHeroAttr, 0, 0, 0), Frame.Cat(Frame.Fix64(1, 700), Cause(12, 1),
            HpEffect(701, 10, 7, -3, -3, 7, 10), HpEffect(700, 10, 9, -1, -1, 9, 10)));
        // 스킬이 원인
        logger.OnFrame(new FrameHeader(Op.UpdateHeroAttr, 0, 0, 0), Frame.Cat(Frame.Fix64(1, 800), Cause(1, SkillId),
            SkillEffect(800, 9200)));
        // 라운드 전환
        logger.OnFrame(new FrameHeader(Op.RoundStart, 0, 0, 0), Frame.Cat(Frame.Fix32(1, 3), Frame.Fix64(5, 700)));
        Console.WriteLine("--- I 종류·그룹 태깅 ---");
        foreach (var t in tagged) Console.WriteLine("   " + t);
        Console.WriteLine();

        int fails = 0;

        // J. 카드 기록을 꺼도 오버레이 커서는 카드·제출 연출만큼 진행해야 한다
        var hidden = new List<string>();
        var adv = new List<string>();
        var noCards = new BattleLogger(log, null, false, new HashSet<int>(), false, false, names);
        noCards.Mirror = (s, k, g, u, _) => hidden.Add($"{k} {Palette.Strip(s).Trim()}");
        noCards.MirrorAdvance = (k, g, u) => adv.Add(k.ToString());
        noCards.OnFrame(new FrameHeader(Op.BattleUseCard, 0, 0, 0), Frame.Cat(Frame.Fix64(1, 900), Frame.Fix32(2, 31)));
        noCards.OnFrame(new FrameHeader(Op.BattleUseCard, 0, 0, 0), Frame.Cat(Frame.Fix64(1, 901), Frame.Varint(3, 1)));
        noCards.OnFrame(new FrameHeader(Op.UseEffectCard, 0, 0, 0), Frame.Cat(Frame.Fix64(1, 902), Frame.Varint(8, 1), Frame.Fix64(9, SkillId)));
        noCards.OnFrame(new FrameHeader(Op.UseEffectCard, 0, 0, 0), Frame.Cat(Frame.Fix64(1, 900), Frame.Fix32(2, 42)));
        noCards.OnFrame(new FrameHeader(Op.UpdateHeroAttr, 0, 0, 0), Frame.Cat(Frame.Fix64(1, 900), Cause(2, 42),
            HpEffect(903, 10, 7, -3, -3, 7, 10)));
        Console.WriteLine("=== 카드 기록 끔 ===");
        bool j1 = string.Join(",", adv) == "Submit,Submit,Card";
        bool j2 = hidden.Exists(l => l.StartsWith("Skill ") && l.Contains("스킬 사용"));
        bool j3 = !hidden.Exists(l => l.Contains("폭탄"));
        Console.WriteLine($"  {(j1 ? "OK  " : "FAIL")} J1 카드·제출은 출력 없이 예약만: [{string.Join(",", adv)}]");
        Console.WriteLine($"  {(j2 ? "OK  " : "FAIL")} J2 스킬 사용은 스킬 종류로 남는다");
        Console.WriteLine($"  {(j3 ? "OK  " : "주의")} J3 카드 이름이 결과 머리줄로 새지 않는다");
        foreach (var l in hidden) Console.WriteLine("       " + l);
        Console.WriteLine();
        if (!j1) fails++;
        if (!j2) fails++;

        // K. 화면 동기화 신호는 실측으로 화면 단계와 대응한 응답에만 붙는다.
        //    하트비트처럼 화면과 무관한 응답까지 동기화로 보면 로그가 화면보다 먼저 나온다.
        var sync = new List<string>();
        var sl = new BattleLogger(log, null, false, new HashSet<int>(), false, true, names);
        sl.Mirror = (s, k, g, u, _) => { };
        sl.MirrorClear = () => sync.Add("clear");
        sl.MirrorSync = (cmd, len) => sync.Add($"sync{cmd}:{len}");
        string SyncOf(int cmdId, long upSn, int bodyLen = 0)
        {
            sync.Clear();
            sl.OnFrame(new FrameHeader(cmdId, 0, upSn, 0), new byte[bodyLen]);
            return string.Join(",", sync);
        }
        Console.WriteLine("=== 화면 동기화 신호 ===");
        // opcode와 본문 길이를 그대로 넘겨야 한다. 분석기가 "결정 창 열림"(5308, 본문 14)만
        // 연출 길이를 배우는 관측으로 쓰고 나머지는 커서 맞추기에만 쓰기 때문이다.
        var kCases = new (string Label, int Cmd, long Up, int Len, string Want)[]
        {
            ("TimeWasting(5308) 결정 창 열림", Op.TimeWasting, 7, 14, "sync5308:14"),
            ("TimeWasting(5308) 닫힘", Op.TimeWasting, 7, 9, "sync5308:9"),
            ("TimeWasting 방송(up=0)", Op.TimeWasting, 0, 14, ""),
            ("BattleUseCard(5036) 응답", Op.BattleUseCard, 7, 0, "sync5036:0"),
            ("BattleChoice(5040) 응답", Op.BattleChoice, 7, 0, "sync5040:0"),
            ("Heartbeat(5004) 응답", 5004, 7, 0, ""),
            ("RoundStart(1015) 응답", Op.RoundStart, 7, 0, ""),
        };
        int kn = 0;
        foreach (var c in kCases)
        {
            string res = SyncOf(c.Cmd, c.Up, c.Len);
            bool ok = res == c.Want;
            Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} K{++kn} {c.Label} → [{res}]");
            if (!ok) { fails++; Console.WriteLine($"       기대: [{c.Want}]"); }
        }
        Console.WriteLine();

        // L. 쓰러진 대상의 버프 해제는 대상별 한 줄로 합친다
        var fell = new List<string>();
        var dl = new BattleLogger(log, null, false, new HashSet<int>(), false, true, names);
        dl.Mirror = (s, k, g, u, _) => fell.Add(Palette.Strip(s).Trim());
        static byte[] BuffOp(long target, long uid, int buffId, int op) =>
            Frame.Msg(4, Frame.Cat(
                Frame.Fix64(1, target),
                Frame.Msg(6, Frame.Cat(
                    Frame.Fix64(1, target),
                    Frame.Msg(2, BuffMsg(uid, buffId, 0, 0)),
                    Frame.Varint(3, (ulong)op)))));
        void Attr(params byte[][] parts) =>
            dl.OnFrame(new FrameHeader(Op.UpdateHeroAttr, 0, 0, 0), Frame.Cat(parts));
        Attr(Frame.Fix64(1, 950), Cause(7, 0),
             BuffOp(950, 31, 101, 1), BuffOp(950, 32, 102, 1), BuffOp(950, 33, 103, 1),
             BuffOp(951, 34, 104, 1), BuffOp(952, 35, 105, 1));
        Attr(Frame.Fix64(1, 900), Cause(12, 1),
             HpEffect(950, 5, 0, -5, -5, 0, 10), HpEffect(952, 4, 0, -4, -4, 0, 10));
        fell.Clear();
        Attr(Frame.Fix64(1, 900),
             BuffOp(950, 31, 101, 2), BuffOp(950, 32, 102, 2), BuffOp(950, 33, 103, 2),
             BuffOp(951, 34, 104, 2), BuffOp(952, 35, 105, 2));
        Console.WriteLine("=== 쓰러짐 버프 해제 묶기 ===");
        bool l1 = fell.Exists(l => l == "버프 해제 ?950 3개 (쓰러짐)") && !fell.Exists(l => l.StartsWith("버프 해제 ?950 ") && !l.EndsWith("(쓰러짐)"));
        bool l2 = fell.Exists(l => l.StartsWith("버프 해제 ?951") && !l.Contains("쓰러짐"));
        bool l3 = fell.Exists(l => l.StartsWith("버프 해제 ?952") && !l.Contains("개 (쓰러짐)"));
        Console.WriteLine($"  {(l1 ? "OK  " : "FAIL")} L1 쓰러진 대상의 해제 3개 → 한 줄");
        Console.WriteLine($"  {(l2 ? "OK  " : "FAIL")} L2 살아 있는 대상은 그대로");
        Console.WriteLine($"  {(l3 ? "OK  " : "FAIL")} L3 해제가 하나면 원래 줄 유지");
        foreach (var l in fell) Console.WriteLine("       " + l);
        Console.WriteLine();
        if (!l1) fails++;
        if (!l2) fails++;
        if (!l3) fails++;

        // M. 게임 종료 구분선은 뒤따르는 정리성 갱신 다음에 나온다
        var end = new List<string>();
        var el = new BattleLogger(log, null, false, new HashSet<int>(), false, true, names);
        el.Mirror = (s, k, g, u, _) => end.Add(Palette.Strip(s).Trim());
        el.OnFrame(new FrameHeader(Op.GameFinish, 0, 0, 0), Array.Empty<byte>());
        el.OnFrame(new FrameHeader(Op.UpdateHeroAttr, 0, 0, 0), Frame.Cat(Frame.Fix64(1, 960), HpEffect(960, 3, 2, -1, -1, 2, 10)));
        bool m1 = !end.Exists(l => l.Contains("게임 종료"));
        el.OnFrame(new FrameHeader(5004, 0, 7, 0), Array.Empty<byte>());
        bool m2 = end.Count > 0 && end[^1].Contains("게임 종료") && end.FindIndex(l => l.Contains("HP 3→2")) < end.Count - 1;
        Console.WriteLine("=== 게임 종료 구분선 ===");
        Console.WriteLine($"  {(m1 ? "OK  " : "FAIL")} M1 종료 직후의 갱신이 끝날 때까지 구분선을 미룬다");
        Console.WriteLine($"  {(m2 ? "OK  " : "FAIL")} M2 다른 메시지가 오면 마지막 줄로 나온다: [{string.Join(" | ", end)}]");
        Console.WriteLine();
        if (!m1) fails++;
        if (!m2) fails++;

        // N. 행위자 없는 메시지의 주어, 스택 이름, 레벨 업, 효과카드 버프
        var subj = new List<string>();
        var sj = new BattleLogger(log, null, false, new HashSet<int>(), false, true, names);
        sj.Mirror = (s, k, g, u, _) => subj.Add(Palette.Strip(s).Trim());
        static byte[] Counter(int field, long target, int ori, int curr) =>
            Frame.Msg(4, Frame.Cat(Frame.Fix64(1, target), Frame.Msg(field, Frame.Cat(
                Frame.Fix64(1, target), Frame.Fix32(2, curr - ori), Frame.Fix32(3, ori), Frame.Fix32(4, curr)))));
        static byte[] Gold(long target, int ori, int curr) =>
            Frame.Msg(4, Frame.Cat(Frame.Fix64(1, target), Frame.Msg(2, Frame.Cat(
                Frame.Fix64(1, target), Frame.Fix32(2, curr - ori), Frame.Fix32(3, ori), Frame.Fix32(4, curr)))));
        static byte[] Level(long target, int lv) =>
            Frame.Msg(4, Frame.Cat(Frame.Fix64(1, target), Frame.Msg(11, Frame.Cat(Frame.Fix64(1, target), Frame.Fix32(2, lv)))));
        static byte[] Gain(long target, long uid, int buffId) =>
            Frame.Msg(4, Frame.Cat(Frame.Fix64(1, target), Frame.Msg(6, Frame.Cat(
                Frame.Fix64(1, target), Frame.Msg(2, BuffMsg(uid, buffId, 0, 0)), Frame.Varint(3, 1)))));
        List<string> Case(Action act) { subj.Clear(); act(); return new List<string>(subj); }
        void Attr2(params byte[][] parts) => sj.OnFrame(new FrameHeader(Op.UpdateHeroAttr, 0, 0, 0), Frame.Cat(parts));
        sj.OnFrame(new FrameHeader(Op.ActionStartNotify, 0, 0, 0), Frame.Fix64(1, 830));

        Console.WriteLine("=== 주어·스택·레벨·효과카드 버프 ===");
        // 골드 한 줄짜리는 송금 짝을 기다리느라 다음 줄이 나올 때 함께 나온다.
        var n1 = Case(() => { Attr2(Gold(831, 8, 15)); sj.OnFrame(new FrameHeader(Op.ActionStartNotify, 0, 0, 0), Frame.Fix64(1, 830)); });
        var n2 = Case(() => Attr2(Counter(15, 830, 3, 1)));
        var n3 = Case(() => Attr2(Cause(11, 0), Gold(832, 4, 10), Gold(833, 14, 19)));
        var n4 = Case(() => Attr2(Counter(17, 831, 0, 2)));
        var n5 = Case(() => Attr2(Gold(830, 18, 3), Level(830, 1)));
        var n6 = Case(() =>
        {
            sj.OnFrame(new FrameHeader(Op.UseEffectCard, 0, 0, 0), Frame.Cat(Frame.Fix64(1, 830), Frame.Fix32(2, 42)));
            Attr2(Gain(830, 71, 4200));
        });
        var n7 = Case(() =>
        {
            sj.OnFrame(new FrameHeader(Op.UseEffectCard, 0, 0, 0), Frame.Cat(Frame.Fix64(1, 830), Frame.Fix32(2, 42)));
            Attr2(Gain(830, 72, 4300));
        });
        var nCases = new (string Label, List<string> Got, string[] Want)[]
        {
            ("N1 차례 주인이 아닌 사람이 받은 골드 → 받은 사람이 주어", n1, new[] { "[R?] ?831", "?831 골드 8→15  +7", "· ?830 행동 시작" }),
            ("N2 차례 주인이 받은 것 → 그대로 차례 주인", n2, new[] { "[R?] ?830", "치유 ?830 3→1 (-2)" }),
            ("N3 여러 명이 받은 것 → 주어 없이 원인만", n3, new[] { "[R?] (라운드 보상)", "?832 골드 4→10  +6", "?833 골드 14→19  +5" }),
            ("N4 스택 이름은 게임 버프 이름", n4, new[] { "[R?] ?831", "【표식】 ?831 0→2 (+2)" }),
            ("N5 레벨 업", n5, new[] { "[R?] ?830", "?830 골드 18→3  -15", "?830 레벨 업 → Lv1" }),
            ("N6 카드 이름과 같은 버프는 카드 줄에 잇는다", n6, new[] { "[R?] ?830 효과카드 \"폭탄\"", "버프 부여 ?830 \"폭탄\"" }),
            ("N7 다른 이름의 버프는 따로 머리줄", n7, new[] { "[R?] ?830 효과카드 \"폭탄\"", "[R?] ?830", "버프 부여 ?830 \"광폭\"" }),
        };
        foreach (var c in nCases)
        {
            bool ok = string.Join("|", c.Got) == string.Join("|", c.Want);
            Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {c.Label}: [{string.Join(" / ", c.Got)}]");
            if (!ok) { fails++; Console.WriteLine($"       기대: [{string.Join(" / ", c.Want)}]"); }
        }
        Console.WriteLine();

        // O. 등록보다 먼저 버프를 받은 새 몬스터 — 등록이 따라오면 이름이 붙는다
        var spawn = new List<string>();
        var oAll = new List<string>();
        var ol = new BattleLogger(log, null, false, new HashSet<int>(), false, true, names);
        ol.Mirror = (s, k, g, u, _) => { string l = Palette.Strip(s).Trim(); spawn.Add(l); oAll.Add(l); };
        // MonsterRefreshS2C { 1: Player{1:id, 2:nick, 10:Hero{2:heroId}} } — 닉네임은 무시되어야 한다
        void Monster(long id, long heroId) => ol.OnFrame(new FrameHeader(Op.MonsterRefresh, 0, 0, 0),
            Frame.Msg(1, Frame.Cat(Frame.Fix64(1, id), Frame.Msg(2, System.Text.Encoding.UTF8.GetBytes("몹닉")),
                                   Frame.Msg(10, Frame.Fix64(2, heroId)))));
        void Beat() => ol.OnFrame(new FrameHeader(5004, 0, 7, 0), Array.Empty<byte>());
        void Hit(long target, long uid) => ol.OnFrame(new FrameHeader(Op.UpdateHeroAttr, 0, 0, 0),
            Frame.Cat(Frame.Fix64(1, 830), Cause(1, SkillId), SkillEffect(target, uid)));
        int At(string s) => spawn.FindIndex(l => l.Contains(s));
        Console.WriteLine("=== 새로 등장한 몬스터 ===");

        Monster(830, 0);
        Hit(3187, 9300);
        bool o1 = spawn.Count == 0;
        Monster(3187, 3187);
        bool o2 = At("→ 도둑") >= 0 && At("?3187") < 0;
        var o2Lines = new List<string>(spawn);

        spawn.Clear();
        Hit(3190, 9301);
        Hit(3191, 9302);
        Monster(3191, 3191);
        bool o3a = spawn.Count == 0;
        Monster(3190, 3190);
        bool o3 = o3a && At("→ 타락한 봉황") >= 0 && At("→ 타락한 봉황") < At("→ 마법 찻주전자");
        var o3Lines = new List<string>(spawn);

        spawn.Clear();
        Hit(3500, 9303);
        for (int i = 1; i < BattleLogger.DeferFrames; i++) Beat();
        bool o4a = At("?3500") < 0;
        Beat();
        bool o4 = o4a && At("?3500") >= 0;
        var o4Lines = new List<string>(spawn);

        spawn.Clear();
        Hit(3501, 9304);
        System.Threading.Thread.Sleep(BattleLogger.DeferWindow + TimeSpan.FromMilliseconds(50));
        Monster(3502, 0);
        bool o5 = At("?3501") >= 0;
        var o5Lines = new List<string>(spawn);

        spawn.Clear();
        Hit(3503, 9305);
        ol.OnFrame(new FrameHeader(Op.MatchSuccess, 0, 0, 0), Array.Empty<byte>());
        for (int i = 0; i <= BattleLogger.DeferFrames; i++) Beat();
        bool o6 = At("?3503") < 0;

        spawn.Clear();
        Monster(830, 0);
        Hit(4343, 9306);
        ol.OnFrame(new FrameHeader(Op.RunningGame, 0, 0, 0), Room(4000, 4343, "방닉", 1, 1002));
        bool o7 = At("→ 루루") >= 0 && At("?4343") < 0;
        var o7Lines = new List<string>(spawn);

        Console.WriteLine($"  {(o1 ? "OK  " : "FAIL")} O1 모르는 대상이 든 결과는 등록을 기다린다");
        Console.WriteLine($"  {(o2 ? "OK  " : "FAIL")} O2 등록이 온 그 프레임에 이름을 붙여 나온다: [{string.Join(" / ", o2Lines)}]");
        Console.WriteLine($"  {(o3 ? "OK  " : "FAIL")} O3 연속 미확정은 뒤가 먼저 풀려도 도착 순서대로: [{string.Join(" / ", o3Lines)}]");
        Console.WriteLine($"  {(o4 ? "OK  " : "FAIL")} O4 {BattleLogger.DeferFrames}프레임 안에 등록이 없으면 ?id로 확정: [{string.Join(" / ", o4Lines)}]");
        Console.WriteLine($"  {(o5 ? "OK  " : "FAIL")} O5 몬스터 등록만 이어져도 {BattleLogger.DeferWindow.TotalMilliseconds}ms가 지나면 확정: [{string.Join(" / ", o5Lines)}]");
        Console.WriteLine($"  {(o6 ? "OK  " : "FAIL")} O6 새 판이 시작되면 대기 항목은 넘어가지 않는다");
        Console.WriteLine($"  {(o7 ? "OK  " : "FAIL")} O7 명단(RunningGame)으로 풀려도 그 프레임에 나온다: [{string.Join(" / ", o7Lines)}]");
        Console.WriteLine();
        foreach (bool ok in new[] { o1, o2, o3, o4, o5, o6, o7 }) if (!ok) fails++;

        // U. 자리표시자 → 확정 이름. 닉네임은 표시명·캐시 어디에도 쓰이지 않는다
        Console.WriteLine("=== 자리표시자와 닉네임 ===");
        string pCachePath = Path.Combine(dir, "roster-cache-p.tsv");
        if (File.Exists(pCachePath)) File.Delete(pCachePath);
        var pCache = new RosterCache(pCachePath, _ => { });
        var roster = new Roster(names) { Cache = pCache };
        var pLines = new List<string>();
        var pl = new BattleLogger(log, null, false, new HashSet<int>(), false, true, names, pCache);
        pl.Mirror = (s, k, g, u, _) => pLines.Add(Palette.Strip(s).Trim());
        (bool Changed, string Name, int Cached, List<string> Lines) Step(long heroId)
        {
            pLines.Clear();
            byte[] body = Room(5000, 5252, "비밀닉", 1, heroId);
            bool changed = roster.Update(body) && roster.LastChanged.Contains(5252);
            pl.OnFrame(new FrameHeader(Op.RunningGame, 0, 0, 0), body);
            return (changed, Palette.Strip(roster.Name(5252)), pCache.Count, new List<string>(pLines));
        }
        var p0 = Step(0);
        var p1 = Step(9999);
        var p2 = Step(1001);
        var p3 = Step(1001);
        string pFile = File.Exists(pCachePath) ? File.ReadAllText(pCachePath) : "";
        bool pa = p0.Changed && p0.Name == "2P" && p0.Cached == 0 && p0.Lines.Count == 0;
        bool pb = p1.Changed && p1.Name == "2P" && p1.Cached == 0 && p1.Lines.Exists(l => l == "· 참가자 2P");
        bool pc = p2.Changed && p2.Name == "패니" && p2.Cached == 1 && p2.Lines.Exists(l => l == "· 참가자 패니")
                  && pFile.Contains("패니");
        bool pd = !p3.Changed && p3.Lines.Count == 0;
        var allP = new List<string>();
        foreach (var p in new[] { p0, p1, p2, p3 }) allP.AddRange(p.Lines);
        bool pe = !allP.Exists(l => l.Contains("비밀닉")) && !pFile.Contains("비밀닉")
                  && !oAll.Exists(l => l.Contains("몹닉") || l.Contains("방닉"));
        Console.WriteLine($"  {(pa ? "OK  " : "FAIL")} Ua 선택 전: 2P, 참가자 줄·캐시 없음");
        Console.WriteLine($"  {(pb ? "OK  " : "FAIL")} Ub 이름표 없는 HeroId: 이름이 같아도 갱신, 2P로 참가자 줄, 캐시 없음: [{string.Join(" / ", p1.Lines)}]");
        Console.WriteLine($"  {(pc ? "OK  " : "FAIL")} Uc 확정 이름: 갱신·참가자 줄·캐시 1건: [{string.Join(" / ", p2.Lines)}]");
        Console.WriteLine($"  {(pd ? "OK  " : "FAIL")} Ud 같은 명단이 다시 오면 바뀐 것 없음");
        Console.WriteLine($"  {(pe ? "OK  " : "FAIL")} Ue 닉네임이 줄·캐시 파일에 나오지 않는다");
        Console.WriteLine();
        foreach (bool ok in new[] { pa, pb, pc, pd, pe }) if (!ok) fails++;

        // Q. 이름표 캐시: 재접속으로 명단을 놓쳐도 이름이 되살아난다
        Console.WriteLine("=== 이름표 캐시 (재접속) ===");
        string cachePath = Path.Combine(dir, "roster-cache.tsv");
        if (File.Exists(cachePath)) File.Delete(cachePath);

        // Room { 1:roomId, 9:Player{1:id, 2:nick, 6:slot, 10:Hero{2:heroId}} }
        static byte[] Room(long roomId, long pid, string nick, int slot, long heroId) =>
            Frame.Msg(1, Frame.Cat(
                Frame.Fix64(1, roomId),
                Frame.Msg(9, Frame.Cat(
                    Frame.Fix64(1, pid),
                    Frame.Msg(2, System.Text.Encoding.UTF8.GetBytes(nick)),
                    Frame.Varint(6, (ulong)slot),
                    Frame.Msg(10, Frame.Fix64(2, heroId))))));

        var qNames = NameTable.Load(namesPath, _ => { });
        var q1Lines = new List<string>();
        var cacheA = new RosterCache(cachePath, _ => { });
        var q1 = new BattleLogger(log, null, false, new HashSet<int>(), false, true, qNames, cacheA);
        q1.Mirror = (s, k, g, u, _) => q1Lines.Add(Palette.Strip(s).Trim());
        q1.OnFrame(new FrameHeader(Op.RunningGame, 0, 0, 0), Room(555, 4242, "테스터", 0, 1001));
        bool q1ok = cacheA.Count > 0 && File.Exists(cachePath);

        string cacheText = File.ReadAllText(cachePath);
        bool q2ok = !cacheText.Contains("4242") && !cacheText.Contains("테스터") && cacheText.Contains("패니")
                    && cacheText.StartsWith("# roster cache v3");

        var q3Lines = new List<string>();
        var cacheB = new RosterCache(cachePath, _ => { });
        var q3 = new BattleLogger(log, null, false, new HashSet<int>(), false, true, qNames, cacheB);
        q3.Mirror = (s, k, g, u, _) => q3Lines.Add(Palette.Strip(s).Trim());
        q3.OnFrame(new FrameHeader(Op.ActionStartNotify, 0, 0, 0), Frame.Fix64(1, 4242));
        bool q3ok = q3Lines.Exists(l => l.Contains("패니 행동 시작"));

        q3.OnFrame(new FrameHeader(Op.MatchSuccess, 0, 0, 0), Array.Empty<byte>());
        bool q4ok = !File.Exists(cachePath) && cacheB.Count == 0;

        // 이전 버전 파일: v3가 만든 행을 그대로 두고 헤더만 바꾼다. 값은 v2 시절처럼 닉네임으로 바꿔 둔다.
        (bool Gone, List<string> Lines) Legacy(string? header)
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
            var make = new BattleLogger(log, null, false, new HashSet<int>(), false, true, qNames,
                                        new RosterCache(cachePath, _ => { }));
            make.Mirror = (s, k, g, u, _) => { };
            make.OnFrame(new FrameHeader(Op.RunningGame, 0, 0, 0), Room(555, 4242, "테스터", 0, 1001));
            var rows = new List<string>(File.ReadAllLines(cachePath));
            rows.RemoveAt(0);
            if (header is not null) rows.Insert(0, header);
            File.WriteAllText(cachePath, string.Join("\n", rows).Replace("패니", "테스터") + "\n");

            var legacy = new RosterCache(cachePath, _ => { });
            var got = new List<string>();
            var lg = new BattleLogger(log, null, false, new HashSet<int>(), false, true, qNames, legacy);
            lg.Mirror = (s, k, g, u, _) => got.Add(Palette.Strip(s).Trim());
            bool gone = legacy.Count == 0 && legacy.RoomId == 0 && !File.Exists(cachePath);
            lg.OnFrame(new FrameHeader(Op.ActionStartNotify, 0, 0, 0), Frame.Fix64(1, 4242));
            return (gone && got.Exists(l => l.Contains("?4242")) && !got.Exists(l => l.Contains("테스터")), got);
        }
        var q5 = Legacy("# roster cache v2 (keys are hashed; see RosterCache.cs)");
        var q6 = Legacy(null);

        Console.WriteLine($"  {(q1ok ? "OK  " : "FAIL")} Q1 명단을 받으면 캐시에 쌓인다 ({cacheA.Count}건)");
        Console.WriteLine($"  {(q2ok ? "OK  " : "FAIL")} Q2 v3 파일에 원본 id·닉네임이 없고 캐릭터 이름만 있다");
        Console.WriteLine($"  {(q3ok ? "OK  " : "FAIL")} Q3 v3 재접속은 명단 없이도 이름이 되살아난다: [{string.Join(" / ", q3Lines)}]");
        Console.WriteLine($"  {(q4ok ? "OK  " : "FAIL")} Q4 새 판이 시작되면 캐시를 버린다");
        Console.WriteLine($"  {(q5.Gone ? "OK  " : "FAIL")} Q5 v2 파일은 읽지 않고 지운다: [{string.Join(" / ", q5.Lines)}]");
        Console.WriteLine($"  {(q6.Gone ? "OK  " : "FAIL")} Q6 헤더 없는 파일도 지운다: [{string.Join(" / ", q6.Lines)}]");
        Console.WriteLine();
        foreach (bool ok in new[] { q1ok, q2ok, q3ok, q4ok, q5.Gone, q6.Gone }) if (!ok) fails++;

        // R. 안전장치 둘: 판 이탈(씬)과 최초 캐싱 전(다른 방)
        Console.WriteLine("=== 캐시 안전장치 ===");
        if (File.Exists(cachePath)) File.Delete(cachePath);

        var cacheC = new RosterCache(cachePath, _ => { });
        var r1 = new BattleLogger(log, null, false, new HashSet<int>(), false, true, qNames, cacheC);
        r1.Mirror = (s, k, g, u, _) => { };
        r1.OnFrame(new FrameHeader(Op.RunningGame, 0, 0, 0), Room(777, 5151, "나간이", 0, 1001));
        bool r1had = File.Exists(cachePath);
        r1.LeftGame();
        bool r1ok = r1had && !File.Exists(cachePath) && cacheC.Count == 0;

        var cacheD = new RosterCache(cachePath, _ => { });
        var r2 = new BattleLogger(log, null, false, new HashSet<int>(), false, true, qNames, cacheD);
        r2.Mirror = (s, k, g, u, _) => { };
        r2.OnFrame(new FrameHeader(Op.RunningGame, 0, 0, 0), Room(888, 6161, "옛방", 0, 1001));

        var cacheE = new RosterCache(cachePath, _ => { });
        var r3 = new BattleLogger(log, null, false, new HashSet<int>(), false, true, qNames, cacheE);
        var r3Lines = new List<string>();
        r3.Mirror = (s, k, g, u, _) => r3Lines.Add(Palette.Strip(s).Trim());
        r3.OnFrame(new FrameHeader(Op.RunningGame, 0, 0, 0), Room(999, 7171, "새방", 0, 1001));
        r3.OnFrame(new FrameHeader(Op.ActionStartNotify, 0, 0, 0), Frame.Fix64(1, 6161));
        bool r2ok = r3Lines.Exists(l => l.Contains("?6161"));

        if (File.Exists(cachePath)) File.Delete(cachePath);
        var cacheF = new RosterCache(cachePath, _ => { });
        var r4 = new BattleLogger(log, null, false, new HashSet<int>(), false, true, qNames, cacheF);
        r4.Mirror = (s, k, g, u, _) => { };
        r4.OnFrame(new FrameHeader(Op.RunningGame, 0, 0, 0), Room(1234, 8181, "지킴이", 0, 1001));

        var cacheG = new RosterCache(cachePath, _ => { });
        var r5 = new BattleLogger(log, null, false, new HashSet<int>(), false, true, qNames, cacheG);
        var r5Lines = new List<string>();
        r5.Mirror = (s, k, g, u, _) => r5Lines.Add(Palette.Strip(s).Trim());
        r5.OnFrame(new FrameHeader(Op.ActionStartNotify, 0, 0, 0), Frame.Fix64(1, 8181));
        bool r3ok = r5Lines.Exists(l => !l.Contains("?8181"));

        Console.WriteLine($"  {(r1ok ? "OK  " : "FAIL")} R1 판을 나가면 캐시 파일이 사라진다");
        Console.WriteLine($"  {(r2ok ? "OK  " : "FAIL")} R2 다른 방이면 지난 판 캐시를 쓰지 않는다: [{string.Join(" / ", r3Lines)}]");
        Console.WriteLine($"  {(r3ok ? "OK  " : "FAIL")} R3 같은 방 재접속은 캐시로 되살린다: [{string.Join(" / ", r5Lines)}]");
        Console.WriteLine();
        if (!r1ok) fails++;
        if (!r2ok) fails++;
        if (!r3ok) fails++;

        Console.WriteLine("=== 라운드를 모를 때 ===");
        var rtLines = new List<string>();
        var rt = new BattleLogger(log, null, false, new HashSet<int>(), false, true, qNames);
        rt.Mirror = (s, k, g, u, _) => rtLines.Add(Palette.Strip(s).Trim());
        rt.OnFrame(new FrameHeader(Op.ThrowDice, 0, 0, 0), Frame.Cat(Frame.Fix32(1, 3), Frame.Fix64(3, 2)));
        bool t1 = rtLines.Exists(l => l.StartsWith("[R?]"));
        rt.OnFrame(new FrameHeader(Op.RoundStart, 0, 0, 0), Frame.Cat(Frame.Fix32(1, 4), Frame.Fix64(5, 700)));
        rtLines.Clear();
        rt.OnFrame(new FrameHeader(Op.ThrowDice, 0, 0, 0), Frame.Cat(Frame.Fix32(1, 3), Frame.Fix64(3, 2)));
        bool t2 = rtLines.Exists(l => l.StartsWith("[R4]"));
        Console.WriteLine($"  {(t1 ? "OK  " : "FAIL")} T1 라운드를 못 받았으면 [R?]");
        Console.WriteLine($"  {(t2 ? "OK  " : "FAIL")} T2 전환을 받으면 실제 번호: [{string.Join(" / ", rtLines)}]");
        Console.WriteLine();
        if (!t1) fails++;
        if (!t2) fails++;

        Console.WriteLine("=== 라운드 시작 직후의 주인 없는 효과 ===");
        var owner = new List<string>();
        var ro = new BattleLogger(log, null, false, new HashSet<int>(), false, true, qNames);
        ro.Mirror = (s, k, g, u, _) => owner.Add(Palette.Strip(s).Trim());
        ro.OnFrame(new FrameHeader(Op.RoundStart, 0, 0, 0), Frame.Cat(Frame.Fix32(1, 1), Frame.Fix64(5, 700)));
        ro.OnFrame(new FrameHeader(Op.LandBuffs, 0, 0, 0), Frame.Msg(1, Frame.Cat(
            Frame.Fix32(1, 12), Frame.Msg(2, Frame.Msg(1, Frame.Msg(2, Frame.Cat(Frame.Fix64(1, 55), Frame.Fix32(2, 9))))))));
        bool rs1 = !owner.Exists(l => l.Contains("스킬 사용"));
        Console.WriteLine($"  {(rs1 ? "OK  " : "FAIL")} RS1 라운드 시작 패킷의 받는 사람을 차례 주인으로 보지 않는다: [{string.Join(" / ", owner)}]");
        Console.WriteLine();
        if (!rs1) fails++;

        fails += Sched.Run();

        Console.WriteLine();
        Console.WriteLine("=== 프레임 헤더 ===");
        var rs = new FrameReassembler();
        byte[] fr = new byte[35 + 3];
        fr[3] = 3;                                    // LENGTH = 3
        fr[12] = 0x04; fr[13] = 0x10;                 // CMDID = 1040
        long up = 0x0102030405060708, down = 0x7F00000000000042;
        for (int i = 0; i < 8; i++) { fr[17 + i] = (byte)(up >> (56 - 8 * i)); fr[25 + i] = (byte)(down >> (56 - 8 * i)); }
        fr[33] = 0xFF; fr[34] = 0xFE;                 // ERR = -2
        rs.Append(fr, 0, 20);
        bool none = !rs.TryDequeue(out _, out _);
        rs.Append(fr, 20, fr.Length - 20);
        bool hgot = rs.TryDequeue(out FrameHeader fh, out byte[] fb);
        bool hok = none && hgot && fh.CmdId == 1040 && fh.ErrId == -2 && fh.UpSn == up && fh.DownSn == down && fb.Length == 3;
        Console.WriteLine($"  {(hok ? "OK  " : "FAIL")} H1 cmd={fh.CmdId} err={fh.ErrId} up=0x{fh.UpSn:X} down=0x{fh.DownSn:X} len={fb.Length}");
        if (!hok) fails++;

        Console.WriteLine();
        Console.WriteLine("=== 파일 기록 (전용 스레드) ===");
        string file = Path.Combine(dir, "battle-log-test.txt");
        File.WriteAllText(file, "지난 판 내용\n");
        var fl = new BattleLogger(log, file, false, new HashSet<int>(), false, true, names);
        fl.OnFrame(new FrameHeader(Op.ActionStartNotify, 0, 0, 0), Frame.Fix64(1, 1));            // 줄 1
        fl.OnFrame(new FrameHeader(Op.StartGame, 0, 0, 0), Array.Empty<byte>());                  // 새 판 → 비우기
        fl.OnFrame(new FrameHeader(Op.ActionStartNotify, 0, 0, 0), Frame.Fix64(1, 2));            // 줄 2
        for (int i = 0; i < 500; i++) fl.OnFrame(new FrameHeader(Op.ThrowDice, 0, 0, 0), Frame.Cat(Frame.Fix32(1, 3), Frame.Fix64(3, 2)));
        fl.Close();
        string[] got = File.ReadAllLines(file);
        bool clean = got.Length == 501 && got[0].EndsWith("· ?2 행동 시작") && !Array.Exists(got, l => l.Contains("지난 판") || l.Contains("?1 행동"));
        bool stamped = Array.TrueForAll(got, l => l.Length > 13 && l[2] == ':' && l[12] == ' ');
        Console.WriteLine($"  {(clean ? "OK  " : "FAIL")} F1 비우기 뒤의 줄만 남고 순서 유지 ({got.Length}줄, 첫 줄: {got[0]})");
        Console.WriteLine($"  {(stamped ? "OK  " : "FAIL")} F2 모든 줄에 시각이 찍힘");
        if (!clean) fails++;
        if (!stamped) fails++;
        fl.OnFrame(new FrameHeader(Op.ActionStartNotify, 0, 0, 0), Frame.Fix64(1, 3));   // 닫은 뒤: 예외 없이 무시
        Console.WriteLine("  OK   F3 닫은 뒤 넣어도 예외 없음");

        fails += HealthTests.Run();
        fails += ReplayTests.Run(dir);
        Environment.Exit(fails);
    }
}
