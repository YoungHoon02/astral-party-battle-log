using System.Collections.Generic;
using AstralPartyBattleLog.Proto;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// playerId → 사람이 읽는 이름. 같은 종류가 둘 이상일 때만 번호가 붙는다
/// (<c>마법 찻주전자1</c>, <c>마법 찻주전자2</c>).
///
/// 출처는 <c>Room {9: players, 10: monsters}</c>과 <c>MonsterRefreshS2C</c>다.
/// <c>Player</c>에서 읽는 필드는 <c>Id(1) / Nick(2) / Slot(6) / IsBot(20)</c>, 그리고
/// <c>Hero(10)</c>의 <b><c>HeroId</c>(2) 하나뿐</b>이다 — <c>Hero</c>는 손패(<c>Cards</c>, 8)도
/// 품고 있으므로 거기서 다른 필드를 읽는 코드를 추가하지 말 것.
/// </summary>
internal sealed class Roster
{
    private sealed class Entry
    {
        public string Kind = "";
        public int Ordinal;       // 같은 Kind 안에서 1부터
        public bool IsMonster;
        public int Slot = -1;
        public long HeroId;       // 스킬 목록을 찾는 키 (charskill / monskill)
    }

    private readonly Dictionary<long, Entry> _entries = new();

    /// <summary>
    /// 종류별 인원수. 1명이면 번호를 붙이지 않는다. 키에 몹 여부를 섞는 이유는 PvE 적이
    /// 플레이어 캐릭터를 쓰기도 해서다 — 한 통에 세면 그 캐릭터를 고른 플레이어까지
    /// 덩달아 번호가 붙는다.
    /// </summary>
    private readonly Dictionary<(bool IsMonster, string Kind), int> _kindCount = new();

    /// <summary>
    /// 마지막 <see cref="Update"/>에서 바뀐 id. 명단은 캐릭터 선택 전후로 두 번 오므로
    /// (닉네임 → 캐릭터 이름) 전체를 찍으면 같은 사람이 두 벌 나온다.
    /// </summary>
    public readonly List<long> LastChanged = new();

    private readonly NameTable _table;

    public Roster(NameTable table) => _table = table;

    public void Clear()
    {
        _entries.Clear();
        _kindCount.Clear();
    }

    /// <summary>색상 태그가 붙은 표시명. 파일로 나갈 때 <c>Palette.Strip</c>이 걷어낸다.</summary>
    public string Name(long id)
    {
        if (id == 0) return "?";
        if (!_entries.TryGetValue(id, out Entry? e))
        {
            // 몹이라고 단정하지 않는다. 명단이 늦게 오면 플레이어가 조용히 몹으로 찍힌다.
            return $"?{id}";
        }
        return Palette.Player(Label(e), e.Slot);
    }

    /// <summary>
    /// 번호 판정을 이름 만들 때가 아니라 출력할 때 하므로, 두 번째가 나타나면 첫 번째도
    /// 소급해서 번호가 붙는다.
    /// </summary>
    private string Label(Entry e) =>
        _kindCount.TryGetValue((e.IsMonster, e.Kind), out int n) && n > 1
            ? $"{e.Kind}{e.Ordinal}"
            : e.Kind;

    public bool Update(byte[] body)
    {
        LastChanged.Clear();
        var outer = new ProtoReader(body, 0, body.Length);
        if (!outer.NextField(out int f, out int w) || f != 1 || w != ProtoReader.WireLength) return false;
        if (!outer.TryReadMessage(out var room)) return false;

        bool changed = false;
        while (room.NextField(out int field, out int wire))
        {
            if (wire == ProtoReader.WireLength && (field == 9 || field == 10))
            {
                if (!room.TryReadMessage(out var player)) return changed;
                changed |= AddPlayer(player, isMonster: field == 10);
            }
            else if (!room.Skip(wire))
            {
                return changed; // CombatCards(16) / EffectCards(17) 등은 여기서 버려진다
            }
        }
        return changed;
    }

    public static long ReadRoomId(byte[] body)
    {
        var outer = new ProtoReader(body, 0, body.Length);
        if (!outer.NextField(out int f, out int w) || f != 1 || w != ProtoReader.WireLength) return 0;
        if (!outer.TryReadMessage(out var room)) return 0;

        while (room.NextField(out int field, out int wire))
        {
            if (field == 1 && wire is ProtoReader.WireVarint or ProtoReader.WireFixed32
                                         or ProtoReader.WireFixed64)
                return room.TryReadNumber(wire, out long id) ? id : 0;
            if (!room.Skip(wire)) break;
        }
        return 0;
    }

    /// <summary>
    /// <c>MonsterRefreshS2C {1: Player}</c>에서 몹 하나를 등록한다. 몹은 Room이 아니라
    /// 이 메시지로 들어오는 경우가 많다.
    /// </summary>
    public bool UpdateMonster(byte[] body)
    {
        var outer = new ProtoReader(body, 0, body.Length);
        if (!outer.NextField(out int f, out int w) || f != 1 || w != ProtoReader.WireLength) return false;
        return outer.TryReadMessage(out var player) && AddPlayer(player, isMonster: true);
    }

