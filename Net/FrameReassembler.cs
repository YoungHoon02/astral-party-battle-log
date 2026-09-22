using System;

namespace AstralPartyBattleLog.Net;

// 헤더 형식(35바이트, 빅엔디안)은 docs/LOGGER-DESIGN.md.
internal sealed class FrameReassembler
{
    public const int HeaderLength = 35;
    private const int MaxBody = 4 * 1024 * 1024;

    private byte[] _buf = new byte[64 * 1024];
    private int _len;

    // HTTP 등 게임 프로토콜이 아닌 소켓도 같은 타입을 쓴다. 판명되면 이후 입력을 전부 버린다.
    public bool Rejected { get; private set; }

    public void Append(byte[] src, int offset, int count)
    {
        if (Rejected || count <= 0) return;

        if (_len + count > _buf.Length)
        {
            int want = _buf.Length;
            while (want < _len + count) want *= 2;
            if (want > MaxBody * 2) { Rejected = true; return; }
            Array.Resize(ref _buf, want);
        }

        Buffer.BlockCopy(src, offset, _buf, _len, count);
        _len += count;
    }

    public bool TryDequeue(out FrameHeader header, out byte[] body)
    {
        header = default;
        body = Array.Empty<byte>();
        if (Rejected || _len < HeaderLength) return false;

        int bodyLen = ReadInt32BE(_buf, 0);
        if (bodyLen < 0 || bodyLen > MaxBody)
        {
            Rejected = true;
            return false;
        }

        int total = HeaderLength + bodyLen;
        if (_len < total) return false;

        header = new FrameHeader(ReadUInt16BE(_buf, 12), (short)ReadUInt16BE(_buf, 33),
                                 ReadInt64BE(_buf, 17), ReadInt64BE(_buf, 25));
        body = new byte[bodyLen];
        Buffer.BlockCopy(_buf, HeaderLength, body, 0, bodyLen);

        _len -= total;
        if (_len > 0) Buffer.BlockCopy(_buf, total, _buf, 0, _len);
        return true;
    }

    private static int ReadInt32BE(byte[] b, int i) =>
        (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];

    private static long ReadInt64BE(byte[] b, int i) =>
        ((long)ReadInt32BE(b, i) << 32) | (uint)ReadInt32BE(b, i + 4);

    private static int ReadUInt16BE(byte[] b, int i) =>
        (b[i] << 8) | b[i + 1];
}
