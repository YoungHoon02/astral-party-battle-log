using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AstralPartyBattleLog.Log;

// 게임 스레드에서 문자열을 만들지 않으려고 값만 담는다. 글자는 writer 스레드가 만든다.
// 필드 뜻은 종류마다 다르며 OverlaySchedule.Replay.cs의 FormatRecord가 정한다.
internal struct ReplayRecord
{
    public const long Null = long.MinValue;

    public byte Type;
    public long Seq;
    public long A, B, C, D, E, F, G, H, I, J, K;
}

internal interface IReplaySink
{
    void Add(ReplayRecord record);
}

// 재생 기록 JSONL writer. 소켓·메인 스레드를 막지 않는다. 완전성 규칙은 docs/TIMING-REPLAY-HANDOFF.md 4.5절.
// LogFileWriter와 달리 쓰기 예외를 삼키지 않는다: 한 번 실패하면 끝까지 실패로 남기고 end에 적는다.
//
// 게임을 끌 때 ProcessExit가 오지 않는다(실측 2026-09-26). 그래서 쓸 때마다 파일 끝에 임시 end(checkpoint)를
// 붙여 두고, 다음에 쓸 때 그 자리부터 덮어쓴다. 프로세스가 언제 끝나도 마지막으로 쓴 지점까지는 닫힌 파일이다.
// 임시 end는 Pump 구간 밖에서만 붙이고, 구간 안에서 끊긴 레코드는 다음 쓰기로 넘긴다. 닫을 때도 마지막으로
// 닫힌 구간까지만 쓰므로, 종료가 진행 중인 Pump와 겹쳐도 미완성 구간이 파일에 남지 않는다.
internal sealed class ReplayWriter : IReplaySink
{
    public const int DefaultCapacity = 65536;
    public const long DefaultMaxBytes = 512L << 20;
    public const string Checkpoint = "checkpoint";
    private const int EndReserve = 512;
    // 임시 end를 매 프레임 다시 쓰지 않도록 모아서 쓴다. 강제 종료 때 잃는 것은 이 간격만큼이다.
    private const int BatchIntervalMs = 100;
    // 구간이 끝내 닫히지 않을 때 넘긴 레코드가 한없이 쌓이지 않게 한다.
    private const int MaxCarryBytes = 1 << 20;

    private readonly Stream _out;
    private readonly Action<StringBuilder, ReplayRecord> _format;
    private readonly Action<string> _warn;
    private readonly int _capacity;
    private readonly long _maxBytes;
    private readonly byte _sectionOpen, _sectionClose;
    private readonly Func<bool>? _liveOverlap;

    private readonly object _sync = new();
    private readonly Queue<ReplayRecord> _queue = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _wake = new();
    private readonly ManualResetEventSlim _done = new();

    // _sync로 보호한다. 순번을 큐 삽입 시도와 같은 잠금 안에서 매겨, 생산자가 여럿이어도 파일이 순번순이 된다.
    private long _seq;
    private long _dropped;
    private bool _closed;
    private bool _stoppedEarly;
    private bool _completing;
    private string _endReason = "normal";
    private int _maxDepth;

    // writer 스레드에서만 쓴다. _bytes·_count·해시는 임시 end를 뺀 데이터만 센다.
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly MemoryStream _pending = new();
    private int _pendingCount;
    private long _pendingLastSeq;
    private int _depth;
    private long _count;
    private long _bytes;
    private long _lastSeq;
    private bool _capReached;
    private int _ioFailed;

    public ReplayWriter(Stream output, Action<StringBuilder, ReplayRecord> format, Action<string> warn,
                        int capacity = DefaultCapacity, long maxBytes = DefaultMaxBytes,
                        byte sectionOpen = byte.MaxValue, byte sectionClose = byte.MaxValue,
                        Func<bool>? liveOverlap = null)
    {
        _out = output;
        _format = format;
        _warn = warn;
        _capacity = capacity;
        _maxBytes = maxBytes;
        _sectionOpen = sectionOpen;
        _sectionClose = sectionClose;
        _liveOverlap = liveOverlap;
        _thread = new Thread(Run) { IsBackground = true, Name = "BattleLogReplay" };
        _thread.Start();
    }

