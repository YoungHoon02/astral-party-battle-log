using System.Collections.Generic;

namespace AstralPartyBattleLog.Log;

/// <summary>
/// <c>Core.Net.RPCMsgManager</c>의 <c>if (cmdID == N)</c> 체인에서 추출한 값들.
/// 전체 289개 표는 프로젝트의 <c>docs/cmdid-table.tsv</c> 참고.
/// </summary>
internal static class Op
{
    public const int RoundStart = 1015;
    public const int GameFinish = 1016;
    public const int UpdateHeroAttr = 1040;
    public const int GameRoundChange = 1117;
    public const int MoveAgain = 5044;

    public const int Battle = 1007;

    public const int ActionStartNotify = 1026;

    public const int RunningGame = 1003;
    public const int StartGame = 5020;

    public const int MatchSuccess = 5226;
    public const int SingleCampaign = 5230;

    public const int MonsterRefresh = 1018;

    public const int BattleUseCard = 5036;

    public const int UseEffectCard = 5056;

    /// <summary>
    /// 5초마다 오는 하트비트 응답. 우리 요청의 응답이라 헤더 요청 번호가 0이 아니지만 화면과는
    /// 무관하다. 본문을 해석하지 않으므로 허용목록에 넣지 않는다.
    /// </summary>
    public const int Heartbeat = 5004;

    /// <summary>
    /// 칩을 골랐다. <b>획득은 이 메시지가 확정한다</b> — 게임도
    /// <c>RelicLogic.OnSelectRelicS2CServerCallBack</c>에서 <c>!IsReroll &amp;&amp; RelicId != 0</c>일 때만
    /// <c>UpdateSelectedRelic</c>을 부른다. 버프를 안 만드는 칩도 이 메시지는 온다.
    /// </summary>
    public const int SelectRelic = 5212;

    public const int ThrowDice = 5022;

    /// <summary>
    /// 맵 칸에 놓인 버프 = <b>소환물</b>. 칸 단위로 전체 목록이 오므로 diff를 떠야 한다
    /// (자세한 이유는 <c>BattleLogger.DecodeLandBuffs</c> 주석).
    /// </summary>
    public const int LandBuffs = 1013;

    /// <summary>
    /// 스킬이 이동 중에 일으킨 결과 묶음. 알맹이가 <c>UpdateHeroAttrS2C</c>라 같은
    /// 디코더를 쓴다 (<c>BattleLogger.DecodeSkillMoveEffect</c> 주석 참고).
    /// </summary>
    public const int HeroSkillMoveEffect = 1096;

    /// <summary>
    /// 본문을 디코딩해도 되는 메시지. <b>차단목록이 아니라 허용목록이다</b> — 이 집합
    /// 밖은 본문을 아예 파싱하지 않고, 그걸 읽는 디코더 코드도 존재하지 않는다.
    /// </summary>
    public static readonly HashSet<int> Allowed = new()
    {
        RoundStart,
        GameFinish,
        UpdateHeroAttr,
        GameRoundChange,
        ActionStartNotify,
        Battle,
        RunningGame,
        StartGame,
        MonsterRefresh,
        BattleUseCard,
        UseEffectCard,
        SelectRelic,
        ThrowDice,
        MoveAgain,
        LandBuffs,
        HeroSkillMoveEffect,
    };

    /// <summary>
    /// <b>opcode만 보는</b> 판 진입 신호. 셋 다 픽창을 여는 메시지인데 <c>StartGameS2C</c>는
    /// 사용자 방에서만 오므로, 매칭·싱글 판은 이것 없이는 새 판을 알 수 없다.
    /// 본문(방 전체 = 손패 포함)은 필요 없으므로 <see cref="Allowed"/>에 넣지 않는다 —
    /// 헤더의 cmdId와 ERR만 읽는다.
    /// </summary>
    public static readonly HashSet<int> GameEntry = new()
    {
        MatchSuccess,
        SingleCampaign,
    };

