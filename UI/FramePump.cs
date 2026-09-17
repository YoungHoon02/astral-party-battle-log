using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AstralPartyBattleLog.UI;

/// <summary>
/// 프레임당 한 번 <see cref="LogOverlay.Pump"/>를 돌리고 씬이 바뀌었는지 본다.
///
/// MonoBehaviour를 심을 수 없어서(<c>ClassInjector</c> = 확정 크래시) <c>Update</c>를
/// 만들 수 없다. 대신 게임이 매 프레임 부르는 AOT 메서드 <c>Time.deltaTime</c> getter에
/// Postfix를 걸고 <c>frameCount</c>로 중복을 걷어낸다 — getter는 한 프레임에 수십 번
/// 불리므로 본문은 프레임 번호 비교로 끝나야 한다.
///
/// <c>SceneManager.GetActiveScene()</c>은 <b>호출</b>이라 안전하다. 금지된 건
/// <c>sceneLoaded += ...</c> 같은 IL2CPP 이벤트 구독이다.
/// </summary>
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
            string scene = SceneManager.GetActiveScene().name ?? "";
            if (scene != _scene)
            {
                _scene = scene;
                OnSceneChanged?.Invoke(scene);
            }
        }
        catch
        {
            // 씬 조회가 실패해도 오버레이는 계속 돌아야 한다.
        }

        // 이름표가 아직 없으면 게임 설정 에셋에서 뽑아본다. 이미 있으면 즉시 빠져나간다.
        Log.NameHarvest.Tick();

        PumpSchedule();
        LogOverlay.Pump();
    }

    private static readonly Action<string> ShowLine = LogOverlay.Enqueue;
    private static readonly Action<int> ShowPage = LogOverlay.NewPage;
    private static readonly Action ClearOverlay = LogOverlay.GameStarted;

    /// <summary>
    /// 오버레이에 보낼 줄 중 연출 시점이 된 것만 내보낸다. <see cref="LogOverlay.Pump"/>보다
    /// 먼저 불러야 같은 프레임에 그려진다.
    ///
    /// Unity 시각을 못 읽어도 실시간 시계로 계속 흐르게 한다 — 시계가 멈추면 로그가
    /// 영영 안 나온다. 배속 연동이 실패해도 전투 로그는 살아 있어야 한다.
    /// </summary>
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
        catch
        {
            // 예약 출력이 실패해도 게임과 파일 로그는 계속 돌아야 한다.
        }
    }
}