    private bool AddPlayer(ProtoReader r, bool isMonster)
    {
        long id = 0, slot = 0, isBot = 0, heroId = 0;
        string nick = "";

        while (r.NextField(out int field, out int wire))
        {
            if (field == 2 && wire == ProtoReader.WireLength)
            {
                if (!r.TryReadString(out nick)) break;
            }
            else if (field == 10 && wire == ProtoReader.WireLength)
            {
                // **HeroId(필드 2)만** 꺼낸다. Hero는 손패(Cards, 8)도 품고 있으므로
                // 다른 필드를 읽는 코드를 추가하지 말 것.
                if (!r.TryReadMessage(out var hero)) break;
                while (hero.NextField(out int hf, out int hw))
                {
                    if (hf == 2 && hw is ProtoReader.WireVarint or ProtoReader.WireFixed32
                                        or ProtoReader.WireFixed64)
                    {
                        if (!hero.TryReadNumber(hw, out heroId)) break;
                    }
                    else if (!hero.Skip(hw)) break;
                }
            }
            else if (wire is ProtoReader.WireVarint or ProtoReader.WireFixed32 or ProtoReader.WireFixed64)
            {
                if (!r.TryReadNumber(wire, out long v)) break;
                if (field == 1) id = v;
                else if (field == 6) slot = v;
                else if (field == 20) isBot = v;
            }
            else if (!r.Skip(wire))
            {
                break;
            }
        }

        if (id == 0) return false;

        // 서버가 같은 개체를 Players와 Monsters 양쪽에 실어 보내므로, 한 번 플레이어로
        // 잡힌 id는 되돌리지 않는다.
        bool wasPlayer = _entries.TryGetValue(id, out Entry? existing) && !existing.IsMonster;
        if (wasPlayer) isMonster = false;

        // 표시 이름의 뿌리. 몹이든 플레이어든 캐릭터/몹 이름표를 먼저 본다.
        string kind = (isMonster ? _table.Lookup("monster", heroId) : _table.Lookup("character", heroId))
                      ?? _table.Lookup(isMonster ? "character" : "monster", heroId)
                      ?? (nick.Length > 0 ? nick : isMonster ? "몹" : $"{slot + 1}P");

        if (existing is not null && existing.Kind == kind && existing.IsMonster == isMonster) return false;

        if (existing is not null) Release(existing.IsMonster, existing.Kind);
        var bucket = (isMonster, kind);
        _kindCount.TryGetValue(bucket, out int count);
        _kindCount[bucket] = count + 1;

        _entries[id] = new Entry
        {
            Kind = kind,
            Ordinal = FreeOrdinal(id, isMonster, kind),
            IsMonster = isMonster,
            Slot = isMonster ? -1 : (int)slot,
            HeroId = heroId,
        };
        LastChanged.Add(id);
        return true;
    }

    /// <summary>
    /// 같은 종류 안에서 아직 안 쓴 가장 작은 번호. 인원수+1을 쓰면 픽창에서 캐릭터를
    /// 바꿨다가 다른 사람이 같은 캐릭터를 고를 때 두 사람이 같은 번호를 갖는다.
    /// </summary>
    private int FreeOrdinal(long self, bool isMonster, string kind)
    {
        var used = new HashSet<int>();
        foreach (var (id, e) in _entries)
            if (id != self && e.IsMonster == isMonster && e.Kind == kind) used.Add(e.Ordinal);

        int n = 1;
        while (used.Contains(n)) n++;
        return n;
    }

    private void Release(bool isMonster, string kind)
    {
        var bucket = (isMonster, kind);
        if (!_kindCount.TryGetValue(bucket, out int count)) return;
        if (count <= 1) _kindCount.Remove(bucket);
        else _kindCount[bucket] = count - 1;
    }

    /// <summary>
    /// 표시명이 캐릭터/몹 이름으로 확정됐는가. <c>false</c>면 아직 계정 닉네임이나
    /// <c>2P</c> 같은 자리표시자다.
    ///
    /// 명단은 캐릭터 선택 <b>전후로 두 번</b> 온다. 확정 전 것을 찍으면 같은 사람이 두 벌
    /// 나오고, 게다가 첫 벌에는 <b>계정 닉네임</b>이 들어간다 — 로그를 공유하면 남의
    /// 계정 정보가 같이 나간다(CONTRIBUTING "계정 식별자를 로그 본문에 쓰지 않는다").
    /// </summary>
    public bool Knows(long id) => _entries.ContainsKey(id);

    public bool IsEmpty => _entries.Count == 0;

    public bool HasHero(long id) =>
        _entries.TryGetValue(id, out Entry? e) && e.HeroId != 0;

    /// <summary>
    /// 이 참가자가 가진 스킬 이름들. <b>패시브는 발동해도 "스킬 썼다"가 안 오고 버프로만
    /// 오므로</b>, 미리 적어둬야 나중 버프 줄을 패시브로 짚어낼 수 있다.
    /// </summary>
    public string Skills(long id)
    {
        if (!_entries.TryGetValue(id, out Entry? e) || e.HeroId == 0) return "";
        return _table.Lookup(e.IsMonster ? "monskill" : "charskill", e.HeroId) ?? "";
    }

    public string Describe(long id, bool includeIds)
    {
        string name = Name(id);
        if (!includeIds) return name;
        // 진단용: 서버가 이 개체를 Players로 보냈는지 Monsters로 보냈는지 보여준다.
        bool monster = _entries.TryGetValue(id, out Entry? e) && e.IsMonster;
        return $"{name} = {id} ({(monster ? "Monsters" : "Players")} 목록)";
    }
}
