using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using AstralPartyBattleLog.Proto;

namespace AstralPartyBattleLog.Log;

/// <summary>playerId → 표시명. 규칙은 <c>docs/ARCHITECTURE.md</c> "플레이어 이름".</summary>
internal sealed class Roster
{
    private sealed class Entry
    {
        public string Kind = "";
        public int Ordinal;
        public bool IsMonster;
        public int Slot = -1;
        public long HeroId;
    }

    private readonly Dictionary<long, Entry> _entries = new();

    // PvE 적이 플레이어 캐릭터를 쓰기도 해서 몹 여부까지 키에 넣는다.
    private readonly Dictionary<(bool IsMonster, string Kind), int> _kindCount = new();

    public readonly List<long> LastChanged = new();

    private readonly NameTable _table;

    public RosterCache? Cache;

    public Roster(NameTable table) => _table = table;

    public void Clear()
    {
        _entries.Clear();
        _kindCount.Clear();
    }

    public string Name(long id)
    {
        if (id == 0) return "?";
        if (!_entries.TryGetValue(id, out Entry? e))
        {
            // 몹으로 단정하면 명단이 늦게 올 때 플레이어가 조용히 몹으로 찍힌다.
            if (!Restore(id, out e)) return $"?{id}";
        }
        return Palette.Player(Label(e), e.Slot);
    }

    private bool Restore(long id, [NotNullWhen(true)] out Entry? entry)
    {
        entry = null;
        if (Cache is null || !Cache.TryGet(id, out RosterCache.Row row)) return false;
        entry = Register(id, row);
        return true;
    }

    private Entry Register(long id, in RosterCache.Row row)
    {
        var bucket = (row.IsMonster, row.Kind);
        _kindCount.TryGetValue(bucket, out int count);
        _kindCount[bucket] = count + 1;

        var entry = new Entry
        {
            Kind = row.Kind,
            Ordinal = FreeOrdinal(id, row.IsMonster, row.Kind),
            IsMonster = row.IsMonster,
            Slot = row.Slot,
            HeroId = row.HeroId,
        };
        _entries[id] = entry;
        return entry;
    }

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
                return changed;
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

    public bool UpdateMonster(byte[] body)
    {
        var outer = new ProtoReader(body, 0, body.Length);
        if (!outer.NextField(out int f, out int w) || f != 1 || w != ProtoReader.WireLength) return false;
        return outer.TryReadMessage(out var player) && AddPlayer(player, isMonster: true);
    }

    private bool AddPlayer(ProtoReader r, bool isMonster)
    {
        long id = 0, slot = 0, isBot = 0, heroId = 0;

        // Nick(2)은 계정 닉네임이라 읽지 않는다. 표시명·캐시로 새는 경로를 코드에서 없앤다.
        while (r.NextField(out int field, out int wire))
        {
            if (field == 10 && wire == ProtoReader.WireLength)
            {
                // Hero는 손패(Cards, 8)도 품고 있다. HeroId(2) 말고는 읽지 말 것.
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

        // 서버가 같은 개체를 Players와 Monsters 양쪽에 실어 보낸다.
        bool wasPlayer = _entries.TryGetValue(id, out Entry? existing) && !existing.IsMonster;
        if (wasPlayer) isMonster = false;

        string? named = (isMonster ? _table.Lookup("monster", heroId) : _table.Lookup("character", heroId))
                        ?? _table.Lookup(isMonster ? "character" : "monster", heroId);
        string kind = named ?? (isMonster ? "몹" : $"{slot + 1}P");

        if (existing is not null && existing.Kind == kind && existing.IsMonster == isMonster
            && existing.HeroId == heroId) return false;

        if (existing is not null) Release(existing.IsMonster, existing.Kind);
        var row = new RosterCache.Row(kind, isMonster, isMonster ? -1 : (int)slot, heroId);
        Register(id, row);
        // 자리표시자는 캐시에 남기지 않는다. 캐시에는 이름표에서 온 이름만 들어간다.
        if (named is not null) Cache?.Put(id, row);
        LastChanged.Add(id);
        return true;
    }

    // 인원수+1로 매기면 픽창에서 캐릭터를 바꿨다가 다른 사람이 같은 캐릭터를 고를 때 번호가 겹친다.
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

    // 캐시는 안 본다 — 방이 아직 확정되지 않았을 때는 지난 방의 캐시일 수 있고, 대기열이
    // 기다리는 건 이번 판 등록이다. 캐시 이름은 어차피 Name()에서 대기열과 무관하게 되살아난다.
    public bool Knows(long id) => _entries.ContainsKey(id);

    public bool IsCharacter(long id) => _entries.TryGetValue(id, out Entry? e) && !e.IsMonster;

    public bool IsEmpty => _entries.Count == 0;

    // false면 캐릭터 선택 전 명단이다. 같은 사람이 자리표시자와 캐릭터 이름으로 두 번 찍히지 않게 거른다.
    public bool HasHero(long id) =>
        _entries.TryGetValue(id, out Entry? e) && e.HeroId != 0;

    public string Skills(long id)
    {
        if (!_entries.TryGetValue(id, out Entry? e) || e.HeroId == 0) return "";
        return _table.Lookup(e.IsMonster ? "monskill" : "charskill", e.HeroId) ?? "";
    }

    public string Describe(long id, bool includeIds)
    {
        string name = Name(id);
        if (!includeIds) return name;
        bool monster = _entries.TryGetValue(id, out Entry? e) && e.IsMonster;
        return $"{name} = {id} ({(monster ? "Monsters" : "Players")} 목록)";
    }
}
