using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// 판 도중 재접속·게임 재시작으로 참가자 명단(<c>RunningGameS2C</c>)을 놓쳤을 때 쓰는
/// 이름표 캐시.
///
/// <b>playerId를 파일에 남기지 않는다.</b> <c>Player.Id</c>는 매치용 슬롯이 아니라 영구
/// 계정 식별자라, 파일로 새면 로그를 공유할 때 남의 계정 정보가 같이 나간다. 그래서 키를
/// 해시로만 적고, 조회할 때 들어온 id를 같은 방식으로 해시해 맞춘다 — 되돌릴 수단이
/// 없으므로 파일만 얻어도 id를 복원하지 못한다.
///
/// 값(캐릭터·몹 이름표, 슬롯)은 게임 자산이라 민감하지 않다.
/// </summary>
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

    private const string Header = "# roster cache v2 (keys are hashed; see RosterCache.cs)";

    /// <summary>방 번호는 계정과 무관한 매치 식별자라 해시하지 않고 그대로 적는다.</summary>
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

    /// <summary>앞 8바이트만 쓴다. 한 판에 수십 명이라 충돌은 사실상 나지 않는다.</summary>
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

    /// <summary>수명을 짧게 두는 것이 이 캐시의 안전장치다.</summary>
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
            foreach (string line in File.ReadAllLines(_path))
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
