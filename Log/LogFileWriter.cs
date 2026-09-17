using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// <c>battle-log.txt</c>를 전용 스레드에서 쓴다. <b>시각과 순서는 넣을 때 정해진다</b> —
/// 부르는 쪽이 해석 시점의 시각을 찍어서 넘기므로 파일 내용은 소켓 스레드에서 바로 쓰던
/// 때와 같다.
///
/// 소켓 스레드에서 디스크를 기다리면 다음 패킷 처리가 그만큼 밀린다. 넣기는 대기 없이
/// 끝나고, 비우기(<see cref="Truncate"/>)도 같은 큐를 타서 줄과 순서가 섞이지 않는다.
///
/// 순수 .NET 스레드이고 IL2CPP 객체를 건드리지 않는다.
/// </summary>
internal sealed class LogFileWriter
{
    private static readonly string TruncateSignal = new('\0', 1);

    /// <summary>
    /// 큐 상한. 넘치면 그 줄은 파일에 남기지 않는다 — 소켓 스레드를 막지 않는 쪽을 고른다.
    /// 디스크가 멈추지 않는 한 닿지 않는 크기다.
    /// </summary>
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
        // 종료와 겹치면 CompleteAdding 뒤에 들어올 수 있다. 소켓 스레드로 예외를 넘기지 않는다.
        try { return _queue.TryAdd(item); }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>남은 줄을 쓰고 끝낸다. 종료 경로에서 불리므로 오래 기다리지 않는다.</summary>
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
                // 몰려 온 줄은 한 번에 쓴다. 비우기 신호가 끼어 있으면 거기서 끊는다.
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
        catch { /* 로그 파일 실패로 게임을 막지 않는다 */ }
    }
}
