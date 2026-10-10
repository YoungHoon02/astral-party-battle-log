using System;
using System.Collections.Generic;
using System.Linq;
using AstralPartyBattleLog.Log;

namespace AstralPartyBattleLog.UI;

// battle-log.txt를 오버레이에 나간 순서와 시각으로 쓴다. 받은 즉시 쓰면 파일을 열어 둔 사람이
// 연출보다 먼저 결과를 보게 되어, 오버레이를 늦춘 의미가 사라진다. 근거는 docs/ARCHITECTURE.md "파일 기록 시점".
internal static partial class OverlaySchedule
{
    // 펌프가 멈춰도(프레임 후크 오류 등) 파일 기록이 영영 막히지 않게 하는 여유. 정상 대기 상한보다 길어야 한다.
    private const long FileStallMarginUs = 60_000_000;

    private static readonly object FileGate = new();
    private static readonly SortedDictionary<long, (DateTime At, string Text)> FilePending = new();
    private static LogFileWriter? _file;
    private static long _fileKeys;

    public static void AttachFile(LogFileWriter? file)
    {
        lock (FileGate) _file = file;
    }

    private static long HoldForFile(string text)
    {
        if (_file is null) return 0;
        DateTime now = DateTime.Now;
        TimeSpan stall = TimeSpan.FromTicks((Math.Max(_maxLagUs, GateCapUs) + FileStallMarginUs) * 10);
        lock (FileGate)
        {
            LogFileWriter? file = _file;
            if (file is null) return 0;
            while (FilePending.Count > 0)
            {
                (long key, (DateTime At, string Text) oldest) = FilePending.First();
                if (now - oldest.At < stall) break;
                FilePending.Remove(key);
                file.AppendUnshown(oldest.Text, oldest.At);
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
