namespace AstralPartyBattleLog.Log;

/// <summary>
/// 오버레이 재생에서 화면을 얼마나 차지하는지를 가르는 사건 종류. 파일 로그에는 영향이 없다.
/// 종류별 연출 길이와 분류 기준은 <c>docs/OVERLAY-TIMING.md</c>.
/// </summary>
internal enum LineKind
{
    Immediate,
    Round,
    Turn,
    Card,
    Dice,
    Move,
    Submit,
    Attack,
    Hit,
    Skill,
    Land,
    Relic,
    Effect,
}
