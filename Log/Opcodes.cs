using System.Collections.Generic;

namespace AstralPartyBattleLog.Log;

/// <summary><c>Core.Net.RPCMsgManager</c>에서 추출한 cmdID. 전체 표는 <c>docs/cmdid-table.tsv</c>.</summary>
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

    public const int BattleChoice = 5040;
    public const int TimeWasting = 5308;

    public static readonly HashSet<int> TimingSync = new()
    {
        TimeWasting,
        BattleUseCard,
        BattleChoice,
    };

    public const int SelectRelic = 5212;
    public const int ThrowDice = 5022;
    public const int LandBuffs = 1013;
    public const int HeroSkillMoveEffect = 1096;

    // 차단목록이 아니라 허용목록이다. 이 밖의 본문은 파싱하지 않는다.
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

    // 헤더만 보는 판 진입 신호. 본문(방 전체)은 손패를 품고 있어 Allowed에 넣지 않는다.
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

// Buff.Source(50).source — CauseSource와 번호가 다른 열거형이다. 섞어 쓰면 조용히 틀린 이름이 붙는다.
internal static class BuffOrigin
{
    public static (string Label, string? Table) Describe(long s) => s switch
    {
        0 => ("", null),
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

// 이름표가 null인 종류는 id가 런타임 고유값이라 로그에 찍지 않는다.
internal static class CauseSource
{
    public static (string Label, string? Table) Describe(long s) => s switch
    {
        0 => ("", null),
        1 => ("스킬", "skill"),
        2 => ("카드", "card"),
        3 => ("이벤트", "event"),
        5 => ("운명", null),
        6 => ("점술", null),
        7 => ("버프", null),
        8 => ("땅 버프", null),
        9 => ("땅", "land"),
        10 => ("폭탄", null),
        11 => ("라운드 보상", null),
        12 => ("전투", null),
        13 => ("상점 오픈", null),
        14 => ("상점 구매", null),
        15 => ("맵 이벤트", "event"),
        16 => ("라운드 종료", null),
        17 => ("칩", "relic"),
        18 => ("미션", null),
        19 => ("현상금 처치", null),
        20 => ("현상금 방어", null),
        21 => ("보스 합체", null),
        22 => ("방 규칙", null),
        23 => ("럭키스타", null),
        _ => ($"원인{s}", null),
    };
}

// DamageType — CauseOrigin.source와 번호가 다른 열거형이다 (Event가 여기선 5, 저기선 3).
internal static class DamageKind
{
    public static string Name(long t) => t switch
    {
        1 => "스킬", 2 => "카드", 3 => "운명", 4 => "점술", 5 => "이벤트",
        6 => "땅", 7 => "전투", 8 => "버프", 9 => "소환", 10 => "칩", 11 => "맵 이벤트",
        _ => $"종류{t}",
    };

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
