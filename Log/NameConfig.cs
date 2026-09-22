using System;
using System.Collections.Generic;
using System.Linq;
using AstralPartyBattleLog.Proto;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// 설정 에셋에서 이름표 행을 만든다. 오프라인 대조를 위해 Unity에 의존하지 않는다
/// (<c>docs/ARCHITECTURE.md</c> "만드는 곳이 둘이다").
/// </summary>
internal static class NameConfig
{
    private static readonly (string Kind, string Info, int NameField, string Strings)[] Tables =
    {
        ("card", "Card", 2, "STRCard"),
        ("skill", "Skill", 4, "STRSkill"),
        // NameId(14). DescId(15)를 쓰면 이름 대신 설명문이 나온다.
        ("buff", "Buff", 14, "STRBuff"),
        ("event", "Event", 5, "STREvent"),
        ("land", "Land", 5, "STRLand"),
        ("relic", "Relic", 7, "STRRelic"),
        ("monster", "Monster", 3, "STRMonster"),
        ("character", "Character", 4, "STRCharacter"),
    };

    private static readonly (string Kind, string Info, int Field, string[] Values, string Fallback)[] Enums =
    {
        // CardInfoConfigure.CardType — 게임은 공격/방어/그 외 셋으로만 칠한다.
        ("cardtype", "Card", 9, new[] { "", "Attack", "Defend" }, "Other"),
        // RelicInfoConfigure.RelicQualityType
        ("relicgrade", "Relic", 2, new[] { "", "Blue", "Purple", "Orange" }, "None"),
    };

    // 패시브 필드는 packed repeated sfixed32라 숫자로 읽으면 통째로 건너뛴다.
    private static readonly (string Kind, string Info, int[] Active, int[] Packed)[] Skills =
    {
        ("charskill", "Character", new[] { 18, 19 }, new[] { 20, 21 }),
        ("monskill", "Monster", new[] { 17 }, new[] { 18 }),
    };

    // 한국어(6) → 영어(3) → 간체(2). 한글패치 INT 빌드는 영어 슬롯에 한국어를 넣는다.
    private static readonly int[] LanguageOrder = { 6, 3, 2 };

    public static HashSet<string> Required()
    {
        var wanted = new HashSet<string>();
        foreach (var t in Tables) { wanted.Add(t.Info); wanted.Add(t.Strings); }
        foreach (var e in Enums) wanted.Add(e.Info);
        foreach (var sk in Skills) wanted.Add(sk.Info);
        return wanted;
    }

    public static List<(string Kind, long Id, string Text)> Build(Dictionary<string, byte[]> assets)
    {
        var rows = new List<(string Kind, long Id, string Text)>();

        foreach ((string kind, string info, int nameField, string strings) in Tables)
        {
            if (!assets.ContainsKey(info) || !assets.ContainsKey(strings)) continue;
            Dictionary<long, long> ids = ParseInfo(assets[info], nameField);
            Dictionary<long, string> texts = ParseStrings(assets[strings]);
            foreach ((long id, long nameId) in ids.OrderBy(kv => kv.Key))
                if (texts.TryGetValue(nameId, out string? text))
                    rows.Add((kind, id, Clean(text)));
        }

        foreach ((string kind, string info, int field, string[] values, string fallback) in Enums)
            if (assets.ContainsKey(info))
            foreach ((long id, long value) in ParseValues(assets[info], field).OrderBy(kv => kv.Key))
                rows.Add((kind, id, value > 0 && value < values.Length ? values[value] : fallback));

        var skillNames = rows.Where(r => r.Kind == "skill").ToDictionary(r => r.Id, r => r.Text);
        foreach ((string kind, string info, int[] active, int[] packed) in Skills)
            if (assets.ContainsKey(info))
            foreach ((long id, List<long> list) in ParseSkills(assets[info], active, packed).OrderBy(kv => kv.Key))
            {
                var labels = new List<string>();
                foreach (long sid in list)
                    if (skillNames.TryGetValue(sid, out string? n) && !labels.Contains(n))
                        labels.Add(n);
                if (labels.Count > 0) rows.Add((kind, id, string.Join(", ", labels)));
            }

        return rows;
    }

    private static Dictionary<long, long> ParseInfo(byte[] data, int nameField)
    {
        var map = new Dictionary<long, long>();
        foreach (ProtoReader item in Items(data))
        {
            (long id, long value) = TwoNumbers(item, nameField);
            if (id != 0 && value != 0) map[id] = value;
        }
        return map;
    }

    private static Dictionary<long, long> ParseValues(byte[] data, int field)
    {
        var map = new Dictionary<long, long>();
        foreach (ProtoReader item in Items(data))
        {
            (long id, long value) = TwoNumbers(item, field);
            if (id != 0) map[id] = value;
        }
        return map;
    }

    private static (long Id, long Value) TwoNumbers(ProtoReader item, int field)
    {
        long id = 0, value = 0;
        while (item.NextField(out int f, out int w))
        {
            if (w == ProtoReader.WireLength) { if (!item.Skip(w)) break; continue; }
            if (!item.TryReadNumber(w, out long v)) break;
            if (f == 1) id = v;
            else if (f == field) value = v;
        }
        return (id, value);
    }

    private static Dictionary<long, string> ParseStrings(byte[] data)
    {
        var map = new Dictionary<long, string>();
        foreach (ProtoReader item in Items(data))
        {
            long id = 0;
            var texts = new Dictionary<int, string>();
            while (item.NextField(out int f, out int w))
            {
                if (w == ProtoReader.WireLength)
                {
                    if (!item.TryReadString(out string s)) break;
                    texts[f] = s;
                    continue;
                }
                if (!item.TryReadNumber(w, out long v)) break;
                if (f == 1) id = v;
            }
            if (id == 0) continue;
            foreach (int slot in LanguageOrder)
                if (texts.TryGetValue(slot, out string? t) && t.Trim().Length > 0)
                {
                    map[id] = t.Trim();
                    break;
                }
        }
        return map;
    }

    private static Dictionary<long, List<long>> ParseSkills(byte[] data, int[] active, int[] packed)
    {
        var map = new Dictionary<long, List<long>>();
        foreach (ProtoReader item in Items(data))
        {
            long id = 0;
            var skills = new List<long>();
            while (item.NextField(out int f, out int w))
            {
                if (w == ProtoReader.WireLength)
                {
                    if (!item.TryReadMessage(out ProtoReader chunk)) break;
                    if (Array.IndexOf(packed, f) >= 0)
                        while (chunk.HasMore && chunk.TryReadFixed32(out long sid))
                            if (sid != 0 && !skills.Contains(sid)) skills.Add(sid);
                    continue;
                }
                if (!item.TryReadNumber(w, out long v)) break;
                if (f == 1) id = v;
                else if (Array.IndexOf(active, f) >= 0 && v != 0 && !skills.Contains(v)) skills.Add(v);
            }
            if (id != 0 && skills.Count > 0) map[id] = skills;
        }
        return map;
    }

    private static IEnumerable<ProtoReader> Items(byte[] data)
    {
        var r = new ProtoReader(data, 0, data.Length);
        while (r.NextField(out int f, out int w))
        {
            if (f == 1 && w == ProtoReader.WireLength)
            {
                if (!r.TryReadMessage(out ProtoReader item)) yield break;
                yield return item;
            }
            else if (!r.Skip(w))
            {
                yield break;
            }
        }
    }

    private static string Clean(string text) =>
        text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
