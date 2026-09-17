namespace AstralPartyBattleLog.Log;

/// <summary>
/// 오버레이에 언제 보여줄지를 가르는 사건 종류. 파일 로그에는 영향이 없다.
/// 종류별 지연과 분류 기준은 <c>docs/OVERLAY-TIMING.md</c>.
/// </summary>
internal enum LineKind
{
    Immediate,
    Round,
    Turn,
    Dice,
    Move,
    Attack,
    Hit,
    Card,
    Skill,
    Relic,
    Effect,
}