    public long Dropped
    {
        get { lock (_sync) return _dropped; }
    }

    public bool IoFailed => Volatile.Read(ref _ioFailed) != 0;

    public bool Stopped
    {
        get { lock (_sync) return _closed; }
    }

    public int MaxQueueDepth
    {
        get { lock (_sync) return _maxDepth; }
    }

    public long BytesWritten => Interlocked.Read(ref _bytes);

    // 큐가 차면 기다리지 않고 버린다. 순번은 버려도 소비되어 파일에 구멍으로 드러난다.
    public void Add(ReplayRecord record)
    {
        lock (_sync)
        {
            if (_closed)
            {
                if (_stoppedEarly) _dropped++;
                return;
            }
            record.Seq = ++_seq;
            if (_queue.Count >= _capacity)
            {
                _dropped++;
                return;
            }
            _queue.Enqueue(record);
            if (_queue.Count > _maxDepth) _maxDepth = _queue.Count;
            if (_queue.Count == 1) Monitor.Pulse(_sync);
        }
    }

    // 표 용량 초과처럼 기록을 더 믿을 수 없을 때. 받은 것까지 쓰고 불완전 end로 닫는다.
    public void Abort(string why)
    {
        StopEarly();
        lock (_sync)
        {
            _completing = true;
            Monitor.Pulse(_sync);
        }
        _wake.Set();
        _warn($"[replay] recording stopped: {why}");
    }

    // 제한 시간 안에 최종 end를 쓰지 못하면 false. 파일에는 마지막 임시 end가 남는다.
    public bool Close(string reason, int waitMs)
    {
        lock (_sync)
        {
            if (!_completing) _endReason = reason;
            _closed = true;
            _completing = true;
            Monitor.Pulse(_sync);
        }
        _wake.Set();
        return _done.Wait(waitMs);
    }

    private void StopEarly()
    {
        lock (_sync)
        {
            _closed = true;
            _stoppedEarly = true;
        }
    }

    private void Run()
    {
        var batch = new List<ReplayRecord>();
        var line = new StringBuilder(256);
        try
        {
            while (true)
            {
                bool finishing;
                lock (_sync)
                {
                    while (_queue.Count == 0 && !_completing) Monitor.Wait(_sync);
                    while (_queue.Count > 0) batch.Add(_queue.Dequeue());
                    finishing = _completing;
                }
                WriteBatch(batch, line, finishing);
                batch.Clear();
                if (finishing) break;
                _wake.Wait(BatchIntervalMs);
            }
        }
        catch (Exception e)
        {
            Fail(e);
        }
        finally
        {
            try
            {
                _out.Dispose();
            }
            catch (Exception e)
            {
                Fail(e);
            }
            _done.Set();
        }
    }

