using System.Collections.Generic;
using AstralPartyBattleLog.Proto;

namespace AstralPartyBattleLog.Log;

// CauseOrigin.Id(land)는 LandType이 아니라 맵 칸 번호다. Room.Lands(12)로 종류를 찾는다.
internal sealed class LandMap
{
    private readonly Dictionary<long, long> _typeByNode = new();

    public void Clear() => _typeByNode.Clear();

    public long? TypeOf(long nodeId) =>
        _typeByNode.TryGetValue(nodeId, out long type) ? type : null;

    public void Update(byte[] body)
    {
        var outer = new ProtoReader(body, 0, body.Length);
        if (!outer.NextField(out int f, out int w) || f != 1 || w != ProtoReader.WireLength) return;
        if (!outer.TryReadMessage(out var room)) return;

        while (room.NextField(out int field, out int wire))
        {
            if (field == 12 && wire == ProtoReader.WireLength)
            {
                if (!room.TryReadMessage(out var entry)) return;
                ReadEntry(entry);
            }
            else if (!room.Skip(wire))
            {
                return;
            }
        }
    }

    private void ReadEntry(ProtoReader entry)
    {
        long node = 0, type = 0;
        bool haveType = false;

        while (entry.NextField(out int field, out int wire))
        {
            if (field == 1 && wire is ProtoReader.WireFixed32 or ProtoReader.WireFixed64
                                   or ProtoReader.WireVarint)
            {
                if (!entry.TryReadNumber(wire, out node)) return;
            }
            else if (field == 2 && wire == ProtoReader.WireLength)
            {
                if (!entry.TryReadMessage(out var land)) return;
                while (land.NextField(out int lf, out int lw))
                {
                    if (lw is ProtoReader.WireFixed32 or ProtoReader.WireFixed64 or ProtoReader.WireVarint)
                    {
                        if (!land.TryReadNumber(lw, out long v)) break;
                        if (lf == 1 && node == 0) node = v;
                        else if (lf == 2) { type = v; haveType = true; }
                    }
                    else if (!land.Skip(lw)) break;
                }
            }
            else if (!entry.Skip(wire))
            {
                return;
            }
        }

        if (node != 0 && haveType) _typeByNode[node] = type;
    }
}
