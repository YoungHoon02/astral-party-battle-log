using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AstralPartyBattleLog.Log;
using AstralPartyBattleLog.UI;

// 재생 기록 검사·재생기. 규칙은 docs/TIMING-REPLAY-HANDOFF.md 4.5절·7절.
// 각 재생은 새 프로세스에서 돈다(스케줄러가 정적 상태라 같은 프로세스에서는 완전 초기 상태를 보장하지 못한다).
static class ReplayCheck
{
    public sealed class Rec
    {
        public int Index;
        public long Offset;
        public string Raw = "";
        public string Type = "";
        public long Seq;
        public JsonElement E;
    }

    public sealed class Parsed
    {
        public readonly List<Rec> Records = new();
        public readonly List<string> Problems = new();
        public readonly List<string> Notes = new();
        // 엄격 모드가 아니어도 재생을 멈춰야 하는 지점(깨진 JSON 등). 그 앞까지만 참고 재생한다.
        public int UsableCount;
    }

    static readonly HashSet<string> Compared = new() { "take", "signal-take", "decision", "diagnostic" };

    public static Parsed Verify(byte[] bytes)
    {
        var p = new Parsed();
        long offset = 0;
        int index = 0;
        bool broken = false;
        while (offset < bytes.Length)
        {
            int nl = Array.IndexOf(bytes, (byte)'\n', (int)offset);
            if (nl < 0)
            {
                p.Problems.Add($"잘린 파일: 마지막 줄에 줄바꿈이 없다 (byte {offset})");
                break;
            }
            string raw = Encoding.UTF8.GetString(bytes, (int)offset, nl - (int)offset);
            var rec = new Rec { Index = index, Offset = offset, Raw = raw };
            try
            {
                using JsonDocument doc = JsonDocument.Parse(raw);
                rec.E = doc.RootElement.Clone();
                rec.Type = rec.E.GetProperty("type").GetString() ?? "";
                rec.Seq = rec.E.GetProperty("recordSeq").GetInt64();
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                p.Problems.Add($"깨진 JSON: {index + 1}번째 줄 ({e.GetType().Name})");
                broken = true;
                break;
            }
            p.Records.Add(rec);
            offset = nl + 1;
            index++;
        }
        p.UsableCount = p.Records.Count;
        if (broken) return p;

        CheckHeader(p);
        CheckSequence(p);
        CheckEnd(p, bytes);
        CheckRefsAndPumps(p);
        return p;
    }

    static void CheckHeader(Parsed p)
    {
        if (p.Records.Count == 0 || p.Records[0].Type != "header")
        {
            p.Problems.Add("버전 헤더 없음");
            return;
        }
        JsonElement h = p.Records[0].E;
        if (h.GetProperty("format").GetInt64() != OverlaySchedule.ReplayFormat)
            p.Problems.Add($"모르는 기록 형식 {h.GetProperty("format")}");
        if (h.GetProperty("schedulerVersion").GetInt64() != OverlaySchedule.SchedulerVersion)
            p.Problems.Add($"모르는 스케줄러 버전 {h.GetProperty("schedulerVersion")} (재생기 {OverlaySchedule.SchedulerVersion})");
        if (h.GetProperty("start").GetString() != "clean")
            p.Problems.Add("중간 시작(start=mid): 시작 상태를 복원할 수 없다");
    }

    static void CheckSequence(Parsed p)
    {
        for (int i = 0; i < p.Records.Count; i++)
        {
            if (p.Records[i].Seq == i + 1) continue;
            long prev = i > 0 ? p.Records[i - 1].Seq : 0;
            p.Problems.Add(p.Records[i].Seq <= prev
                ? $"순번 중복/역행: {i + 1}번째 줄 recordSeq={p.Records[i].Seq}"
                : $"순번 누락: {i + 1}번째 줄 recordSeq={p.Records[i].Seq}");
            return;
        }
    }

