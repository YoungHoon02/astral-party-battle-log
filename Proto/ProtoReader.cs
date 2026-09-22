namespace AstralPartyBattleLog.Proto;

// 게임 파서를 쓰지 않는 이유는 안전이다. 디코더가 없는 메시지(손패 등)는 실수로도 읽을 수 없다.
internal struct ProtoReader
{
    public const int WireVarint = 0;
    public const int WireFixed64 = 1;
    public const int WireLength = 2;
    public const int WireFixed32 = 5;

    private readonly byte[] _buf;
    private int _pos;
    private readonly int _end;

    public ProtoReader(byte[] buf, int offset, int length)
    {
        _buf = buf;
        _pos = offset;
        _end = offset + length;
    }

    public bool HasMore => _pos < _end;

    public bool NextField(out int fieldNumber, out int wireType)
    {
        fieldNumber = 0;
        wireType = 0;
        if (_pos >= _end) return false;
        if (!TryReadVarint(out ulong tag)) return false;
        fieldNumber = (int)(tag >> 3);
        wireType = (int)(tag & 0x7);
        return fieldNumber > 0;
    }

    public bool TryReadVarint(out ulong value)
    {
        value = 0;
        int shift = 0;
        while (_pos < _end && shift <= 63)
        {
            byte b = _buf[_pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    // 이 게임은 숫자를 sfixed32/64로 보낸다(enum만 varint). varint만 읽으면 값이 조용히 0이 된다.
    public bool TryReadNumber(int wireType, out long value)
    {
        switch (wireType)
        {
            case WireVarint:
                bool ok = TryReadVarint(out ulong v);
                value = (long)v;
                return ok;
            case WireFixed32:
                return TryReadFixed32(out value);
            case WireFixed64:
                return TryReadFixed64(out value);
            default:
                value = 0;
                return false;
        }
    }

    public bool TryReadFixed32(out long value)
    {
        value = 0;
        if (_end - _pos < 4) return false;
        int raw = _buf[_pos] | (_buf[_pos + 1] << 8) | (_buf[_pos + 2] << 16) | (_buf[_pos + 3] << 24);
        _pos += 4;
        value = raw;
        return true;
    }

    public bool TryReadFixed64(out long value)
    {
        value = 0;
        if (_end - _pos < 8) return false;
        ulong raw = 0;
        for (int i = 7; i >= 0; i--) raw = (raw << 8) | _buf[_pos + i];
        _pos += 8;
        value = (long)raw;
        return true;
    }

    public bool TryReadLengthDelimited(out int offset, out int length)
    {
        offset = 0;
        length = 0;
        if (!TryReadVarint(out ulong len)) return false;
        if (len > (ulong)(_end - _pos)) return false;
        offset = _pos;
        length = (int)len;
        _pos += length;
        return true;
    }

    public bool TryReadString(out string value)
    {
        if (TryReadLengthDelimited(out int off, out int len))
        {
            value = System.Text.Encoding.UTF8.GetString(_buf, off, len);
            return true;
        }
        value = "";
        return false;
    }

    public bool TryReadMessage(out ProtoReader sub)
    {
        if (TryReadLengthDelimited(out int off, out int len))
        {
            sub = new ProtoReader(_buf, off, len);
            return true;
        }
        sub = default;
        return false;
    }

    public bool Skip(int wireType)
    {
        switch (wireType)
        {
            case WireVarint:
                return TryReadVarint(out _);
            case WireFixed64:
                return Advance(8);
            case WireLength:
                return TryReadLengthDelimited(out _, out _);
            case WireFixed32:
                return Advance(4);
            default:
                return false;
        }
    }

    private bool Advance(int n)
    {
        if (_end - _pos < n) return false;
        _pos += n;
        return true;
    }
}
