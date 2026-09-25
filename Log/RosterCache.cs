using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AstralPartyBattleLog.Log;

// playerId는 영구 계정 uid라 파일에 해시로만 적는다 (docs/ARCHITECTURE.md "재접속 이름표 캐시").
internal sealed class RosterCache
{
    internal readonly struct Row
    {
        public readonly string Kind;
        public readonly bool IsMonster;
        public readonly int Slot;
        public readonly long HeroId;

        public Row(string kind, bool isMonster, int slot, long heroId)
        {
            Kind = kind;
            IsMonster = isMonster;
            Slot = slot;
            HeroId = heroId;
        }
    }

    // v2까지는 캐릭터 선택 전 계정 닉네임이 이름값으로 들어갈 수 있었다. 헤더가 다르면 통째로 버린다.
    private const string Header = "# roster cache v3 (keys are hashed; see RosterCache.cs)";

    public long RoomId;

    private readonly string _path;
    private readonly Action<string> _warn;
    private readonly Dictionary<string, Row> _rows = new();

    public RosterCache(string path, Action<string> warn)
    {
        _path = path;
        _warn = warn;
    }

    public int Count => _rows.Count;

    private static string Key(long id)
    {
        Span<byte> src = stackalloc byte[8];
        BitConverter.TryWriteBytes(src, id);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(src, hash);

        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public bool TryGet(long id, out Row row) => _rows.TryGetValue(Key(id), out row);

    public void Put(long id, in Row row) => _rows[Key(id)] = row;

    public void Discard()
    {
        _rows.Clear();
        RoomId = 0;
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception e)
        {
            _warn($"roster cache delete failed: {e.Message}");
        }
    }

    public void Load()
    {
        _rows.Clear();
        try
        {
            if (!File.Exists(_path)) return;
            string[] lines = File.ReadAllLines(_path);
            if (lines.Length == 0 || lines[0] != Header)
            {
                _warn("roster cache header is not v3; discarded");
                Discard();
                return;
            }
            foreach (string line in lines)
            {
                if (line.Length == 0) continue;
                if (line[0] == '#')
                {
                    const string roomTag = "# room	";
                    if (line.StartsWith(roomTag, StringComparison.Ordinal)
                        && long.TryParse(line.Substring(roomTag.Length), NumberStyles.Integer,
                                         CultureInfo.InvariantCulture, out long rid))
                        RoomId = rid;
                    continue;
                }
                string[] c = line.Split('\t');
                if (c.Length != 5) continue;
                if (!int.TryParse(c[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot)) continue;
                if (!long.TryParse(c[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out long heroId)) continue;
                _rows[c[0]] = new Row(c[1], c[2] == "1", slot, heroId);
            }
        }
        catch (Exception e)
        {
            _rows.Clear();
            _warn($"roster cache load failed: {e.Message}");
        }
    }

    public void Save()
    {
        if (_rows.Count == 0) return;
        try
        {
            var sb = new StringBuilder();
            sb.Append(Header).Append('\n');
            sb.Append("# room\t").Append(RoomId.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var kv in _rows)
            {
                Row r = kv.Value;
                sb.Append(kv.Key).Append('\t')
                  .Append(r.Kind.Replace('\t', ' ')).Append('\t')
                  .Append(r.IsMonster ? '1' : '0').Append('\t')
                  .Append(r.Slot.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(r.HeroId.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            _warn($"roster cache save failed: {e.Message}");
        }
    }
}
