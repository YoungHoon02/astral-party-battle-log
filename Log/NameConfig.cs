using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
        // CardInfoConfigure.CardType — 게임은 공격/방어/그 외 셋으로만 칠한다. 건물(9)은 줄 머리말만 다르게 쓴다.
        ("cardtype", "Card", 9, new[] { "", "Attack", "Defend", "", "", "", "", "", "", "Build" }, "Other"),
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

    // 빠른 대화는 문구 id를 그대로 보내고, 마커는 마커 id를 보내 Chat.Marks(3)에서 문구 id를 찾는다.
    private const string ChatInfo = "Chat";
    private const string ChatStrings = "STRChat";
    private const int ChatMarkList = 3;
    private const int ChatMarkTextField = 2;

    // 표 구성이 바뀌면 올린다. 옛 names.tsv는 다음 실행에 다시 만들어진다.
    public const int Schema = 5;

    // 건물 이름은 STRBuild(BuildInfoConfigure.NameID=2)에서 읽고, 없으면 그 건물을 놓는 카드의 이름을 빌린다
    // (CardInfoConfigure.Params 둘째 값이 건물 id, sint32 zigzag packed). STRBuild는 2026-11-12에야 확인한 표라
    // 없어도 이름표를 다시 만들 수 있게 필수 목록에서 뺐다.
    private const string BuildInfo = "Build";
    private const string BuildStrings = "STRBuild";
    private const int BuildNameField = 2;
    private const int CardParams = 20;
    private const int CardBuildingIndex = 1;

    // PvE 핑에만 쓰는 표라 필수 목록에서 뺐다. 없어도 다른 이름은 그대로 만든다.
    // 이벤트 카드: MapEvent.MapEventCards(3) {1:Id, 2:NameID} → STRMapEvent
    private const string MapEventInfo = "MapEvent";
    private const string MapEventStrings = "STRMapEvent";
    private const int MapEventCardList = 3;
    private const int MapEventCardNameField = 2;

    // 임무: PVEMission.Infos {1:Id, 4:MissionDescID, 5:map<int, {1:packed Values}> MissionTargetConfigs,
    // 7:MissionTarget, 8:MissionParam} → STRPVEMission
    private const string MissionInfo = "PVEMission";
    private const string MissionStrings = "STRPVEMission";

    // PVEMissionTargetType: 처치(1)는 몬스터 이름을, 누적 횟수류는 첫 값을 제목 칸에 채운다.
    private const int KillMonsterTarget = 1;
    private static readonly int[] CountTargets = { 5, 6, 7, 8, 9, 10 };

    public static HashSet<string> Required()
    {
        var wanted = new HashSet<string> { ChatInfo, ChatStrings, BuildInfo };
        foreach (var t in Tables) { wanted.Add(t.Info); wanted.Add(t.Strings); }
        foreach (var e in Enums) wanted.Add(e.Info);
        foreach (var sk in Skills) wanted.Add(sk.Info);
        return wanted;
    }

    public static HashSet<string> Wanted()
    {
        var wanted = Required();
        wanted.UnionWith(new[] { BuildStrings, MapEventInfo, MapEventStrings, MissionInfo, MissionStrings });
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
                rows.Add((kind, id, value > 0 && value < values.Length && values[value].Length > 0 ? values[value] : fallback));

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

        var named = new HashSet<long>();
        if (assets.ContainsKey(BuildInfo) && assets.ContainsKey(BuildStrings))
        {
            Dictionary<long, string> texts = ParseStrings(assets[BuildStrings]);
            foreach ((long id, long nameId) in ParseInfo(assets[BuildInfo], BuildNameField).OrderBy(kv => kv.Key))
                if (texts.TryGetValue(nameId, out string? text) && named.Add(id))
                    rows.Add(("building", id, Clean(text)));
        }
        if (assets.ContainsKey(BuildInfo) && assets.ContainsKey("Card"))
        {
            var buildings = new HashSet<long>(ParseValues(assets[BuildInfo], 2).Keys);
            var cardNames = rows.Where(r => r.Kind == "card").ToDictionary(r => r.Id, r => r.Text);
            foreach (ProtoReader item in Items(assets["Card"]))
                if (CardBuilding(item) is { } pair && buildings.Contains(pair.Building)
                    && cardNames.TryGetValue(pair.Card, out string? cardName) && named.Add(pair.Building))
                    rows.Add(("building", pair.Building, cardName));
        }

        if (assets.ContainsKey(ChatStrings))
        {
            Dictionary<long, string> texts = ParseStrings(assets[ChatStrings]);
            foreach ((long id, string text) in texts.OrderBy(kv => kv.Key))
                rows.Add(("chat", id, Clean(text)));
            if (assets.ContainsKey(ChatInfo))
                foreach ((long id, long chatId) in ParseInfo(assets[ChatInfo], ChatMarkTextField, ChatMarkList).OrderBy(kv => kv.Key))
                    if (texts.TryGetValue(chatId, out string? text))
                        rows.Add(("chatmark", id, Clean(text)));
        }

        if (assets.ContainsKey(MapEventInfo) && assets.ContainsKey(MapEventStrings))
        {
            Dictionary<long, string> texts = ParseStrings(assets[MapEventStrings]);
            foreach ((long id, long nameId) in ParseInfo(assets[MapEventInfo], MapEventCardNameField, MapEventCardList).OrderBy(kv => kv.Key))
                if (texts.TryGetValue(nameId, out string? text))
                    rows.Add(("eventcard", id, Clean(text)));
        }

        if (assets.ContainsKey(MissionInfo) && assets.ContainsKey(MissionStrings))
        {
            Dictionary<long, string> texts = ParseStrings(assets[MissionStrings]);
            var heroes = rows.Where(r => r.Kind == "character").ToDictionary(r => r.Id, r => r.Text);
            var monsters = rows.Where(r => r.Kind == "monster").ToDictionary(r => r.Id, r => r.Text);
            // CharacterHandle.GetCharacterName처럼 영웅 표를 먼저 본다.
            string? CharacterName(long id) =>
                heroes.TryGetValue(id, out string? h) ? h : monsters.TryGetValue(id, out string? m) ? m : null;
            foreach (Mission mission in Items(assets[MissionInfo]).Select(ReadMission).OrderBy(m => m.Id))
                if (mission.Id != 0 && texts.TryGetValue(mission.DescId, out string? desc))
                    rows.Add(("mission", mission.Id, Clean(MissionTitle(mission, desc, CharacterName))));
        }

        return rows;
    }

    private sealed class Mission
    {
        public long Id, DescId;
        public readonly List<long> Targets = new(), Params = new();
        public readonly List<(long Key, List<long> Values)> Configs = new();
    }

    private static readonly Regex Placeholder = new(@"\{(\d+)\}");

    // MapMissionTargetData.GetTitle을 설정만으로 따라 한다.
    // MissionTargetConfigs가 있으면 IPVEMissionTargetConfigs.GetTitleByMissionTargetConfigs와 같다.
    // 없는 옛 임무는 서버가 보내는 목표 종류에 따라 갈리는데(수집·변신은 MissionParam, 보스 체력은 대상 이름,
    // 그 외는 문구 뒤에 대상 이름) 그 값은 설정에 없다. 그래서 대상이 모두 이름이 있으면 이름을, 아니면 MissionParam을 쓴다.
    private static string MissionTitle(Mission m, string desc, Func<long, string?> characterName)
    {
        int slots = Placeholder.Matches(desc).Count;
        var args = new List<string>();
        if (slots == 0)
        {
            // 대상이 여럿인 옛 임무(320880 등)는 게임도 문구만 쓴다.
            return m.Configs.Count == 0 && m.Targets.Count == 1 && characterName(m.Targets[0]) is { } single
                ? $"{desc} {single}"
                : desc;
        }

        if (m.Configs.Count > 0)
        {
            foreach ((long key, List<long> values) in m.Configs)
            {
                if (values.Count == 0) continue;
                if (key == KillMonsterTarget)
                    args.AddRange(values.Skip(1).Select(id => characterName(id) ?? "?"));
                else if (Array.IndexOf(CountTargets, (int)key) >= 0)
                    args.Add(values[0].ToString());
            }
        }
        else
        {
            var names = m.Targets.Take(slots).Select(characterName).ToList();
            if (names.Count == slots && names.All(n => n is not null)) args.AddRange(names!);
            else if (m.Params.Count > 0) args.Add(m.Params[0].ToString());
        }

        return Placeholder.Replace(desc, match =>
            int.TryParse(match.Groups[1].Value, out int i) && i < args.Count ? args[i] : "?");
    }

    private static Mission ReadMission(ProtoReader item)
    {
        var m = new Mission();
        while (item.NextField(out int f, out int w))
        {
            bool ok = f switch
            {
                5 when w == ProtoReader.WireLength => ReadTargetConfig(ref item, m.Configs),
                7 => ReadInts(ref item, w, m.Targets),
                8 => ReadInts(ref item, w, m.Params),
                _ when w == ProtoReader.WireLength => item.Skip(w),
                _ => ReadId(ref item, w, f, m),
            };
            if (!ok) break;
        }
        return m;
    }

    private static bool ReadId(ref ProtoReader item, int wire, int field, Mission m)
    {
        if (!item.TryReadNumber(wire, out long v)) return false;
        if (field == 1) m.Id = v;
        else if (field == 4) m.DescId = v;
        return true;
    }

    // map<int, {1: repeated sfixed32 Values}> 항목 하나
    private static bool ReadTargetConfig(ref ProtoReader item, List<(long Key, List<long> Values)> into)
    {
        if (!item.TryReadMessage(out ProtoReader entry)) return false;
        long key = 0;
        var values = new List<long>();
        while (entry.NextField(out int f, out int w))
        {
            if (f == 2 && w == ProtoReader.WireLength)
            {
                if (!entry.TryReadMessage(out ProtoReader val)) return false;
                while (val.NextField(out int vf, out int vw))
                    if (!(vf == 1 ? ReadInts(ref val, vw, values) : val.Skip(vw))) return false;
            }
            else if (w == ProtoReader.WireLength)
            {
                if (!entry.Skip(w)) return false;
            }
            else if (!entry.TryReadNumber(w, out long v))
            {
                return false;
            }
            else if (f == 1)
            {
                key = v;
            }
        }
        into.Add((key, values));
        return true;
    }

    // repeated sfixed32는 packed(길이형)로 오지만 파서는 낱개 모양도 받는다.
    private static bool ReadInts(ref ProtoReader r, int wire, List<long> into)
    {
        if (wire != ProtoReader.WireLength)
        {
            if (!r.TryReadNumber(wire, out long one)) return false;
            into.Add(one);
            return true;
        }
        if (!r.TryReadMessage(out ProtoReader chunk)) return false;
        while (chunk.HasMore && chunk.TryReadFixed32(out long v)) into.Add(v);
        return true;
    }

    private static (long Card, long Building)? CardBuilding(ProtoReader item)
    {
        long id = 0;
        var values = new List<long>();
        while (item.NextField(out int f, out int w))
        {
            if (f == CardParams && w == ProtoReader.WireLength)
            {
                if (!item.TryReadMessage(out ProtoReader chunk)) return null;
                while (chunk.HasMore && chunk.TryReadVarint(out ulong raw))
                    values.Add((long)(raw >> 1) ^ -(long)(raw & 1));
            }
            else if (w == ProtoReader.WireLength)
            {
                if (!item.Skip(w)) return null;
            }
            else if (!item.TryReadNumber(w, out long v))
            {
                return null;
            }
            else if (f == 1)
            {
                id = v;
            }
        }
        return id != 0 && values.Count > CardBuildingIndex ? (id, values[CardBuildingIndex]) : null;
    }

    private static Dictionary<long, long> ParseInfo(byte[] data, int nameField, int listField = 1)
    {
        var map = new Dictionary<long, long>();
        foreach (ProtoReader item in Items(data, listField))
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

    private static IEnumerable<ProtoReader> Items(byte[] data, int listField = 1)
    {
        var r = new ProtoReader(data, 0, data.Length);
        while (r.NextField(out int f, out int w))
        {
            if (f == listField && w == ProtoReader.WireLength)
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
