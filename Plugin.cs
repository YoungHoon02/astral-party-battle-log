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

[BepInPlugin(Guid, "Astral Party Battle Log", "0.1.6")]
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
            "오버레이 창 높이(보이는 줄 수). 창 크기는 이 값과 Width로 고정된다.");
        ConfigEntry<int> overlayFontSize = Config.Bind(
            "Overlay", "FontSize", 15, "오버레이 글자 크기.");
        ConfigEntry<int> overlayWidth = Config.Bind(
            "Overlay", "Width", 460,
            "오버레이 창 가로 폭(화면 높이 1080 기준). 창 크기는 이 값과 Lines로 고정되고, " +
            "이보다 긴 줄은 창 경계에서 잘린다.");
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

        // 이름표가 없으면 게임의 설정 에셋을 Addressables로 직접 불러와 만든다.
        // 여기서는 준비만 하고, 실제 로드와 완료 폴링은 프레임 펌프가 돌린다
        // (이유는 NameHarvest 주석 참고).
        NameHarvest.Arm(Log, names, namesPath, rebuildNames.Value);

        var logger = new BattleLogger(Log, path, traceFrames.Value, dumpSet,
                                      logPlayerIds.Value, logCards.Value, names);
        _logger = logger;
        SocketTap.OnFrame = logger.OnFrame;
        // 파일은 전용 스레드가 쓰므로 종료할 때 남은 줄을 마저 쓰게 한다.
        // 순수 .NET 이벤트라 IL2CPP 등록 문제를 일으키지 않는다(AnimSpeedMod와 같은 방식).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => logger.Close();

        ConfigEntry<bool> syncAnimation = Config.Bind(
            "Overlay", "SyncWithAnimation", true,
            "오버레이 줄을 게임 연출에 맞춰 늦게 보여준다. 파일 로그는 영향이 없다. " +
            "종류별 지연은 플러그인 폴더의 overlay-timing.tsv가 정한다(없으면 순서만 맞춘다).");
        ConfigEntry<int> syncOffsetMs = Config.Bind(
            "Overlay", "SyncOffsetMs", 0,
            "모든 연출 지연에 더하는 보정값(밀리초). 음수면 그만큼 일찍 보여준다.");
        ConfigEntry<int> syncMaxDelayMs = Config.Bind(
            "Overlay", "SyncMaxDelayMs", 8000,
            "한 줄이 수신 뒤 기다릴 수 있는 최대 시간(밀리초). 앞 줄이 밀려도 이보다 늦게 나오지 않는다.");
        ConfigEntry<bool> traceTiming = Config.Bind(
            "Diagnostics", "TraceTiming", false,
            "연출 지연을 재기 위한 기록을 BepInEx 로그에 남긴다. 켜면 F10을 눌러 " +
            "\"지금 화면에 연출이 끝났다\"를 표시할 수 있다. 측정이 끝나면 false로 둘 것.");

        if (showOverlay.Value)
        {
            if (!Enum.TryParse(overlayKey.Value, ignoreCase: true, out KeyCode toggle))
            {
                Log.LogWarning($"Unknown ToggleKey '{overlayKey.Value}'; falling back to F9.");
                toggle = KeyCode.F9;
            }
            LogOverlay.Init(Log, overlayLines.Value, overlayFontSize.Value, overlayWidth.Value,
                            toggle, scrollLines.Value,
                            new Vector2(overlayX.Value, overlayY.Value),
                            pos => SaveTogether(() => { overlayX.Value = pos.x; overlayY.Value = pos.y; }));
            OverlaySchedule.Init(Log, syncAnimation.Value, syncOffsetMs.Value, syncMaxDelayMs.Value,
                                 traceTiming.Value,
                                 Path.Combine(Paths.PluginPath, "AstralPartyBattleLog", "overlay-timing.tsv"));
            logger.Mirror = OverlaySchedule.Line;
            logger.MirrorNewPage = OverlaySchedule.Page;
            logger.MirrorClear = OverlaySchedule.Reset;
        }

        // 씬이 바뀌면 화면만 비운다. 로비로 나왔는데 전투 로그가 떠 있으면 방해되니까.
        //
        // 여기서 로거 상태(명단·라운드)까지 지우면 안 된다 — 씬 전환은 게임에
        // *들어갈* 때도 일어나서 방에서 받아둔 명단을 날려버린다. 그건 새 판이
        // 시작될 때(StartGame/MatchSuccess/SingleCampaign 신호) 로거가 스스로 한다.
        FramePump.OnSceneChanged = LogOverlay.OnSceneChanged;

        try
        {
            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(BeginReceivePatch));
            _harmony.PatchAll(typeof(EndReceivePatch));
            // 오버레이를 꺼도 씬 감지는 필요하므로 항상 건다.
            _harmony.PatchAll(typeof(FramePump));
            Log.LogInfo("Socket patches applied. Waiting for battle traffic.");
        }
        catch (Exception e)
        {
            // 패치 실패가 게임을 막으면 안 된다. 로그만 남기고 조용히 비활성화.
            Log.LogError($"Socket patching failed - no battle logging will happen: {e}");
            SocketTap.OnFrame = null;
        }

        if (path is not null) Log.LogInfo($"Battle log file: {path}");
    }

    /// <summary>값 두 개를 따로 대입하면 파일을 두 번 쓰므로 자동 저장을 잠시 끄고 한 번에 쓴다.</summary>
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
