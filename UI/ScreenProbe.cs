using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AstralPartyBattleLog.UI;

// GameObject.SetActive에서 화면 신호 오브젝트(주사위 눈·PK 창·타격·라운드 팁·위쪽 팁)의 전환을 골라
// OverlaySchedule로 넘긴다. 어떤 오브젝트를 왜 쓰는지는 docs/SIGNAL-GATING.md.
internal static class ScreenProbe
{
    // FairyGUI 패널이 한 프레임에 수백 개를 켜고 끌 때 이름 조회가 프레임을 끊지 않게 한다.
    // 조회하지 못한 포인터는 캐시하지 않으므로 다음 호출에서 다시 본다.
    private const int MaxNewNamesPerFrame = 1000;
    private const int MaxFailures = 20;
    // FairyGUI가 오브젝트를 만들 때 붙였다가 곧 패키지 이름으로 바꾸는 이름.
    private const string UnnamedComponent = "GComponent";

    private static readonly Regex DiceFace = new(@"^Dice_\d+_(\d+)", RegexOptions.Compiled);

    private static ManualLogSource? _log;
    private static Action<ScreenSignal, int, bool, IntPtr>? _signal;
    private static Action? _onDisabled;
    private static int _failures;

    private static readonly Dictionary<IntPtr, bool> Active = new();
    private static readonly Dictionary<IntPtr, (ScreenSignal Sig, int Pip)?> Tags = new();

    private static int _frame = -1;
    private static int _namesThisFrame;

    // 설치에 실패하면 false. 설치 뒤 오류가 반복돼 스스로 꺼질 때는 onDisabled를 부른다.
    public static bool Init(ManualLogSource log, Harmony harmony,
                            Action<ScreenSignal, int, bool, IntPtr>? signal, Action? onDisabled, bool record)
    {
        _log = log;
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
        if (record) ScreenRecorder.Init(log);
        return true;
    }

    // 포인터는 오브젝트가 파괴되면 재사용될 수 있어 씬마다 비운다.
    public static void OnScene(string scene)
    {
        Active.Clear();
        Tags.Clear();
        ScreenRecorder.OnScene(scene);
    }

    public static void NoteActive(GameObject go, bool value)
    {
        bool record = ScreenRecorder.Enabled;
        if (_signal is null && !record) return;
        try
        {
            IntPtr key = go.Pointer;
            if (!TryClassify(key, go, out (ScreenSignal Sig, int Pip)? tag)) return;
            if (tag is null && !record) return;
            if (Active.TryGetValue(key, out bool last) && last == value) return;
            Active[key] = value;
            if (tag is { } t) _signal?.Invoke(t.Sig, t.Pip, value, key);
            if (record) ScreenRecorder.Record(go, value);
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private static bool TryClassify(IntPtr key, GameObject go, out (ScreenSignal Sig, int Pip)? tag)
    {
        if (Tags.TryGetValue(key, out tag)) return true;
        int frame = Time.frameCount;
        if (frame != _frame)
        {
            _frame = frame;
            _namesThisFrame = 0;
        }
        if (_namesThisFrame >= MaxNewNamesPerFrame) return false;
        _namesThisFrame++;
        string name = go.name;
        tag = SignalOf(name);
        // 이름이 바뀌기 전에 "신호 아님"으로 캐시하면 라운드 팁·차례 배너를 끝내 못 잡는다.
        if (tag is not null || name != UnnamedComponent) Tags[key] = tag;
        return true;
    }

    private static (ScreenSignal, int)? SignalOf(string name)
    {
        if (name == "BattleShow(Clone)") return (ScreenSignal.Window, 0);
        // 3의 배수 라운드는 라운드 팁 대신 보상 팁을 띄운다.
        if (name is "Tips_Com_Common" or "Tips_Com_RoundReward") return (ScreenSignal.RoundTip, 0);
        // 차례 배너와 다른 팁이 같은 이름이라 가르는 것은 OverlaySchedule이 한다.
        if (name == "Tips_Com_Top") return (ScreenSignal.TopTip, 0);
        if (name.StartsWith("BattleShow_Atk", StringComparison.Ordinal)) return (ScreenSignal.Hit, 0);
        Match dice = DiceFace.Match(name);
        return dice.Success ? (ScreenSignal.DiceFace, int.Parse(dice.Groups[1].Value)) : null;
    }

    private static void Fail(Exception e)
    {
        if (++_failures < MaxFailures) return;
        ScreenRecorder.Disable();
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
