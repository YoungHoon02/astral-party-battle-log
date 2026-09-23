using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AstralPartyBattleLog.UI;

// MonoBehaviour를 심을 수 없어 Time.deltaTime getter로 프레임 루프를 대신한다.
// getter는 한 프레임에 수십 번 불리므로 본문은 프레임 번호 비교로 끝나야 한다.
[HarmonyPatch]
internal static class FramePump
{
    private static int _lastFrame = -1;
    private static string _scene = "";

    public static string CurrentScene => _scene;
    public static Action<string>? OnSceneChanged;

    private static MethodBase TargetMethod() =>
        AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));

    private static void Postfix()
    {
        int frame = Time.frameCount;
        if (frame == _lastFrame) return;
        _lastFrame = frame;

        try
        {
            // 폴링은 안전하다. 금지된 건 sceneLoaded += 같은 IL2CPP 이벤트 구독이다.
            string scene = SceneManager.GetActiveScene().name ?? "";
            if (scene != _scene)
            {
                _scene = scene;
                ScreenProbe.OnScene();
                OnSceneChanged?.Invoke(scene);
            }
        }
        catch { }

        Log.NameHarvest.Tick();

        PumpSchedule();
        LogOverlay.Pump();
    }

    private static readonly Action<string> ShowLine = LogOverlay.Enqueue;
    private static readonly Action<int> ShowPage = LogOverlay.NewPage;
    private static readonly Action ClearOverlay = LogOverlay.GameStarted;

    // LogOverlay.Pump보다 먼저 불러야 같은 프레임에 그려진다.
    private static void PumpSchedule()
    {
        float delta, maxDelta, scale;
        try
        {
            delta = Time.unscaledDeltaTime;
            maxDelta = Time.maximumDeltaTime;
            scale = Time.timeScale;
        }
        catch
        {
            // 음수면 스케줄러가 실시간 시계로 대신한다. 시계가 멈추면 로그가 영영 안 나온다.
            delta = -1f;
            maxDelta = 0f;
            scale = 1f;
        }

        try
        {
            OverlaySchedule.Tick(delta, maxDelta, scale);
            if (OverlaySchedule.TraceTiming && Input.GetKeyDown(KeyCode.F10)) OverlaySchedule.Mark();
            OverlaySchedule.Pump(ShowLine, ShowPage, ClearOverlay);
        }
        catch { }
    }
}
