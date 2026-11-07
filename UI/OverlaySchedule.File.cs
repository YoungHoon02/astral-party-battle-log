using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AstralPartyBattleLog.Log;

namespace AstralPartyBattleLog.UI;

// battle-log.txt를 오버레이에 나간 순서와 시각으로 쓴다. 받은 즉시 쓰면 파일을 열어 둔 사람이
// 연출보다 먼저 결과를 보게 되어, 오버레이를 늦춘 의미가 사라진다. 근거는 docs/ARCHITECTURE.md "파일 기록 시점".
internal static partial class OverlaySchedule
{
    // 펌프가 이만큼 돌지 않으면(프레임 후크 오류 등) 보류 중인 줄을 미표시로 써서 파일 기록이 영영 막히지 않게 한다.
    // 줄의 나이로 재면 대기열 규칙으로 오래 기다리는 줄이 오버레이보다 먼저 파일에 쓰인다(docs/QUEUE-SYNC.md 규칙 8).
    private const long FileStallMarginUs = 60_000_000;

    private static readonly object FileGate = new();
    private static readonly SortedDictionary<long, (DateTime At, string Text)> FilePending = new();
    private static LogFileWriter? _file;
    private static long _fileKeys;

    // 펌프 멈춤은 펌프와 함께 멈추는 스케줄러 시계로 잴 수 없어 벽시계를 쓴다. 하네스가 바꿔 끼운다.
    private static readonly Stopwatch FileWatch = Stopwatch.StartNew();
    internal static Func<long> FileClockUs = () => FileWatch.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
    private static long _lastPumpUs;

    public static void AttachFile(LogFileWriter? file)
    {
        lock (FileGate)
        {
            _file = file;
            Volatile.Write(ref _lastPumpUs, FileClockUs());
        }
    }

    private static long HoldForFile(string text)
    {
        if (_file is null) return 0;
        DateTime now = DateTime.Now;
        bool stalled = FileClockUs() - Volatile.Read(ref _lastPumpUs) >= FileStallMarginUs;
        lock (FileGate)
        {
            LogFileWriter? file = _file;
            if (file is null) return 0;
            if (stalled)
            {
                foreach ((DateTime at, string pending) in FilePending.Values) file.AppendUnshown(pending, at);
                FilePending.Clear();
            }
            long next = ++_fileKeys;
            FilePending[next] = (now, Palette.Strip(text));
            return next;
        }
    }

    private static void WriteFile(long key, bool shown)
    {
        if (key == 0) return;
        lock (FileGate)
        {
            if (!FilePending.Remove(key, out (DateTime At, string Text) entry)) return;
            if (shown) _file?.AppendShown(entry.Text, entry.At);
            else _file?.AppendUnshown(entry.Text, entry.At);
        }
    }

    // 비우기는 큐 순서대로 나가므로 지난 판 줄이 모두 쓰이거나 버려진 뒤에 파일을 비운다.
    private static void TruncateFile()
    {
        lock (FileGate) _file?.Truncate();
    }

    // 종료 때 아직 화면에 나가지 않은 줄도 잃지 않게 남긴다.
    internal static void FlushFile()
    {
        lock (FileGate)
        {
            foreach ((DateTime at, string text) in FilePending.Values) _file?.AppendUnshown(text, at);
            FilePending.Clear();
        }
    }
}
