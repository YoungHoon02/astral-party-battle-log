using System;
using System.Collections.Generic;
using System.IO;

namespace AstralPartyBattleLog.Log;

internal sealed class NameTable
{
    // 통째로 갈아끼우기만 하므로 잠금이 없다. 항목 단위로 바꾸면 스레드 간 잠금이 필요해진다.
    private volatile Dictionary<string, string> _names = new();

    public int Count => _names.Count;

    public static NameTable Load(string path, Action<string> warn)
    {
        var table = new NameTable();
        if (!File.Exists(path)) return table;

        try
        {
            var names = new Dictionary<string, string>();
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] parts = line.Split('\t');
                if (parts.Length < 3) continue;
                names[Key(parts[0], long.Parse(parts[1]))] = parts[2];
            }
            table._names = names;
        }
        catch (Exception e)
        {
            warn($"Could not read names.tsv; ids will be shown instead: {e.Message}");
        }
        return table;
    }

    public void Adopt(Dictionary<string, string> fresh) => _names = fresh;

    public static string Key(string kind, long id) => $"{kind} {id}";

    public string? Lookup(string kind, long id) =>
        id > 0 && _names.TryGetValue(Key(kind, id), out string? n) ? n : null;
}