    static void CheckEnd(Parsed p, byte[] bytes)
    {
        List<Rec> ends = p.Records.Where(r => r.Type == "end").ToList();
        if (ends.Count == 0)
        {
            p.Problems.Add("end 없음: 정상 경계에서 닫히지 않은 기록");
            return;
        }
        if (ends.Count > 1) p.Problems.Add($"end가 {ends.Count}개");
        Rec end = ends[^1];
        if (end.Index != p.Records.Count - 1) p.Problems.Add("end 뒤에 레코드가 있다");
        JsonElement e = end.E;
        if (e.GetProperty("previousCount").GetInt64() != end.Index)
            p.Problems.Add($"개수 불일치: end.previousCount={e.GetProperty("previousCount")} 실제 {end.Index}");
        if (e.GetProperty("previousBytes").GetInt64() != end.Offset)
            p.Problems.Add($"바이트 수 불일치: end.previousBytes={e.GetProperty("previousBytes")} 실제 {end.Offset}");
        string hash = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, (int)end.Offset))).ToLowerInvariant();
        if (e.GetProperty("sha256").GetString() != hash) p.Problems.Add("해시 불일치");
        if (e.GetProperty("dropped").GetInt64() != 0) p.Problems.Add($"기록 유실 dropped={e.GetProperty("dropped")}");
        if (e.GetProperty("ioFailed").GetBoolean()) p.Problems.Add("쓰기 실패(ioFailed)");
        if (e.GetProperty("overlap").GetBoolean()) p.Problems.Add("세대 변경과 처리 구간의 경합(overlap)");
        string reason = e.GetProperty("reason").GetString() ?? "";
        if (reason == ReplayWriter.Checkpoint)
            p.Notes.Add("종료 경계 미확인(checkpoint): 게임 종료 처리가 기록되지 않아 마지막 임시 end까지의 완전한 앞부분만 재생한다");
        else if (reason is not ("normal" or "unload")) p.Problems.Add($"불완전 종료 reason={reason}");
    }

    static long? Opt(JsonElement e, string name) =>
        e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetInt64();

    static void CheckRefsAndPumps(Parsed p)
    {
        var items = new HashSet<long>();
        var signals = new HashSet<long>();
        var controls = new HashSet<long>();
        var takenItems = new HashSet<long>();
        var takenSignals = new HashSet<long>();
        long? open = null;
        void Bad(Rec r, string what) => p.Problems.Add($"{r.Index + 1}번째 줄({r.Type}): {what}");

        foreach (Rec r in p.Records)
        {
            JsonElement e = r.E;
            switch (r.Type)
            {
                case "item":
                    if (!items.Add(e.GetProperty("itemId").GetInt64())) Bad(r, "itemId 중복");
                    if (Opt(e, "parentControlId") is { } parent && !controls.Contains(parent)) Bad(r, "모르는 parentControlId");
                    break;
                case "signal":
                    if (!signals.Add(e.GetProperty("signalId").GetInt64())) Bad(r, "signalId 중복");
                    break;
                case "control":
                    if (!controls.Add(e.GetProperty("controlId").GetInt64())) Bad(r, "controlId 중복");
                    break;
                case "pump-begin":
                    if (open is not null) Bad(r, $"Pump {open} 안에서 새 Pump 시작");
                    open = e.GetProperty("pumpId").GetInt64();
                    break;
                case "pump-end":
                    if (open != e.GetProperty("pumpId").GetInt64()) Bad(r, "짝 없는 pump-end");
                    if (e.GetProperty("overlap").GetBoolean()) Bad(r, "세대 변경과 겹친 Pump");
                    open = null;
                    break;
                case "take":
                    if (open is null || open != Opt(e, "pumpId")) Bad(r, "Pump 밖의 take");
                    if (!items.Contains(e.GetProperty("itemRef").GetInt64())) Bad(r, "모르는 itemRef");
                    else if (!takenItems.Add(e.GetProperty("itemRef").GetInt64())) Bad(r, "같은 입력을 두 번 꺼냄");
                    break;
                case "signal-take":
                    if (open is null || open != Opt(e, "pumpId")) Bad(r, "Pump 밖의 signal-take");
                    if (!signals.Contains(e.GetProperty("signalRef").GetInt64())) Bad(r, "모르는 signalRef");
                    else if (!takenSignals.Add(e.GetProperty("signalRef").GetInt64())) Bad(r, "같은 신호를 두 번 꺼냄");
                    break;
                case "decision":
                    if (!items.Contains(e.GetProperty("itemRef").GetInt64())) Bad(r, "모르는 itemRef");
                    if (Opt(e, "causeRef") is { } cause && !signals.Contains(cause)) Bad(r, "모르는 causeRef");
                    if (Opt(e, "leaderItemRef") is { } leader && !items.Contains(leader)) Bad(r, "모르는 leaderItemRef");
                    break;
                case "diagnostic":
                    if (Opt(e, "itemRef") is { } di && !items.Contains(di)) Bad(r, "모르는 itemRef");
                    if (Opt(e, "signalRef") is { } ds && !signals.Contains(ds)) Bad(r, "모르는 signalRef");
                    break;
            }
        }
        if (open is not null) p.Problems.Add($"미완성 Pump {open}: pump-end 없이 끝났다");
    }

    sealed class MemorySink : IReplaySink
    {
        public readonly List<string> Stream = new();
        readonly StringBuilder _sb = new();

        public void Add(ReplayRecord r)
        {
            _sb.Clear().Append('{');
            OverlaySchedule.FormatRecord(_sb, r);
            string text = _sb.ToString();
            int typeEnd = text.IndexOf('"', 9);
            if (Compared.Contains(text.Substring(9, typeEnd - 9))) Stream.Add(text);
        }
    }

    static string Canonical(Rec r) => "{" + r.Raw.Substring(r.Raw.IndexOf(',') + 1);

    // 새 프로세스에서 부른다. 반환: 0 일치, 1 불일치, 2 거부.
    public static int Run(string path, bool lenient)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Parsed p = Verify(bytes);
        Console.WriteLine($"기록: {Path.GetFileName(path)} ({bytes.Length} bytes, {p.Records.Count} records)");
        foreach (string note in p.Notes) Console.WriteLine("참고: " + note);
        if (p.Problems.Count > 0)
        {
            Console.WriteLine(lenient ? "엄격 검사 실패(참고 재생으로 계속):" : "엄격 재생 거부:");
            foreach (string s in p.Problems.Take(10)) Console.WriteLine("  - " + s);
            if (!lenient) return 2;
            Console.WriteLine("※ 불완전 재생(참고용). 엄격 재현 성공이 아니다.");
        }

        var sink = new MemorySink();
        string? stop = Drive(p.Records.Take(p.UsableCount).ToList(), sink, out List<string> expected,
                             out Dictionary<long, OverlaySchedule.ItemIn> items, out int marks);

        int n = Math.Min(expected.Count, sink.Stream.Count);
        int matched = 0;
        while (matched < n && expected[matched] == sink.Stream[matched]) matched++;
        bool ok = stop is null && matched == expected.Count && matched == sink.Stream.Count;
        Console.WriteLine(ok
            ? $"결정 일치: take·신호·결정·진단 {matched}건이 실기록과 같다"
            : $"결정 불일치: {matched}/{expected.Count}건까지 일치 (재생 {sink.Stream.Count}건)");
        if (stop is not null) Console.WriteLine("  재생 중단: " + stop);
        if (!ok && matched < n)
        {
            Console.WriteLine("  기록: " + expected[matched]);
            Console.WriteLine("  재생: " + sink.Stream[matched]);
        }
        Summarize(expected, items.Keys, marks);
        return ok && p.Problems.Count == 0 ? 0 : 1;
    }

    // 기록된 입력·시각을 실제 스케줄러에 주입한다. 결정은 sink로, 기록의 결정은 expected로 모은다.
    static string? Drive(List<Rec> recs, MemorySink sink, out List<string> expected,
                         out Dictionary<long, OverlaySchedule.ItemIn> items, out int marks)
    {
        OverlaySchedule.ReplayPort.Attach(sink);
        items = new Dictionary<long, OverlaySchedule.ItemIn>();
        var signals = new Dictionary<long, OverlaySchedule.SignalIn>();
        expected = new List<string>();
        marks = 0;
        string? stop = null;

        for (int i = 0; i < recs.Count && stop is null; i++)
        {
            Rec r = recs[i];
            JsonElement e = r.E;
            if (Compared.Contains(r.Type)) expected.Add(Canonical(r));
            switch (r.Type)
            {
                case "init":
                    OverlaySchedule.ReplayPort.Init(e.GetProperty("nowUs").GetInt64(), (int)e.GetProperty("speedBits").GetInt64(),
                                                    e.GetProperty("enabled").GetBoolean(), e.GetProperty("maxLagUs").GetInt64());
                    break;
                case "tick":
                    OverlaySchedule.ReplayPort.Clock(e.GetProperty("nowUs").GetInt64(), (int)e.GetProperty("speedBits").GetInt64());
                    break;
                case "item":
                    items[e.GetProperty("itemId").GetInt64()] = new OverlaySchedule.ItemIn(
                        e.GetProperty("itemId").GetInt64(), e.GetProperty("signal").GetString()!, e.GetProperty("kind").GetString()!,
                        e.GetProperty("group").GetInt64(), (int)e.GetProperty("units").GetInt64(), (int)e.GetProperty("detail").GetInt64(),
                        (int)e.GetProperty("round").GetInt64(), e.GetProperty("receivedUs").GetInt64(),
                        (int)e.GetProperty("speedBits").GetInt64(), (int)e.GetProperty("gen").GetInt64());
                    break;
                case "signal":
                    signals[e.GetProperty("signalId").GetInt64()] = new OverlaySchedule.SignalIn(
                        e.GetProperty("signalId").GetInt64(), e.GetProperty("atUs").GetInt64(), (int)e.GetProperty("gen").GetInt64(),
                        e.GetProperty("kind").GetString()!, (int)e.GetProperty("pip").GetInt64(), e.GetProperty("on").GetBoolean(),
                        e.GetProperty("instanceId").GetInt64());
                    break;
                case "control":
                    OverlaySchedule.ReplayPort.Control(e.GetProperty("op").GetString() == "reset", (int)e.GetProperty("genAfter").GetInt64());
                    break;
                case "env":
                    string op = e.GetProperty("op").GetString()!;
                    if (op == "fallback") OverlaySchedule.FallBackToModel();
                    else if (op == "forget-banner") OverlaySchedule.ForgetBanner();
                    break;
                case "mark":
                    marks++;
                    break;
                case "pump-begin":
                    int endAt = recs.FindIndex(i + 1, x => x.Type is "pump-end" or "pump-begin");
                    if (endAt < 0 || recs[endAt].Type != "pump-end")
                    {
                        stop = $"{i + 1}번째 줄 Pump가 끝나지 않아 여기서 멈춘다";
                        break;
                    }
                    var takeItems = new List<OverlaySchedule.ItemIn>();
                    var takeSignals = new List<OverlaySchedule.SignalIn>();
                    for (int k = i + 1; k < endAt; k++)
                    {
                        if (recs[k].Type == "take") takeItems.Add(items[recs[k].E.GetProperty("itemRef").GetInt64()]);
                        else if (recs[k].Type == "signal-take") takeSignals.Add(signals[recs[k].E.GetProperty("signalRef").GetInt64()]);
                    }
                    stop = OverlaySchedule.ReplayPort.Pump(e.GetProperty("pumpId").GetInt64(), e.GetProperty("nowUs").GetInt64(),
                                                           e.GetProperty("mode").GetString() == "gated",
                                                           (int)e.GetProperty("observedGen").GetInt64(), takeItems, takeSignals);
                    break;
            }
        }
        return stop;
    }

    // 비교 모드: 다른 스케줄러 버전의 기록에 현재 스케줄러를 돌려 줄마다 공개 시각이 어떻게 바뀌는지 본다.
    // 입력(수신·신호·시각)은 결정과 무관하게 기록된 그대로라 가정 실험이 된다. 엄격 재현 성공으로 표시하지 않는다.
    // 원래 자기 신호·창 닫힘으로 공개된 줄이 그 신호보다 먼저 나가면 따로 센다. 검토 대상일 뿐 조기 표시 판정이
    // 아니다 — 과거 신호가 실제 화면보다 늦었다면 정상적인 앞당김도 여기 들어간다. 화면 정확도는 독립 증거가 있을 때만.
    public static int Compare(string path, bool lenient)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Parsed p = Verify(bytes);
        List<string> blocking = p.Problems.Where(s => !s.StartsWith("모르는 스케줄러 버전")).ToList();
        string recorded = p.Records.Count > 0 && p.Records[0].Type == "header"
            ? p.Records[0].E.GetProperty("schedulerVersion").ToString() : "?";
        Console.WriteLine($"비교: {Path.GetFileName(path)} 기록 버전 {recorded} → 현재 {OverlaySchedule.SchedulerVersion} (가정 실험, 엄격 재현 아님)");
        foreach (string note in p.Notes) Console.WriteLine("참고: " + note);
        if (blocking.Count > 0)
        {
            Console.WriteLine(lenient ? "검사 실패(참고 비교로 계속):" : "비교 거부:");
            foreach (string s in blocking.Take(10)) Console.WriteLine("  - " + s);
            if (!lenient) return 2;
        }

        var sink = new MemorySink();
        string? stop = Drive(p.Records.Take(p.UsableCount).ToList(), sink, out List<string> expected,
                             out Dictionary<long, OverlaySchedule.ItemIn> items, out _);
        if (stop is not null) Console.WriteLine("  재생 중단: " + stop);
        Dictionary<long, (long At, string Why)> before = Releases(expected), after = Releases(sink.Stream);

        int earlier = 0, later = 0, beforeSignal = 0, missing = 0;
        long saved = 0;
        var moved = new List<(long Id, long Old, long New, string OldWhy, string NewWhy)>();
        foreach ((long id, (long at, string why)) in before)
        {
            if (!after.TryGetValue(id, out (long At, string Why) now))
            {
                missing++;
                continue;
            }
            if (now.At == at) continue;
            moved.Add((id, at, now.At, why, now.Why));
            if (now.At < at)
            {
                earlier++;
                saved += at - now.At;
                if (why is "own-signal" or "window-close") beforeSignal++;
            }
            else later++;
        }
        long MaxWait(Dictionary<long, (long At, string Why)> rel) =>
            rel.Count == 0 ? 0 : rel.Max(kv => kv.Value.At - items[kv.Key].ReceivedUs);

        Console.WriteLine($"공개 줄 {before.Count}건 중 시각이 바뀐 줄 {moved.Count}건: 앞당김 {earlier}건(합계 {saved / 1e6:0.0}초), "
                          + $"늦어짐 {later}건, 새 결과에 없음 {missing}건");
        Console.WriteLine($"최대 대기: {MaxWait(before) / 1e6:0.0}초 → {MaxWait(after) / 1e6:0.0}초");
        Console.WriteLine($"기존 신호 시각보다 빠른 줄: {beforeSignal}건 (검토 대상, 조기 표시 판정 아님)");
        Console.WriteLine("화면 정확도: 판정 불가 — 기록에 독립 증거(공개 시점 구간·정렬 오차)가 없다");
        foreach (var m in moved.OrderBy(m => m.New - m.Old).Take(10))
        {
            OverlaySchedule.ItemIn it = items[m.Id];
            Console.WriteLine($"  item {m.Id,5} {it.Kind,-9} 수신 {it.ReceivedUs / 1e6,8:0.00}  {m.OldWhy,-13} {(m.Old - it.ReceivedUs) / 1e6,6:0.00}초"
                              + $" → {m.NewWhy,-13} {(m.New - it.ReceivedUs) / 1e6,6:0.00}초");
        }
        return missing == 0 && stop is null ? 0 : 1;
    }

    static Dictionary<long, (long At, string Why)> Releases(List<string> stream)
    {
        var map = new Dictionary<long, (long, string)>();
        foreach (string s in stream)
        {
            if (!s.Contains("\"action\":\"release\"") || s.Contains("\"output\":\"none\"")) continue;
            using JsonDocument doc = JsonDocument.Parse(s);
            JsonElement e = doc.RootElement;
            map[e.GetProperty("itemRef").GetInt64()] = (e.GetProperty("releasedUs").GetInt64(), e.GetProperty("reason").GetString()!);
        }
        return map;
    }

    // 지표는 두 축으로 나눈다. 해제 근거는 결정 기록에서, 화면 정확도는 독립 증거가 있을 때만 판정한다.
    static void Summarize(List<string> stream, IEnumerable<long> itemIds, int marks)
    {
        var byAction = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var diags = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var taken = new HashSet<long>();
        var finished = new HashSet<long>();
        foreach (string s in stream)
        {
            using JsonDocument doc = JsonDocument.Parse(s);
            JsonElement e = doc.RootElement;
            switch (e.GetProperty("type").GetString())
            {
                case "take":
                    taken.Add(e.GetProperty("itemRef").GetInt64());
                    break;
                case "decision":
                    string key = $"{e.GetProperty("action").GetString()}/{e.GetProperty("reason").GetString()}";
                    byAction[key] = byAction.GetValueOrDefault(key) + 1;
                    if (e.GetProperty("action").GetString() is "release" or "drop") finished.Add(e.GetProperty("itemRef").GetInt64());
                    break;
                case "diagnostic":
                    string kind = e.GetProperty("kind").GetString()!;
                    diags[kind] = diags.GetValueOrDefault(kind) + 1;
                    break;
            }
        }
        Console.WriteLine("해제 근거: " + (byAction.Count == 0 ? "-" : string.Join(", ", byAction.Select(kv => $"{kv.Key}={kv.Value}"))));
        Console.WriteLine($"화면 정확도: 판정 불가 — v1 기록에는 독립 증거(공개 시점 구간·정렬 오차)가 없다. F10 표식 {marks}건은 참고로만 병기");
        Console.WriteLine("진단 사건: " + (diags.Count == 0 ? "-" : string.Join(", ", diags.Select(kv => $"{kv.Key}={kv.Value}"))));
        int pending = taken.Count(id => !finished.Contains(id));
        int unconsumed = itemIds.Count(id => !taken.Contains(id));
        Console.WriteLine($"미관측: 종료 때 대기 중 {pending}건, 꺼내지 않은 입력 {unconsumed}건 (최종 출력을 추측하지 않는다)");
    }
}
