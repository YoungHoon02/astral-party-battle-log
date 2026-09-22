using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace AstralPartyBattleLog.Log;

// 소켓 스레드가 디스크를 기다리지 않게 전용 스레드에서 쓴다. 시각은 넣을 때 찍는다.
internal sealed class LogFileWriter
{
    private static readonly string TruncateSignal = new('\0', 1);

    // 넘치면 소켓 스레드를 막는 대신 그 줄을 버린다.
    private const int Capacity = 20000;

    private readonly string _path;
    private readonly Action<string> _warn;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), Capacity);
    private readonly Thread _thread;
    private int _dropped;

    public LogFileWriter(string path, Action<string> warn)
    {
        _path = path;
        _warn = warn;
        _thread = new Thread(Run) { IsBackground = true, Name = "BattleLogFile" };
        _thread.Start();
    }

    public void Append(string line)
    {
        if (TryAdd(DateTime.Now.ToString("HH:mm:ss.fff ") + line) || _queue.IsAddingCompleted) return;
        if (Interlocked.Increment(ref _dropped) == 1)
            _warn("battle-log.txt queue is full; some lines were not written.");
    }

    public void Truncate() => TryAdd(TruncateSignal);

    private bool TryAdd(string item)
    {
        // 종료와 겹치면 CompleteAdding 뒤에 들어올 수 있다.
        try { return _queue.TryAdd(item); }
        catch (InvalidOperationException) { return false; }
    }

    public void Close(int waitMs = 1000)
    {
        _queue.CompleteAdding();
        _thread.Join(waitMs);
    }

    private void Run()
    {
        var batch = new StringBuilder();
        try
        {
            foreach (string item in _queue.GetConsumingEnumerable())
            {
                if (ReferenceEquals(item, TruncateSignal))
                {
                    Flush(batch);
                    Write(() => File.WriteAllText(_path, ""));
                    continue;
                }

                batch.Append(item).Append(Environment.NewLine);
                while (_queue.TryTake(out string? next))
                {
                    if (ReferenceEquals(next, TruncateSignal))
                    {
                        Flush(batch);
                        Write(() => File.WriteAllText(_path, ""));
                        break;
                    }
                    batch.Append(next).Append(Environment.NewLine);
                }
                Flush(batch);
            }
        }
        catch (Exception e)
        {
            _warn($"battle-log.txt writer stopped: {e.Message}");
        }
    }

    private void Flush(StringBuilder batch)
    {
        if (batch.Length == 0) return;
        string text = batch.ToString();
        batch.Clear();
        Write(() => File.AppendAllText(_path, text));
    }

    private void Write(Action io)
    {
        try { io(); }
        catch { }
    }
}
