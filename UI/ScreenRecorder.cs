using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using UnityEngine;

namespace AstralPartyBattleLog.UI;

// Diagnostics.ScreenProbe: 신호 후보를 찾으려고 모든 활성 전환을 경로·시각과 함께 남긴다.
// 절차와 분석 스크립트는 docs/SIGNAL-GATING.md "라운드 전환 신호 후보 수집".
internal static class ScreenRecorder
{
    private const int MaxPathsPerFrame = 400;
    private const int MaxLinesPerFrame = 80;
    private const int MaxDepth = 12;
    private const string OwnRoot = "AstralPartyBattleLogOverlay";
    private const string PlayerCameraSuffix = "_VirtualCamera";

    private static readonly Regex DiceFace = new(@"^Dice_\d+_(\d+)", RegexOptions.Compiled);
    private static readonly Regex LongDigits = new(@"\d{6,}", RegexOptions.Compiled);

    private static ManualLogSource? _log;
    private static int _frame = -1;
    private static int _pathsThisFrame;
    private static int _linesThisFrame;
    private static long _budgetPath, _budgetLine;

    public static bool Enabled { get; private set; }

    public static void Init(ManualLogSource log)
    {
        _log = log;
        Enabled = true;
        log.LogInfo("[probe] recording every SetActive change");
    }

    public static void Disable() => Enabled = false;

    public static void OnScene(string scene)
    {
        if (Enabled) Write($"scene name={Mask(scene)} budgetPath={_budgetPath} budgetLine={_budgetLine}");
    }

    // FairyGUI는 숨긴 오브젝트를 부모에서 떼었다가 다시 붙여서, 경로를 캐시하면 떼어진 상태만 남는다.
    public static void Record(GameObject go, bool value)
    {
        int frame = Time.frameCount;
        if (frame != _frame)
        {
            _frame = frame;
            _pathsThisFrame = 0;
            _linesThisFrame = 0;
        }
        if (_pathsThisFrame >= MaxPathsPerFrame)
        {
            _budgetPath++;
            return;
        }
        _pathsThisFrame++;
        string? path = PathOf(go.transform);
        if (path is null) return;
        if (_linesThisFrame >= MaxLinesPerFrame)
        {
            _budgetLine++;
            return;
        }
        _linesThisFrame++;
        Write($"active={(value ? 1 : 0)} path={path}");
    }

    private static string? PathOf(Transform transform)
    {
        if (transform.root.name == OwnRoot) return null;
        var parts = new List<string>();
        Transform? t = transform;
        for (int i = 0; t != null && i < MaxDepth; i++, t = t.parent) parts.Add(Mask(t.name));
        parts.Reverse();
        return (t != null ? ".../" : "") + string.Join('/', parts);
    }

    // 플레이어별 카메라가 "<닉네임>_VirtualCamera"라 ASCII 닉네임도 새므로 따로 가린다.
    private static string Mask(string name)
    {
        if (name.EndsWith(PlayerCameraSuffix, StringComparison.Ordinal) && name.Length > PlayerCameraSuffix.Length)
            return "~" + PlayerCameraSuffix;
        Match dice = DiceFace.Match(name);
        if (dice.Success) return "Dice_" + dice.Groups[1].Value;
        foreach (char c in name)
            if (c > '~') return $"~{name.Length}";
        return LongDigits.Replace(name, "#");
    }

    private static void Write(string text) =>
        _log?.LogInfo($"[probe] {DateTime.Now:HH:mm:ss.fff} f={Time.frameCount} {text}");
}
