using System;
using System.Diagnostics;
using System.IO;
using BepInEx.Logging;

namespace AstralPartyBattleLog.Net;

// 새 릴리스 확인은 게임 밖 프로그램(Updater/)이 한다. 게임 프로세스는 띄우기만 하고 네트워크를 쓰지 않는다.
internal static class ReleaseCheck
{
    private const string UpdaterExe = "AstralPartyBattleLogUpdater.exe";

    public static string Current { get; } =
        typeof(ReleaseCheck).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : "?";

    public static void Start(ManualLogSource log, string pluginDir)
    {
        string exe = Path.Combine(pluginDir, UpdaterExe);
        if (!File.Exists(exe))
        {
            log.LogInfo($"[update] {UpdaterExe} not found; skipping the release check.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--auto",
                WorkingDirectory = pluginDir,
                UseShellExecute = true,
            });
            log.LogInfo("[update] release check started outside the game.");
        }
        catch (Exception e)
        {
            log.LogWarning($"[update] could not start {UpdaterExe}: {e.Message}");
        }
    }
}