    public static string Name(int cmdId) => cmdId switch
    {
        RoundStart => "RoundStart",
        GameFinish => "GameFinish",
        UpdateHeroAttr => "UpdateHeroAttr",
        HeroSkillMoveEffect => "HeroSkillMoveEffect",
        GameRoundChange => "GameRoundChange",
        ActionStartNotify => "ActionStartNotify",
        Battle => "Battle",
        RunningGame => "RunningGame",
        StartGame => "StartGame",
        MatchSuccess => "MatchSuccess",
        SingleCampaign => "SingleCampaign",
        MonsterRefresh => "MonsterRefresh",
        BattleUseCard => "BattleUseCard",
        UseEffectCard => "UseEffectCard",
        SelectRelic => "SelectRelic",
        ThrowDice => "ThrowDice",
        MoveAgain => "MoveAgain",
        LandBuffs => "LandBuffs",
        _ => "?",
    };
}

/// <summary>
/// <c>Buff.Source</c>(필드 50)의 source → 표시 이름과 참조할 이름표.
/// <b><see cref="CauseSource"/>와 번호가 다른 별개의 열거형이다</b> — relic이 여기 6,
/// 저기 17이다. 섞어 쓰면 조용히 틀린 이름이 붙는다.
/// </summary>
internal static class BuffOrigin
{
    public static (string Label, string? Table) Describe(long s) => s switch
    {
        0 => ("", null),                  // unknown
        1 => ("스킬", "skill"),
        2 => ("카드", "card"),
        3 => ("이벤트", "event"),
        4 => ("소환", null),
        5 => ("운명", null),
        6 => ("칩", "relic"),
        7 => ("미션", null),
        8 => ("게임 모드", null),
        9 => ("럭키스타", null),
        10 => ("방 규칙", null),
        11 => ("레벨", null),
        _ => ("", null),
    };
}

/// <summary>
/// <c>CauseOrigin.source</c> → 표시 이름과 참조할 이름표. 이름표가 <c>null</c>인 종류는
/// <c>Id</c>가 설정 id가 아니라 런타임 고유값이라 로그에 찍지 않는다.
/// </summary>
internal static class CauseSource
{
    public static (string Label, string? Table) Describe(long s) => s switch
    {
        0 => ("", null),                  // unknown — 아예 출력하지 않는다
        1 => ("스킬", "skill"),
        2 => ("카드", "card"),
        3 => ("이벤트", "event"),
        5 => ("운명", null),
        6 => ("점술", null),
        7 => ("버프", null),              // heroBuff — id가 버프 인스턴스 uid
        8 => ("땅 버프", null),
        9 => ("땅", "land"),
        10 => ("폭탄", null),
        11 => ("라운드 보상", null),
        12 => ("전투", null),             // battle — id가 battleId(런타임)
        13 => ("상점 오픈", null),
        14 => ("상점 구매", null),
        15 => ("맵 이벤트", "event"),
        16 => ("라운드 종료", null),
        17 => ("칩", "relic"),   // 인게임 용어는 "칩"이다 (내부 테이블 이름만 relic)
        18 => ("미션", null),
        19 => ("현상금 처치", null),
        20 => ("현상금 방어", null),
        21 => ("보스 합체", null),
        22 => ("방 규칙", null),
        23 => ("럭키스타", null),
        _ => ($"원인{s}", null),
    };
}

/// <summary>
/// <c>DamageType</c> — HP 변화의 출처. <c>CauseOrigin.source</c>와 또 다른 열거형이라
/// 번호가 안 맞는다 (DamageType.Event=5, source.event=3). 같은 뜻인 짝은
/// <see cref="EquivalentCause"/>에 있다.
/// </summary>
internal static class DamageKind
{
    public static string Name(long t) => t switch
    {
        1 => "스킬", 2 => "카드", 3 => "운명", 4 => "점술", 5 => "이벤트",
        6 => "땅", 7 => "전투", 8 => "버프", 9 => "소환", 10 => "칩", 11 => "맵 이벤트",
        _ => $"종류{t}",
    };

    /// <summary>이 DamageType과 같은 뜻인 <c>CauseOrigin.source</c> 값들.</summary>
    public static long[] EquivalentCause(long t) => t switch
    {
        1 => new long[] { 1 },        // skill
        2 => new long[] { 2 },        // card
        3 => new long[] { 5 },        // destiny
        4 => new long[] { 6 },        // divination
        5 => new long[] { 3, 15 },    // event / map_event
        6 => new long[] { 8, 9 },     // landBuff / land
        7 => new long[] { 12 },       // battle
        8 => new long[] { 7, 8 },     // heroBuff / landBuff
        10 => new long[] { 17 },      // select_relic
        11 => new long[] { 15 },      // map_event
        _ => System.Array.Empty<long>(),
    };
}
