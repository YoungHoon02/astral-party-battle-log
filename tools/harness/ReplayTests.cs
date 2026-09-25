using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AstralPartyBattleLog.Log;
using AstralPartyBattleLog.UI;
using BepInEx.Logging;

// 재생 기록(S1)·재생기(S3) 검증. 스케줄러가 정적 상태라 기록과 재생은 각각 새 프로세스에서 돌린다.
static class ReplayTests
{
    static int _fail;

    static void Check(bool ok, string label, string? detail = null)
    {
        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {label}{(detail is null ? "" : $": {detail}")}");
        if (!ok) _fail++;
    }

    // ── 자식 프로세스 모드 ─────────────────────────────────────────────

    public static int ChildMain(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        switch (args[0])
        {
            case "sched":
                return ChildSched(args.Length > 1 ? args[1] : null);
            case "killed":
                return ChildKilled(args[1]);
            case "scenario":
                return ChildScenario(args[1], args[2]);
            case "replay":
                return ReplayCheck.Run(args[1], lenient: args.Contains("--lenient"));
            case "compare":
                return ReplayCheck.Compare(args[1], lenient: args.Contains("--lenient"));
        }
        Console.Error.WriteLine($"unknown mode {args[0]}");
        return 64;
    }

    static ReplayWriter NewWriter(string file) =>
        OverlaySchedule.NewReplayWriter(new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read),
                                        s => Console.Error.WriteLine(s));

    static int ChildSched(string? file)
    {
        ReplayWriter? w = file is null ? null : NewWriter(file);
        if (w is not null) OverlaySchedule.StartReplay(w);
        var sw = Stopwatch.StartNew();
        int fails = Sched.Run();
        sw.Stop();
        if (w is not null) OverlaySchedule.Shutdown(unload: false);
        Console.WriteLine($"#stat ms={sw.ElapsedMilliseconds} maxQueue={w?.MaxQueueDepth ?? 0} "
                          + $"bytes={(file is null ? 0 : new FileInfo(file).Length)}");
        return fails;
    }

    // 게임처럼 ProcessExit 없이 프로세스가 강제로 끝나는 경우. Shutdown을 부르지 않고 스스로 종료한다.
    static int ChildKilled(string file)
    {
        OverlaySchedule.StartReplay(NewWriter(file));
        int fails = Sched.Run();
        Thread.Sleep(500);
        Process.GetCurrentProcess().Kill();
        return fails;
    }

    static int ChildScenario(string name, string file)
    {
        var log = new ManualLogSource("scenario");
        var shown = new List<string>();
        void Pump() => OverlaySchedule.Pump(s => shown.Add(s ?? "?"), r => shown.Add($"<page {r}>"), () => shown.Add("<clear>"));
        void Step(double seconds, float speed = 1f)
        {
            OverlaySchedule.Tick((float)seconds, 0f, speed);
            Pump();
        }

        if (name == "mid") OverlaySchedule.Tick(0.01f, 0f, 1f);
        OverlaySchedule.StartReplay(NewWriter(file));
        OverlaySchedule.Init(log, true, 60000, false);
        switch (name)
        {
            case "mid":
                OverlaySchedule.Line("x", LineKind.Card, 1, 0);
                Step(0.1);
                break;
            case "budget":
                // 큐 예산(4096) 초과 강제 해제를 계측 사유 budget으로 구별하는지.
                for (int i = 0; i < 4100; i++) OverlaySchedule.Line("d", LineKind.Dice, i, 3, 3);
                Step(0.01);
                Step(0.01);
                break;
            case "pending":
                OverlaySchedule.Line("d", LineKind.Dice, 1, 3, 3);
                OverlaySchedule.Line("after", LineKind.Card, 2, 0);
                Step(0.5);
                break;
            case "precise":
                // 반올림하면 달라지는 배속·시각을 그대로 보존하는지.
                OverlaySchedule.Tick(0.0123457f, 0f, 1.3f);
                OverlaySchedule.Line("pk", LineKind.Attack, 1, 0, 0);
                OverlaySchedule.Line("dmg", LineKind.Hit, 1, 0);
                Step(0.0333, 1.3f);
                OverlaySchedule.Screen(ScreenSignal.Window, 0, true, (IntPtr)0x7ff00001);
                Step(3.1, 1.3f);
                OverlaySchedule.Screen(ScreenSignal.Hit, 0, true, (IntPtr)0x7ff00002);
                Step(0.017, 1.3f);
                OverlaySchedule.Screen(ScreenSignal.Hit, 0, false, (IntPtr)0x7ff00002);
                OverlaySchedule.Screen(ScreenSignal.Window, 0, false, (IntPtr)0x7ff00001);
                Step(0.017, 1.3f);
                break;
            case "overlap-pump":
                // Pump가 줄을 내보내는 도중 새 판이 시작되는 경합. 게임 동작은 그대로여야 한다.
                OverlaySchedule.FallBackToModel();
                OverlaySchedule.Line("a", LineKind.Immediate, 1, 0);
                OverlaySchedule.Tick(0.01f, 0f, 1f);
                OverlaySchedule.Pump(s =>
                {
                    shown.Add(s);
                    OverlaySchedule.Reset();
                }, _ => { }, () => shown.Add("<clear>"));
                OverlaySchedule.Line("b", LineKind.Immediate, 2, 0);
                Step(0.01);
                break;
            case "overlap-span":
                // 세대 변경이 진행 중일 때 Pump가 시작되는 경합(변경 구간이 처리 시작을 걸침).
                OverlaySchedule.Reset();
                Step(0.01);
                ScheduleHealth.Init(_ => Pump(), null);
                OverlaySchedule.Reset();
                ScheduleHealth.Init(_ => { }, null);
                Step(0.01);
                break;
            case "overlap-controls":
                // Reset 도중 Discard가 끼어드는 변경끼리의 경합. 기록 순서와 세대 순서가 어긋날 수 있다.
                OverlaySchedule.Reset();
                Step(0.01);
                ScheduleHealth.Init(_ => OverlaySchedule.Discard(), null);
                OverlaySchedule.Reset();
                ScheduleHealth.Init(_ => { }, null);
                Step(0.01);
                break;
        }
        OverlaySchedule.Shutdown(unload: false);
        Console.WriteLine("#shown " + string.Join(",", shown));
        return 0;
    }

    // ── 부모 프로세스 검증 ─────────────────────────────────────────────

    static (int Code, string Out) Child(params string[] args)
    {
        string exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(ReplayTests).Assembly.Location);
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.StandardOutputEncoding = Encoding.UTF8;
        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + err.Result);
    }

    static string Stat(string output, string key)
    {
        Match m = Regex.Match(output, $@"#stat .*\b{key}=(\d+)");
        return m.Success ? m.Groups[1].Value : "?";
    }

    static string WithoutStats(string output) =>
        string.Join("\n", output.Split('\n').Where(l => !l.StartsWith("#stat")));

    public static int Run(string dir)
    {
        _fail = 0;
        Console.WriteLine();
        Console.WriteLine("=== 재생 기록·재생기 ===");
        string sched = Path.Combine(dir, "sched.jsonl");
        if (File.Exists(sched)) File.Delete(sched);

        var off = Child("sched");
        var on = Child("sched", sched);
        Check(off.Code == 0 && on.Code == 0, "RP1 기존 하네스가 기록 off/on 모두 통과", $"exit {off.Code}/{on.Code}");
        Check(WithoutStats(off.Out) == WithoutStats(on.Out), "RP1 기록 off/on의 하네스 출력이 같다");
        Console.WriteLine($"       비용: off {Stat(off.Out, "ms")}ms / on {Stat(on.Out, "ms")}ms, "
                          + $"파일 {Stat(on.Out, "bytes")} bytes, writer 큐 최대 {Stat(on.Out, "maxQueue")}");

        var replay = Child("replay", sched);
        Check(replay.Code == 0 && replay.Out.Contains("결정 일치"), "RP2 새 프로세스에서 엄격 재생하면 기존 하네스 전체의 결정이 일치");
        foreach (string l in replay.Out.Split('\n').Where(l => l.Length > 0)) Console.WriteLine("       " + l.TrimEnd());

        string killed = Path.Combine(dir, "killed.jsonl");
        if (File.Exists(killed)) File.Delete(killed);
        Child("killed", killed);
        var killedReplay = Child("replay", killed);
        Check(killedReplay.Code == 0 && killedReplay.Out.Contains("checkpoint") && killedReplay.Out.Contains("결정 일치"),
              "RP5 종료 처리 없이 강제 종료된 기록도 마지막 임시 end로 닫혀 엄격 재생이 일치한다",
              Regex.Match(killedReplay.Out, "결정 [^\n]*").Value.Trim());

        var same = Child("compare", sched);
        Check(same.Code == 0 && same.Out.Contains("시각이 바뀐 줄 0건") && same.Out.Contains("조기 표시(원래 화면 신호 시각보다 먼저): 0건"),
              "RP6 같은 버전 기록을 비교 모드로 돌리면 바뀐 공개 시각이 없다", Regex.Match(same.Out, "공개 줄[^\n]*").Value.Trim());

        byte[] bytes = File.ReadAllBytes(sched);
        List<string> lines = Encoding.UTF8.GetString(bytes).Split('\n').Where(l => l.Length > 0).ToList();
        CheckContent(lines);
        CheckCorruption(bytes, lines);
        CheckScenarios(dir);
        CheckWriter(dir);

        Console.WriteLine(_fail == 0 ? "=== 재생 전부 통과 ===" : $"=== 재생 실패 {_fail}건 ===");
        return _fail;
    }

    static List<JsonElement> Parse(IEnumerable<string> lines) =>
        lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    static void CheckContent(List<string> lines)
    {
        List<JsonElement> recs = Parse(lines);
        var resets = recs.Where(r => r.GetProperty("type").GetString() == "control" && r.GetProperty("op").GetString() == "reset").ToList();
        bool clearOnce = resets.Count > 0 && resets.All(c =>
            recs.Count(r => r.GetProperty("type").GetString() == "item"
                            && r.GetProperty("parentControlId").ValueKind == JsonValueKind.Number
                            && r.GetProperty("parentControlId").GetInt64() == c.GetProperty("controlId").GetInt64()
                            && r.GetProperty("signal").GetString() == "clear") == 1);
        Check(clearOnce, "RP3 Reset마다 비우기 항목이 정확히 하나 parentControlId로 연결된다", $"reset {resets.Count}건");
        bool discardNoItem = recs.Where(r => r.GetProperty("type").GetString() == "control" && r.GetProperty("op").GetString() == "discard")
            .All(c => !recs.Any(r => r.GetProperty("type").GetString() == "item" && r.GetProperty("parentControlId").ValueKind == JsonValueKind.Number
                                     && r.GetProperty("parentControlId").GetInt64() == c.GetProperty("controlId").GetInt64()));
        Check(discardNoItem, "RP3 Discard는 세대만 바꾸고 항목을 만들지 않는다");

        string Reasons(string action) => string.Join(",", recs
            .Where(r => r.GetProperty("type").GetString() == "decision" && r.GetProperty("action").GetString() == action)
            .Select(r => r.GetProperty("reason").GetString()).Distinct().OrderBy(s => s));
        Console.WriteLine($"       release 사유: {Reasons("release")} / resolve: {Reasons("resolve")} / drop: {Reasons("drop")}");
        string rel = Reasons("release");
        Check(new[] { "model", "sync", "own-signal", "later-signal", "window-close", "cap", "group-follow", "counter-sync" }
                  .All(r => rel.Split(',').Contains(r)),
              "RP4 모델 기한·동기화 앞당김·자기 신호·늦은 경로·창 닫힘·상한·그룹 동반·반격 동기화 사유가 모두 나온다");
        Check(recs.Any(r => r.GetProperty("type").GetString() == "decision" && r.GetProperty("action").GetString() == "transfer"),
              "RP4 FallBackToModel의 예약 이동이 transfer로 남는다");
        Check(recs.Any(r => r.GetProperty("type").GetString() == "item" && r.GetProperty("signal").GetString() == "advance"
                            && r.GetProperty("kind").GetString() == "submit"),
              "RP4 Advance의 kind가 보존된다");
        Check(recs.Any(r => r.GetProperty("type").GetString() == "item" && r.GetProperty("detail").GetInt64() == 205),
              "RP4 다중 주사위 detail(5+2 → 205)이 보존된다");
        bool gatedReleaseNoDue = recs.Where(r => r.GetProperty("type").GetString() == "decision"
                                                  && r.GetProperty("action").GetString() == "release"
                                                  && r.GetProperty("reason").GetString() == "own-signal")
            .All(r => r.GetProperty("dueUs").ValueKind == JsonValueKind.Null && r.GetProperty("resolvedUs").ValueKind == JsonValueKind.Number);
        Check(gatedReleaseNoDue, "RP4 게이트 해제는 예약값 없이 해결 시각과 출력 시각을 따로 남긴다");
    }

    // 끝 레코드의 개수·바이트·해시를 다시 맞춰 한 가지 결함만 남긴다.
    static byte[] Reseal(List<string> lines, bool renumber, Action<JsonObject>? editEnd = null)
    {
        var body = new StringBuilder();
        for (int i = 0; i < lines.Count - 1; i++)
        {
            string l = renumber ? Regex.Replace(lines[i], @"^\{""recordSeq"":\d+,", $"{{\"recordSeq\":{i + 1},") : lines[i];
            body.Append(l).Append('\n');
        }
        byte[] head = Encoding.UTF8.GetBytes(body.ToString());
        JsonObject end = JsonNode.Parse(lines[^1])!.AsObject();
        if (renumber) end["recordSeq"] = lines.Count;
        end["previousCount"] = lines.Count - 1;
        end["previousBytes"] = head.Length;
        end["sha256"] = Convert.ToHexString(SHA256.HashData(head)).ToLowerInvariant();
        editEnd?.Invoke(end);
        return head.Concat(Encoding.UTF8.GetBytes(end.ToJsonString() + "\n")).ToArray();
    }

    static void Rejects(byte[] data, string label, string expect)
    {
        ReplayCheck.Parsed p = ReplayCheck.Verify(data);
        bool ok = p.Problems.Any(s => s.Contains(expect));
        Check(ok, label, ok ? p.Problems.First(s => s.Contains(expect)) : $"[{string.Join(" | ", p.Problems)}]");
    }

    static void CheckCorruption(byte[] bytes, List<string> lines)
    {
        Check(ReplayCheck.Verify(bytes).Problems.Count == 0, "C0 원본 기록은 엄격 검사를 통과한다");
        int mid = lines.FindIndex(l => l.Contains("\"type\":\"decision\""));

        var gap = new List<string>(lines);
        gap.RemoveAt(mid);
        Rejects(Reseal(gap, renumber: false), "C1 레코드 하나가 빠진 파일(순번 누락) 거부", "순번 누락");

        var dup = new List<string>(lines);
        dup.Insert(mid, lines[mid]);
        Rejects(Reseal(dup, renumber: false), "C2 레코드가 중복된 파일 거부", "순번 중복");

        Rejects(bytes.Take(bytes.Length / 2).ToArray(), "C3 중간에서 잘린 파일 거부", "잘린 파일");
        byte[] noEnd = Encoding.UTF8.GetBytes(string.Join("\n", lines.Take(lines.Count - 1)) + "\n");
        Rejects(noEnd, "C4 end가 없는 파일 거부", "end 없음");

        string tampered = Regex.Replace(lines[mid], @"""releasedUs"":(\d)", m => $"\"releasedUs\":{(m.Groups[1].Value == "9" ? 8 : int.Parse(m.Groups[1].Value) + 1)}");
        if (tampered == lines[mid]) tampered = lines[mid].Replace("\"pumpId\":", "\"pumpId\": ");
        var hashed = new List<string>(lines) { [mid] = tampered };
        Rejects(Encoding.UTF8.GetBytes(string.Join("\n", hashed) + "\n"), "C5 내용을 바꾼 파일(해시 불일치) 거부", "해시 불일치");

        var broken = new List<string>(lines) { [mid] = lines[mid][..^5] };
        Rejects(Reseal(broken, renumber: false), "C6 깨진 JSON 줄이 있는 파일 거부", "깨진 JSON");

        var unfinished = new List<string>(lines);
        unfinished.RemoveAt(unfinished.FindLastIndex(l => l.Contains("\"type\":\"pump-end\"")));
        Rejects(Reseal(unfinished, renumber: true), "C7 마지막 Pump가 닫히지 않은 파일 거부", "미완성 Pump");

        Rejects(Reseal(lines, false, e => e["dropped"] = 3), "C8 end.dropped>0 거부", "기록 유실");
        Rejects(Reseal(lines, false, e => e["ioFailed"] = true), "C9 쓰기 실패 표시 거부", "쓰기 실패");
        Rejects(Reseal(lines, false, e => e["overlap"] = true), "C10 경합 표시 거부", "경합");
        Rejects(Reseal(lines, false, e => e["reason"] = "incomplete"), "C11 불완전 종료 거부", "불완전 종료");

        var mid2 = new List<string>(lines) { [0] = lines[0].Replace("\"start\":\"clean\"", "\"start\":\"mid\"") };
        Rejects(Reseal(mid2, false), "C12 중간 시작 기록 거부", "중간 시작");
        var ver = new List<string>(lines) { [0] = lines[0].Replace($"\"schedulerVersion\":{OverlaySchedule.SchedulerVersion}", "\"schedulerVersion\":999") };
        Rejects(Reseal(ver, false), "C13 모르는 스케줄러 버전 거부", "모르는 스케줄러 버전");
        var noHeader = new List<string>(lines);
        noHeader.RemoveAt(0);
        Rejects(Reseal(noHeader, true), "C14 버전 헤더 없는 파일 거부", "버전 헤더 없음");
    }

    static void CheckScenarios(string dir)
    {
        string F(string n) => Path.Combine(dir, $"scenario-{n}.jsonl");
        foreach (string n in new[] { "mid", "budget", "pending", "precise", "overlap-pump", "overlap-span", "overlap-controls" })
            if (File.Exists(F(n))) File.Delete(F(n));

        var mid = Child("scenario", "mid", F("mid"));
        var midReplay = Child("replay", F("mid"));
        Check(midReplay.Code == 2 && midReplay.Out.Contains("중간 시작"), "SC1 스케줄러를 건드린 뒤 켠 기록은 mid로 남고 거부된다");

        Child("scenario", "budget", F("budget"));
        var budget = Child("replay", F("budget"));
        Check(budget.Code == 0 && budget.Out.Contains("release/budget="), "SC2 큐 예산 강제 해제가 budget 사유로 재현된다",
              Regex.Match(budget.Out, @"resolve/budget=\d+").Value + " " + Regex.Match(budget.Out, @"release/budget=\d+").Value);

        Child("scenario", "pending", F("pending"));
        var pending = Child("replay", F("pending"));
        Check(pending.Code == 0 && pending.Out.Contains("대기 중 2건"), "SC3 정상 종료 때 남은 입력은 미관측으로 보고하고 재생은 통과",
              Regex.Match(pending.Out, "미관측: [^\n]*").Value.Trim());

        Child("scenario", "precise", F("precise"));
        var precise = Child("replay", F("precise"));
        List<JsonElement> recs = Parse(File.ReadAllLines(F("precise")));
        JsonElement pk = recs.First(r => r.GetProperty("type").GetString() == "item" && r.GetProperty("kind").GetString() == "attack");
        int bits = BitConverter.SingleToInt32Bits(1.3f);
        long ticked = recs.First(r => r.GetProperty("type").GetString() == "tick").GetProperty("nowUs").GetInt64();
        Check(pk.GetProperty("speedBits").GetInt64() == bits && pk.GetProperty("receivedUs").GetInt64() == ticked,
              "SC4 배속은 float 비트값, 수신 시각은 내부 시각 그대로 남는다", $"speedBits={pk.GetProperty("speedBits")} receivedUs={pk.GetProperty("receivedUs")}");
        bool rawPointer = File.ReadAllText(F("precise")).Contains(0x7ff00001.ToString());
        Check(precise.Code == 0 && !rawPointer, "SC4 배속 1.3 게이트 기록도 재생이 일치하고 원시 주소는 파일에 없다");

        var overlapPump = Child("scenario", "overlap-pump", F("overlap-pump"));
        var opReplay = Child("replay", F("overlap-pump"));
        Check(overlapPump.Out.Contains("#shown a,<clear>,b"), "SC5 Pump 도중 Reset이 와도 게임 동작(출력 순서)은 그대로",
              Regex.Match(overlapPump.Out, "#shown [^\r\n]*").Value);
        Check(opReplay.Code == 2 && opReplay.Out.Contains("경합"), "SC5 Pump와 겹친 세대 변경 기록은 엄격 재생이 거부한다");

        Child("scenario", "overlap-span", F("overlap-span"));
        var osReplay = Child("replay", F("overlap-span"));
        Check(osReplay.Code == 2 && osReplay.Out.Contains("경합"), "SC6 변경이 진행 중일 때 시작한 Pump도 경합으로 잡는다");
        Child("scenario", "overlap-controls", F("overlap-controls"));
        var ocReplay = Child("replay", F("overlap-controls"));
        Check(ocReplay.Code == 2 && ocReplay.Out.Contains("경합"), "SC7 세대 변경끼리 겹친 기록도 경합으로 거부한다");
        var lenient = Child("replay", F("overlap-span"), "--lenient");
        Check(lenient.Out.Contains("불완전 재생(참고용)"), "SC6 참고 재생 모드는 불완전 재생임을 밝힌다");
    }

    // ── writer ──────────────────────────────────────────────────────────

    sealed class GateStream : MemoryStream
    {
        public readonly ManualResetEventSlim Open = new(false);
        public int FailOnWrite = -1;
        int _writes;

        public override void Write(byte[] buffer, int offset, int count)
        {
            Open.Wait();
            if (++_writes == FailOnWrite) throw new IOException("disk full (test)");
            base.Write(buffer, offset, count);
        }
    }

    // 스키마 종류 번호: 0 header, 13 mark (OverlaySchedule.Replay.cs의 Rec 순서).
    static ReplayRecord HeaderRec() => new() { Type = 0, A = OverlaySchedule.ReplayFormat, B = OverlaySchedule.SchedulerVersion };
    static ReplayRecord MarkRec(long t) => new() { Type = 13, A = t };

    // 스키마 종류 번호: 7 pump-begin, 12 pump-end.
    static ReplayRecord PumpRec(byte type, long pumpId) => new() { Type = type, A = pumpId, B = pumpId * 1000, C = 0, D = 0 };

    // writer 스레드가 쓰는 중일 수 있어, 기대한 줄이 보이고 두 번 읽은 내용이 같을 때까지 기다린다.
    static string Snapshot(GateStream s, string expect)
    {
        string last = "";
        for (int i = 0; i < 200; i++)
        {
            string now = Encoding.UTF8.GetString(s.ToArray());
            if (now == last && now.Contains(expect) && now.EndsWith("}\n")) return now;
            last = now;
            Thread.Sleep(20);
        }
        return last;
    }

    static string EndOf(GateStream s) => Encoding.UTF8.GetString(s.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .LastOrDefault(l => l.Contains("\"type\":\"end\"")) ?? "(no end)";

    static void CheckWriter(string dir)
    {
        var warns = new List<string>();

        var full = new GateStream();
        var w1 = new ReplayWriter(full, OverlaySchedule.FormatRecord, warns.Add, capacity: 4);
        w1.Add(HeaderRec());
        for (int i = 0; i < 50; i++) w1.Add(MarkRec(i));
        full.Open.Set();
        w1.Close("normal", 5000);
        string e1 = EndOf(full);
        Check(w1.Dropped > 0 && e1.Contains("\"reason\":\"incomplete\""), "WR1 큐가 차면 게임 스레드는 기다리지 않고 버린 수를 end에 남긴다",
              $"dropped={w1.Dropped}");
        Rejects(full.ToArray(), "WR1 유실된 기록은 거부된다", "기록 유실");

        var flaky = new GateStream { FailOnWrite = 2 };
        flaky.Open.Set();
        var w2 = new ReplayWriter(flaky, OverlaySchedule.FormatRecord, warns.Add);
        w2.Add(HeaderRec());
        SpinWait.SpinUntil(() => w2.BytesWritten > 0, 2000);
        for (int i = 0; i < 5; i++) w2.Add(MarkRec(i));
        w2.Close("normal", 5000);
        string e2 = EndOf(flaky);
        Check(w2.IoFailed && e2.Contains("\"ioFailed\":true"), "WR2 중간 배치 쓰기 실패는 뒤이은 쓰기(end)가 성공해도 실패로 남는다", e2.Length > 90 ? e2[..90] + "…" : e2);
        Check(warns.Any(s => s.Contains("write failed")), "WR2 쓰기 예외를 삼키지 않고 경고한다");
        Rejects(flaky.ToArray(), "WR2 쓰기 실패 기록은 거부된다", "쓰기 실패");

        var small = new GateStream();
        small.Open.Set();
        var w3 = new ReplayWriter(small, OverlaySchedule.FormatRecord, warns.Add, maxBytes: 700);
        w3.Add(HeaderRec());
        for (int i = 0; i < 20; i++) w3.Add(MarkRec(i));
        w3.Close("normal", 5000);
        int smallBytes = small.ToArray().Length;
        Check(EndOf(small).Contains("\"reason\":\"incomplete\"") && smallBytes <= 700,
              "WR3 용량 상한에 닿으면 앞부분을 덮지 않고 계측을 멈춘 뒤 불완전 end로 닫는다", $"{smallBytes} bytes");

        var live = new GateStream();
        live.Open.Set();
        var w5 = OverlaySchedule.NewReplayWriter(live, warns.Add);
        w5.Add(HeaderRec());
        w5.Add(PumpRec(7, 1));
        w5.Add(PumpRec(12, 1));
        w5.Add(PumpRec(7, 2));
        string snap = Snapshot(live, "\"pumpId\":1,\"observedGen\"");
        ReplayCheck.Parsed p5 = ReplayCheck.Verify(Encoding.UTF8.GetBytes(snap));
        Check(p5.Problems.Count == 0 && p5.Notes.Any(n => n.Contains("checkpoint")) && !snap.Contains("\"pumpId\":2"),
              "WR5 쓰는 도중에도 파일은 임시 end로 닫혀 있고, 열린 Pump 구간은 다음 쓰기로 넘긴다",
              p5.Problems.Count > 0 ? string.Join(" | ", p5.Problems) : null);
        w5.Add(PumpRec(12, 2));
        snap = Snapshot(live, "\"pumpId\":2,\"observedGen\"");
        Check(ReplayCheck.Verify(Encoding.UTF8.GetBytes(snap)).Problems.Count == 0 && snap.Split('\n').Count(l => l.Contains("\"type\":\"end\"")) == 1,
              "WR5 다음 쓰기는 임시 end를 덮어써 end가 하나만 남는다");
        w5.Add(PumpRec(7, 3));
        w5.Close("normal", 5000);
        string liveText = Encoding.UTF8.GetString(live.ToArray());
        Check(EndOf(live).Contains("\"reason\":\"normal\"") && !liveText.Contains("\"pumpId\":3")
              && ReplayCheck.Verify(live.ToArray()).Problems.Count == 0,
              "WR5 정상 종료 때는 최종 end로 바뀌고, 닫는 순간 진행 중이던 Pump는 파일에 남기지 않는다");

        var stuck = new GateStream();
        var w4 = new ReplayWriter(stuck, OverlaySchedule.FormatRecord, warns.Add);
        w4.Add(HeaderRec());
        bool closed = w4.Close("normal", 200);
        Check(!closed, "WR4 종료 제한 시간 안에 flush가 안 끝나면 정상 end를 보장하지 않는다");
        Rejects(stuck.ToArray(), "WR4 그 파일은 재검사에서 거부된다", "end 없음");
        stuck.Open.Set();
    }
}
