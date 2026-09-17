using System;
using BepInEx.Logging;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// 연출 동기화 조사용 기록(<c>Diagnostics.TraceTiming</c>). 줄마다 <b>벽시계</b>를 찍어
/// <c>battle-log.txt</c>의 시각과 바로 맞춰 볼 수 있게 한다.
/// </summary>
internal static class TimingTrace
{
    private static ManualLogSource? _log;

    public static bool Enabled { get; private set; }

    public static void Init(ManualLogSource log, bool enabled)
    {
        _log = log;
        Enabled = enabled;
    }

    public static void Write(string text)
    {
        if (Enabled) _log?.LogInfo($"[timing] {DateTime.Now:HH:mm:ss.fff} {text}");
    }
}
