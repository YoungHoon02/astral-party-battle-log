using System;
using System.Collections.Generic;
using System.IO;
using AstralPartyBattleLog.Log;
using AstralPartyBattleLog.Net;
using AstralPartyBattleLog.UI;
using UnityEngine;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace AstralPartyBattleLog;

[BepInPlugin(Guid, "Astral Party Battle Log", "0.3.0")]
public class Plugin : BasePlugin
{
    public const string Guid = "astralparty.battlelog";

    private Harmony? _harmony;
    private BattleLogger? _logger;

    public override void Load()
    {
        ConfigEntry<bool> writeFile = Config.Bind(
            "Output", "WriteFile", true,
            "전투 로그를 플러그인 폴더의 battle-log.txt에도 남긴다.");

        ConfigEntry<bool> traceFrames = Config.Bind(
            "Diagnostics", "TraceFrames", false,
            "허용목록 밖 프레임의 opcode와 길이를 남긴다. 본문은 파싱하지 않는다. " +
            "파이프라인 점검용이며, 검증이 끝나면 false로 둘 것.");

        ConfigEntry<string> hexDump = Config.Bind(
            "Diagnostics", "HexDumpOpcodes", "",
            "본문을 hex로 덤프할 cmdID 목록(쉼표 구분). 디코딩이 왜 안 맞는지 볼 때만 쓴다. " +
            "허용목록 안의 opcode만 대상이 되므로 카드 메시지는 절대 덤프되지 않는다.");

        ConfigEntry<bool> logPlayerIds = Config.Bind(
            "Output", "LogPlayerIds", false,
            "참가자 줄에 계정 uid를 같이 남긴다. Player.Id는 매치용 슬롯이 아니라 영구 계정 " +
            "식별자라, 로그를 공유하면 남의 계정 정보가 같이 나간다. 디버깅할 때만 켤 것.");

        ConfigEntry<bool> logCards = Config.Bind(
            "Output", "LogCards", true,
            "공개된 카드를 남긴다. 종류까지 아는 건 보드에서 쓴 효과카드뿐이고, PK에 낸 카드는 " +
            "서버가 uid만 보내서 '카드 제출'까지만 남는다. 손패 변경(HeroAttrEffect.Card)은 " +
            "이 옵션과 무관하게 디코딩하지 않는다.");

        ConfigEntry<bool> showOverlay = Config.Bind(
            "Overlay", "Enabled", true,
            "전투 로그를 게임 화면에 겹쳐 보여준다.");
        ConfigEntry<int> overlayLines = Config.Bind(
            "Overlay", "Lines", 14,
            "오버레이 창 높이(보이는 줄 수). 가로 폭은 FontSize에 비례해 자동으로 정해진다.");
        ConfigEntry<int> overlayFontSize = Config.Bind(
            "Overlay", "FontSize", 15,
            "오버레이 글자 크기. 창 가로 폭도 이 값에 비례해 같이 커지고 작아진다.");
        ConfigEntry<string> overlayKey = Config.Bind(
            "Overlay", "ToggleKey", "F9",
            "오버레이를 켜고 끄는 키. UnityEngine.KeyCode 이름을 쓴다 (F9, BackQuote 등).");
        ConfigEntry<int> scrollLines = Config.Bind(
            "Overlay", "ScrollLines", 3,
            "마우스 휠 한 칸에 움직일 줄 수. 커서가 로그창 위에 있을 때만 동작한다.");
        ConfigEntry<float> overlayX = Config.Bind(
            "Overlay", "PositionX", LogOverlay.AutoPosition,
            "오버레이 왼쪽 위 모서리의 가로 위치(왼쪽에서부터). 화면 높이를 1080으로 둔 캔버스 " +
            "좌표이며, 창 왼쪽 위 손잡이를 끌면 저장된다. 화면 밖 값은 표시할 때 안쪽으로 " +
            "보정된다. -1이면 화면 왼쪽 아래 기본 위치에 띄운다.");
        ConfigEntry<float> overlayY = Config.Bind(
            "Overlay", "PositionY", LogOverlay.AutoPosition,
            "오버레이 왼쪽 위 모서리의 세로 위치(위에서부터, 1080 기준). -1이면 기본 위치.");

        string? path = null;
        if (writeFile.Value)
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "AstralPartyBattleLog");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "battle-log.txt");
            }
            catch (Exception e)
            {
                Log.LogWarning($"Could not prepare the log file; overlay only: {e.Message}");
            }
        }

        var dumpSet = new HashSet<int>();
        foreach (string part in hexDump.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part.Trim(), out int op) && Op.Allowed.Contains(op)) dumpSet.Add(op);

        ConfigEntry<bool> rebuildNames = Config.Bind(
            "Names", "Rebuild", false,
            "다음 실행에서 names.tsv를 다시 만든다. 게임이 업데이트돼 카드/스킬 이름이 " +
            "어긋날 때 켠다. 파일을 지워도 같은 효과다.");

        string namesPath = Path.Combine(Paths.PluginPath, "AstralPartyBattleLog", "names.tsv");
        var names = NameTable.Load(namesPath, Log.LogWarning);
        if (names.Count > 0) Log.LogInfo($"Loaded {names.Count} names.");

        NameHarvest.Arm(Log, names, namesPath, rebuildNames.Value);

        string cachePath = Path.Combine(Paths.PluginPath, "AstralPartyBattleLog", "roster-cache.tsv");
        var cache = new RosterCache(cachePath, Log.LogWarning);

        var logger = new BattleLogger(Log, path, traceFrames.Value, dumpSet,
                                      logPlayerIds.Value, logCards.Value, names, cache);
        _logger = logger;
        SocketTap.OnFrame = logger.OnFrame;
        // 순수 .NET 이벤트라 IL2CPP 델리게이트 등록 금지에 걸리지 않는다.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => logger.Close();

        ConfigEntry<bool> syncAnimation = Config.Bind(
            "Overlay", "SyncWithAnimation", true,
            "오버레이 줄을 게임 화면이 그 장면에 도달할 때 보여준다. 게임은 연출을 차례대로 " +
            "재생하므로 봇 차례가 이어지면 화면이 수십 초 뒤처진다. 주사위·PK 결과는 화면에 주사위 눈이나 " +
            "PK 타격이 보이는 순간에 맞추고, 나머지 줄은 연출 길이로 추정한다. 화면 신호가 없는 몬스터 " +
            "주사위는 다음 신호까지 늦게 뜰 수 있다. 끄면 받는 즉시 보여준다. 파일 로그는 영향이 없다.");
        ConfigEntry<int> syncMaxLagMs = Config.Bind(
            "Overlay", "SyncMaxLagMs", 60000,
            "화면 재생 덩어리가 수신보다 뒤처져 시작할 수 있는 최대 시간(밀리초). " +
            "실측 최대 지연은 약 37초였다.");
        ConfigEntry<bool> traceTiming = Config.Bind(
            "Diagnostics", "TraceTiming", false,
            "화면 지연을 재기 위한 기록(수신 프레임 헤더, PK 진행, 표시 시각)을 BepInEx 로그에 남긴다. " +
            "켜면 F10으로 \"지금 화면에서 본 장면\"의 시각을 남길 수 있다. 측정이 끝나면 false로 둘 것.");
        ConfigEntry<bool> screenProbe = Config.Bind(
            "Diagnostics", "ScreenProbe", false,
            "화면 신호 후보를 찾기 위해 모든 GameObject 활성 전환(경로·시각)을 BepInEx 로그에 남긴다. " +
            "TraceTiming과 같이 켜면 패킷 수신 시각과 대조할 수 있다. 부하가 크므로 측정이 끝나면 false로 둘 것.");

        if (showOverlay.Value)
        {
            if (!Enum.TryParse(overlayKey.Value, ignoreCase: true, out KeyCode toggle))
            {
                Log.LogWarning($"Unknown ToggleKey '{overlayKey.Value}'; falling back to F9.");
                toggle = KeyCode.F9;
            }
            LogOverlay.Init(Log, overlayLines.Value, overlayFontSize.Value,
                            toggle, scrollLines.Value,
                            new Vector2(overlayX.Value, overlayY.Value),
                            pos => SaveTogether(() => { overlayX.Value = pos.x; overlayY.Value = pos.y; }));
            OverlaySchedule.Init(Log, syncAnimation.Value, syncMaxLagMs.Value, traceTiming.Value);
            logger.Mirror = OverlaySchedule.Line;
            logger.MirrorAdvance = OverlaySchedule.Advance;
            logger.MirrorNewPage = OverlaySchedule.Page;
            logger.MirrorClear = OverlaySchedule.Reset;
            logger.MirrorSync = OverlaySchedule.Sync;
        }

        // 로거 상태는 씬 전환에 걸지 않는다. 게임에 들어갈 때도 씬이 바뀌어 명단이 날아간다.
        FramePump.OnSceneChanged = LogOverlay.OnSceneChanged;
        LogOverlay.OnLeftGame = logger.LeftGame;

        try
        {
            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(BeginReceivePatch));
            _harmony.PatchAll(typeof(EndReceivePatch));
            // 오버레이를 꺼도 씬 감지·이름표 수집에 필요하다.
            _harmony.PatchAll(typeof(FramePump));
            Log.LogInfo("Socket patches applied. Waiting for battle traffic.");
            bool gating = OverlaySchedule.UsesScreenSignals;
            if ((gating || screenProbe.Value)
                && !ScreenProbe.Init(Log, _harmony, gating ? OverlaySchedule.Screen : null,
                                     gating ? OverlaySchedule.FallBackToModel : null, screenProbe.Value)
                && gating)
            {
                OverlaySchedule.FallBackToModel();
                Log.LogWarning("Screen signal hook failed; overlay falls back to estimated timing.");
            }
        }
        catch (Exception e)
        {
            Log.LogError($"Socket patching failed - no battle logging will happen: {e}");
            SocketTap.OnFrame = null;
        }

        if (path is not null) Log.LogInfo($"Battle log file: {path}");
    }

    // 값 두 개를 따로 대입하면 설정 파일을 두 번 쓴다.
    private void SaveTogether(Action assign)
    {
        bool auto = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        try
        {
            assign();
        }
        finally
        {
            Config.SaveOnConfigSet = auto;
        }
        Config.Save();
    }

    public override bool Unload()
    {
        SocketTap.OnFrame = null;
        OverlaySchedule.Discard();
        _logger?.Close();
        _harmony?.UnpatchSelf();
        return true;
    }
}