    private void WriteBatch(List<ReplayRecord> batch, StringBuilder line, bool final)
    {
        long boundary = -1;
        int boundaryCount = 0;
        long boundarySeq = 0;
        if (_depth == 0)
        {
            boundary = _pending.Length;
            boundaryCount = _pendingCount;
            boundarySeq = _pendingLastSeq;
        }

        foreach (ReplayRecord r in batch)
        {
            if (IoFailed || _capReached)
            {
                Lost(1);
                continue;
            }
            line.Clear();
            line.Append("{\"recordSeq\":").Append(r.Seq).Append(',');
            _format(line, r);
            line.Append('\n');
            byte[] encoded = Encoding.UTF8.GetBytes(line.ToString());
            if (_bytes + _pending.Length + encoded.Length > _maxBytes - EndReserve)
            {
                // 앞부분을 덮거나 게임을 막지 않고 계측을 끝낸다.
                _capReached = true;
                StopEarly();
                _warn($"[replay] file size limit {_maxBytes} bytes reached; recording stopped");
                Lost(1);
                continue;
            }
            _pending.Write(encoded, 0, encoded.Length);
            _pendingCount++;
            _pendingLastSeq = r.Seq;
            if (r.Type == _sectionOpen) _depth++;
            else if (r.Type == _sectionClose && _depth > 0) _depth--;
            if (_depth == 0)
            {
                boundary = _pending.Length;
                boundaryCount = _pendingCount;
                boundarySeq = _pendingLastSeq;
            }
        }

        if (_pending.Length > MaxCarryBytes)
        {
            boundary = _pending.Length;
            boundaryCount = _pendingCount;
            boundarySeq = _pendingLastSeq;
        }
        if (final) Commit((int)Math.Max(boundary, 0), boundaryCount, boundarySeq, line, FinalReason());
        else if (boundary > 0 && !IoFailed) Commit((int)boundary, boundaryCount, boundarySeq, line, Checkpoint);
    }

    // 임시 end 자리부터 데이터를 덮어쓰고 새 end를 붙인다. 넘긴 레코드는 버퍼 앞으로 당긴다.
    private void Commit(int length, int count, long lastSeq, StringBuilder line, string reason)
    {
        byte[] data = _pending.GetBuffer();
        // 이미 쓰기가 실패했으면 데이터는 버리고(유실로 센다) end만 시도한다.
        bool skip = length > 0 && IoFailed;
        if (skip) Lost(count);
        bool committed = length == 0 || skip;
        try
        {
            _out.Position = _bytes;
            if (!committed)
            {
                _out.Write(data, 0, length);
                committed = true;
                _hash.AppendData(data, 0, length);
                _count += count;
                _lastSeq = lastSeq;
                Interlocked.Add(ref _bytes, length);
            }
            byte[] end = EndLine(line, reason);
            _out.Write(end, 0, end.Length);
            _out.SetLength(_out.Position);
            _out.Flush();
        }
        catch (Exception e)
        {
            Fail(e);
        }
        if (!committed) Lost(count);

        int rest = (int)_pending.Length - length;
        Buffer.BlockCopy(data, length, data, 0, rest);
        _pending.SetLength(rest);
        _pendingCount -= count;
    }

    private string FinalReason()
    {
        lock (_sync) return _endReason;
    }

    private byte[] EndLine(StringBuilder line, string reason)
    {
        long dropped;
        bool early;
        lock (_sync)
        {
            dropped = _dropped;
            early = _stoppedEarly;
        }
        bool overlap = _liveOverlap?.Invoke() ?? false;
        bool failed = IoFailed;
        if (early || failed || dropped > 0) reason = "incomplete";

        line.Clear();
        line.Append("{\"recordSeq\":").Append(_lastSeq + 1)
            .Append(",\"type\":\"end\",\"reason\":\"").Append(reason)
            .Append("\",\"dropped\":").Append(dropped)
            .Append(",\"ioFailed\":").Append(failed ? "true" : "false")
            .Append(",\"overlap\":").Append(overlap ? "true" : "false")
            .Append(",\"previousCount\":").Append(_count)
            .Append(",\"previousBytes\":").Append(Interlocked.Read(ref _bytes))
            .Append(",\"sha256\":\"").Append(Convert.ToHexString(_hash.GetCurrentHash()).ToLowerInvariant())
            .Append("\"}\n");
        return Encoding.UTF8.GetBytes(line.ToString());
    }

    private void Lost(int n)
    {
        if (n == 0) return;
        lock (_sync) _dropped += n;
    }

    private void Fail(Exception e)
    {
        if (Interlocked.Exchange(ref _ioFailed, 1) != 0) return;
        StopEarly();
        _warn($"[replay] write failed; the recording is incomplete: {e.Message}");
    }
}
