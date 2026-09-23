using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AstralPartyBattleLog.UI;

// GameObject.SetActive에서 주사위 눈·PK 창·타격 오브젝트의 전환을 골라 OverlaySchedule로 넘긴다.
// 어떤 오브젝트를 왜 쓰는지는 docs/SIGNAL-GATING.md.
internal static class ScreenProbe
{
    // FairyGUI 패널이 한 프레임에 수백 개를 켜고 끌 때 이름 조회가 프레임을 끊지 않게 한다.
    // 조회하지 못한 포인터는 캐시하지 않으므로 다음 호출에서 다시 본다.
    private const int MaxNewNamesPerFrame = 1000;
    private const int MaxFailures = 20;
    private const int MaxPathsPerFrame = 400;
    private const int MaxLinesPerFrame = 80;
    private const int MaxDepth = 12;
    private const string OwnRoot = "AstralPartyBattleLogOverlay";
    private const string PlayerCameraSuffix = "_VirtualCamera";

    private static readonly Regex DiceFace = new(@"^Dice_\d+_(\d+)", RegexOptions.Compiled);
    private static readonly Regex LongDigits = new(@"\d{6,}", RegexOptions.Compiled);

    private static ManualLogSource? _log;
    private static Action<ScreenSignal, int, bool, IntPtr>? _signal;
    private static Action? _onDisabled;
    private static int _failures;
    private static bool _discover;

    private static readonly Dictionary<IntPtr, bool> Active = new();
    private static readonly Dictionary<IntPtr, (ScreenSignal Sig, int Pip)?> Tags = new();

    private static int _frame = -1;
    private static int _namesThisFrame;
    private static int _pathsThisFrame;
    private static int _linesThisFrame;
    private static long _budgetPath, _budgetLine;


    // 설치에 실패하면 false. 설치 뒤 오류가 반복돼 스스로 꺼질 때는 onDisabled를 부른다.
    // discover는 신호 후보를 찾는 진단 기록이다(docs/SIGNAL-GATING.md "라운드 전환 신호 후보 수집").
    public static bool Init(ManualLogSource log, Harmony harmony,
                            Action<ScreenSignal, int, bool, IntPtr>? signal, Action? onDisabled, bool discover)
    {
        _log = log;
        _discover = discover;
        try
        {
            harmony.PatchAll(typeof(ProbeSetActivePatch));
        }
        catch (Exception e)
        {
            log.LogWarning($"[screen] could not patch GameObject.SetActive: {e.Message}");
            return false;
        }
        _signal = signal;
        _onDisabled = onDisabled;
        if (discover) log.LogInfo("[probe] recording every SetActive change");
        return true;
    }

    // 포인터는 오브젝트가 파괴되면 재사용될 수 있어 씬마다 비운다.
    public static void OnScene(string scene)
    {
        Active.Clear();
        Tags.Clear();
        if (_discover)
            Write($"scene name={Mask(scene)} budgetPath={_budgetPath} budgetLine={_budgetLine}");
    }

    public static void NoteActive(GameObject go, bool value)
    {
        if (_signal is null && !_discover) return;
        try
        {
            IntPtr key = go.Pointer;
            if (!TryClassify(key, go, out (ScreenSignal Sig, int Pip)? tag)) return;
            if (tag is null && !_discover) return;
            if (Active.TryGetValue(key, out bool last) && last == value) return;
            Active[key] = value;
            if (tag is { } t) _signal?.Invoke(t.Sig, t.Pip, value, key);
            if (_discover) Record(go, value);
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private static bool TryClassify(IntPtr key, GameObject go, out (ScreenSignal Sig, int Pip)? tag)
    {
        if (Tags.TryGetValue(key, out tag)) return true;
        RollFrame();
        if (_namesThisFrame >= MaxNewNamesPerFrame) return false;
        _namesThisFrame++;
        tag = SignalOf(go.name);
        Tags[key] = tag;
        return true;
    }

    private static (ScreenSignal, int)? SignalOf(string name)
    {
        if (name == "BattleShow(Clone)") return (ScreenSignal.Window, 0);
        if (name.StartsWith("BattleShow_Atk", StringComparison.Ordinal)) return (ScreenSignal.Hit, 0);
        Match dice = DiceFace.Match(name);
        return dice.Success ? (ScreenSignal.DiceFace, int.Parse(dice.Groups[1].Value)) : null;
    }

    private static void Record(GameObject go, bool value)
    {
        RollFrame();
        // FairyGUI는 숨긴 오브젝트를 부모에서 떼었다가 다시 붙인다. 처음 본 경로를 캐시하면 떼어진 상태의
        // 이름("GComponent")만 남아 후보를 알아볼 수 없어서 매번 다시 잰다.
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

    // 오브젝트 이름에 닉네임이 들어갈 수 있다. 플레이어별 카메라는 "<닉네임>_VirtualCamera"라 ASCII 닉네임도
    // 새므로 따로 가리고, ASCII 밖의 글자가 섞인 이름과 긴 숫자를 가린다.
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

    private static void RollFrame()
    {
        int frame = Time.frameCount;
        if (frame == _frame) return;
        _frame = frame;
        _namesThisFrame = 0;
        _pathsThisFrame = 0;
        _linesThisFrame = 0;
    }

    private static void Write(string text) =>
        _log?.LogInfo($"[probe] {DateTime.Now:HH:mm:ss.fff} f={Time.frameCount} {text}");

    private static void Fail(Exception e)
    {
        if (++_failures < MaxFailures) return;
        _discover = false;
        _signal = null;
        _log?.LogWarning($"[screen] disabled after {_failures} errors: {e.Message}");
        _onDisabled?.Invoke();
    }
}

[HarmonyPatch]
internal static class ProbeSetActivePatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive), new[] { typeof(bool) });

    private static void Postfix(GameObject __instance, bool __0) => ScreenProbe.NoteActive(__instance, __0);
}
