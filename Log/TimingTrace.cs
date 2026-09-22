using System;
using BepInEx.Logging;

namespace AstralPartyBattleLog.Log;

// battle-log.txt 시각과 맞춰 보려고 벽시계를 찍는다.
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
