# 디코딩하는 메시지의 필드 배치

이 문서는 프로토콜 디코더를 수정하거나 검증하는 개발자를 위한 필드 참조표입니다.
사용 방법과 일반적인 동작은 [README](../README.md)를 확인해 주세요.

`Log/BattleLogger.cs`는 protobuf를 손으로 읽는다. **필드 번호나 wire type을 잘못 잡으면
예외 없이 조용히 0이 나오거나 필드가 통째로 건너뛰어진다.** 그래서 디코더를 고치기 전에
이 표와 대조한다.

이 표는 코드 주석이 아니라 여기에 둔다 — 디코더가 읽는 필드는 코드가 이미 말하고,
표가 보태는 것은 **읽지 않는 필드의 존재**와 **모양(packed / map / message)**이다.

## 다시 뽑는 법

원본은 `ref/AstralParty.Runtime.dll`이고(리포에 커밋하지 않는다), 아래로 다시 만든다.

```bash
ilspycmd -p -o <디컴파일루트> ref/AstralParty.Runtime.dll
python tools/dump_tags.py <디컴파일루트> HeroHpChangeS2C
```

`REPEATED`/`MAP`으로 나오는 필드는 **길이형(wire 2)**이다. 숫자 wire만 받는 루프에서
`case N:`으로 받고 있으면 그 필드는 한 번도 읽힌 적이 없다는 뜻이다.

## 읽지 않기로 한 필드

허용목록과 같은 이유로 **일부러 디코더를 두지 않은** 것들이다
([CONTRIBUTING.md](../CONTRIBUTING.md)의 "카드에 그은 선").

| 메시지 | 필드 | 왜 안 읽나 |
| --- | --- | --- |
| `HeroAttrEffect` | 9 `Card` | 손패 변경. 마스킹된 `-1`이 오는 곳이다 |
| `BattleRole` | 5 `UseCards` | 값이 카드 uid라 종류를 알 수 없다 (packed sfixed32) |
| `Battle` | 4 `CardUseState` | `map<long, bool>` — 누가 냈는지만 담긴 UI 상태 |
| `Player` | 10 `Hero`의 8 `Cards` | 손패. `Hero`에서는 `HeroId`(2)만 꺼낸다 |
| `HeroHpChangeS2C` | — | 전부 읽는다 (5 `RealChangeHp`, 6 `RealHp` 포함) |

## 메시지별로 알아둘 것

코드에서 뺀 사실들이다. 필드 번호만으로는 알 수 없고, 잘못 알면 조용히 틀린다.

| 메시지 | 사실 |
| --- | --- |
| `MonsterRefreshS2C`(1018) | **몹 명단은 `Room`이 아니라 여기서 온다.** Room만 읽으면 전투 상대가 전부 `?id`로 남는다 |
| `BattleUseCardS2C`(5036) | 공개 전에는 `CardId == 0`으로 온다. 게임도 그때만 "준비 완료" 표시를 띄운다(`FightLogic`) |
| 〃 | **한 PK에 여러 장 낼 수 있다** (`RoundStartS2C.UseCardMaxNum`). 실측에서 한 사람이 3장을 냈다 — 제출 줄을 묶으면 안 되고, 같은 `CardId`(uid)가 다시 올 때만 막는다 |
| `SelectRelicS2C`(5212) | 칩 획득의 확정 신호. `!IsReroll && RelicId != 0` (`RelicLogic`) |
| `HeroHpChangeS2C`(1040 안) | 화면 숫자는 `RealChangeHp`, 실제 HP는 `RealHp`(0이면 `HP + RealChangeHp`) — `BattleProperty.OnLifeChanged` |
| 〃 | **최대 체력에서 회복을 받아도 `RealChangeHp`가 0이 아니다** (실측 10건, 전부 `CurrHp == MaxHp`). `CurrHp`와 `RealHp` 중 무엇이 화면 HP인지는 미확정 |
| `HeroBuffChangeS2C` | 한 메시지에 동일한 스킬·대상 버프가 여러 번 들어올 수 있다. 대상이 다르면 각각 남기고, 같은 `(스킬, 대상)`만 중복 제거한다 |
| `TimeWastingS2C`(5308) | 결정 창 열림·닫힘 때 응답으로 온다. 측정 3에서 `UPSN != 0`, 본문 길이는 열림 14바이트·닫힘 9바이트였다. 동기화에는 헤더만 사용하고 본문은 해석하지 않는다 |
| `HeartbeatS2C`(5004) | 주기적인 응답이라 `UPSN != 0`이어도 화면 동기화 근거가 아니다. 본문을 해석하거나 허용목록에 넣지 않는다 |

프레임 헤더의 `UPSN`은 우리 요청에 대응하는 응답 번호다(`FrameReassembler`가 읽는다).
오버레이는 `UPSN != 0`이면서 화면 단계와의 대응을 관측한 5308·5036·5040 응답만
동기화 신호로 사용한다. `UPSN` 자체는 **사용자 입력을 증명하지 않는다.** 이 검사는
본문 디코딩 범위와 `Op.Allowed`를 바꾸지 않는다.

## 표

```
===== RoundStartS2C =====
    1 Round                  fix32  WriteSFixed32  Round
                             └ 선언: int
    2 SkillCds               len    MAP            map<int, int> entry{1:fix32 key, 2:fix32 val}
                             └ 선언: MapField<int, int>
    3 Gold                   fix32  WriteSFixed32  Gold
                             └ 선언: int
    4 CardNum                fix32  WriteSFixed32  CardNum
                             └ 선언: int
    5 PlayerId               fix64  WriteSFixed64  PlayerId
                             └ 선언: long
    6 UseCardMaxNum          fix32  WriteSFixed32  UseCardMaxNum
                             └ 선언: int

===== ActionStartNotifyS2C =====
    1 PlayerId               fix64  WriteSFixed64  PlayerId
                             └ 선언: long
    2 IsDie                  varint WriteBool      IsDie
                             └ 선언: bool
    3 IsHospital             varint WriteBool      IsHospital
                             └ 선언: bool
    4 IsStopRound            varint WriteBool      IsStopRound
                             └ 선언: bool

===== ThrowDiceS2C =====
    1 Vals                   len    REPEATED       SFixed32
                             └ 선언: RepeatedField<int>
    2 MovePoint              fix32  WriteSFixed32  MovePoint
                             └ 선언: int
    3 PlayerId               fix64  WriteSFixed64  PlayerId
                             └ 선언: long
    4 ForceDir               varint WriteBool      ForceDir
                             └ 선언: bool
    5 IsControlMovePoint     varint WriteBool      IsControlMovePoint
                             └ 선언: bool

===== MoveAgainS2C =====
    1 PlayerId               fix64  WriteSFixed64  PlayerId
                             └ 선언: long
    2 MovePoint              fix32  WriteSFixed32  MovePoint
                             └ 선언: int

===== BattleUseCardS2C =====
    1 PlayerId               fix64  WriteSFixed64  PlayerId
                             └ 선언: long
    2 CardId                 fix32  WriteSFixed32  CardId
                             └ 선언: int
    3 NoCard                 varint WriteBool      NoCard
                             └ 선언: bool

===== UseEffectCardS2C =====
    1 PlayerId               fix64  WriteSFixed64  PlayerId
                             └ 선언: long
    2 CardId                 fix32  WriteSFixed32  CardId
                             └ 선언: int
    3 TargetIds              len    REPEATED       SFixed64
                             └ 선언: RepeatedField<long>
    4 TargetNodeIds          len    REPEATED       SFixed32
                             └ 선언: RepeatedField<int>
    8 UseSkill               varint WriteBool      UseSkill
                             └ 선언: bool
    9 SkillId                fix32  WriteSFixed32  SkillId
                             └ 선언: int
   10 SkillCds               len    MAP            map<int, int> entry{1:fix32 key, 2:fix32 val}
                             └ 선언: MapField<int, int>
   11 EnterReverse           varint WriteBool      EnterReverse
                             └ 선언: bool

===== SelectRelicS2C =====
    1 PlayerId               fix64  WriteSFixed64  PlayerId
                             └ 선언: long
    2 RelicId                fix32  WriteSFixed32  RelicId
                             └ 선언: int
    3 IsReroll               varint WriteBool      IsReroll
                             └ 선언: bool

===== UpdateHeroAttrS2C =====
    1 PlayerId               fix64  WriteSFixed64  PlayerId
                             └ 선언: long
    2 Cause                  len    WriteMessage   (IMessage
                             └ 선언: CauseOrigin
    4 EffectDatas            len    REPEATED       Message<HeroAttrEffect>
                             └ 선언: RepeatedField<HeroAttrEffect>

===== CauseOrigin =====
    1 S                      varint WriteEnum      (int
                             └ 선언: Types.source
    3 Id                     fix64  WriteSFixed64  Id
                             └ 선언: long

===== HeroSkillMoveEffectS2C =====
    1 PlayerId               fix64  WriteSFixed64  PlayerId
                             └ 선언: long
    2 EffectData             len    MAP            map<int, HeroSkillMoveEffect> entry{1:fix32 key, 2:len val}
                             └ 선언: MapField<int, HeroSkillMoveEffect>

===== LandBuffsS2C =====
    1 Buffs                  len    REPEATED       Message<Types.LandBuffsWrap>
                             └ 선언: RepeatedField<Types.LandBuffsWrap>

===== LandBuffsWrap =====
    1 NodeId                 fix32  WriteSFixed32  NodeId
                             └ 선언: int
    2 BuffArr                len    WriteMessage   (IMessage
                             └ 선언: BuffArray

===== HeroGoldChangeS2C =====
    1 PlayerId               fix64  WriteSFixed64  PlayerId
                             └ 선언: long
    2 ChangeGold             fix32  WriteSFixed32  ChangeGold
                             └ 선언: int
    3 OriGold                fix32  WriteSFixed32  OriGold
                             └ 선언: int
    4 CurrGold               fix32  WriteSFixed32  CurrGold
                             └ 선언: int

===== HeroAtkChangeS2C =====
    1 PlayerId               fix64  WriteSFixed64  PlayerId
                             └ 선언: long
    5 CurrAtk                fix32  WriteSFixed32  CurrAtk
                             └ 선언: int

===== HeroBuffChangeS2C =====
    1 PlayerId               fix64  WriteSFixed64  PlayerId
                             └ 선언: long
    2 Buff                   len    WriteMessage   (IMessage
                             └ 선언: Buff
    3 Op                     varint WriteEnum      (int
                             └ 선언: Types.Oper
    4 Buffs                  len    MAP            map<long, Buff> entry{1:fix64 key, 2:len val}
                             └ 선언: MapField<long, Buff>


===== Battle =====
    1 BattleId               fix64  WriteSFixed64  BattleId
                             └ 선언: long
    2 Attacker               len    WriteMessage   (IMessage
                             └ 선언: BattleRole
    3 Defender               len    WriteMessage   (IMessage
                             └ 선언: BattleRole
    4 CardUseState           len    MAP            map<long, bool> entry{1:fix64 key, 2:varint val}
                             └ 선언: MapField<long, bool>
    5 IsEnd                  varint WriteBool      IsEnd
                             └ 선언: bool
    6 IfNoWinMustDie         varint WriteBool      IfNoWinMustDie
                             └ 선언: bool
    7 IsPursuit              varint WriteBool      IsPursuit
                             └ 선언: bool
    8 FightBack              varint WriteBool      FightBack
                             └ 선언: bool
    9 SkillPlayerId          fix64  WriteSFixed64  SkillPlayerId
                             └ 선언: long

===== BattleRole =====
    1 PlayerId               fix64  WriteSFixed64  PlayerId
                             └ 선언: long
    2 Atk                    fix32  WriteSFixed32  Atk
                             └ 선언: int
    3 Def                    fix32  WriteSFixed32  Def
                             └ 선언: int
    5 UseCards               len    REPEATED       SFixed32
                             └ 선언: RepeatedField<int>
    6 Point                  fix32  WriteSFixed32  Point
                             └ 선언: int
    7 Dodge                  varint WriteBool      Dodge
                             └ 선언: bool
    8 IncHp                  fix32  WriteSFixed32  IncHp
                             └ 선언: int
    9 Cost                   fix32  WriteSFixed32  Cost
                             └ 선언: int
   10 DropGold               fix32  WriteSFixed32  DropGold
                             └ 선언: int
   11 MaxCost                fix32  WriteSFixed32  MaxCost
                             └ 선언: int
   12 CanNotFightBack        varint WriteBool      CanNotFightBack
                             └ 선언: bool
   13 HeroId                 fix32  WriteSFixed32  HeroId
                             └ 선언: int
   14 AttackBonus            fix32  WriteSFixed32  AttackBonus
                             └ 선언: int
   15 ChainAttacker          fix64  WriteSFixed64  ChainAttacker
                             └ 선언: long
   16 ChainAttackDamage      fix32  WriteSFixed32  ChainAttackDamage
                             └ 선언: int
   17 MaxAtk                 fix32  WriteSFixed32  MaxAtk
                             └ 선언: int
   18 MaxDef                 fix32  WriteSFixed32  MaxDef
                             └ 선언: int
   19 MinAtk                 fix32  WriteSFixed32  MinAtk
                             └ 선언: int
   20 MinDef                 fix32  WriteSFixed32  MinDef
                             └ 선언: int
   21 InitAtk                fix32  WriteSFixed32  InitAtk
                             └ 선언: int
   22 InitDef                fix32  WriteSFixed32  InitDef
                             └ 선언: int
   23 CardCombatBonus        len    REPEATED       Message<cardCombat>
                             └ 선언: RepeatedField<cardCombat>

===== Buff =====
    1 UniqueId               fix64  WriteSFixed64  UniqueId
                             └ 선언: long
    2 BuffId                 fix32  WriteSFixed32  BuffId
                             └ 선언: int
    3 Params                 len    MAP            map<int, long> entry{1:fix32 key, 2:fix64 val}
                             └ 선언: MapField<int, long>
    4 RestoreParams          len    MAP            map<int, long> entry{1:fix32 key, 2:fix64 val}
                             └ 선언: MapField<int, long>
    5 KeepRound              fix32  WriteSFixed32  KeepRound
                             └ 선언: int
    6 DelayRound             fix32  WriteSFixed32  DelayRound
                             └ 선언: int
    7 UseTime                fix32  WriteSFixed32  UseTime
                             └ 선언: int
    8 TargetIds              len    REPEATED       SFixed64
                             └ 선언: RepeatedField<long>
    9 Priority               fix32  WriteSFixed32  Priority
                             └ 선언: int
   10 NodeId                 fix32  WriteSFixed32  NodeId
                             └ 선언: int
   11 Progress               fix32  WriteSFixed32  Progress
                             └ 선언: int
   12 RelationId             len    WriteString    RelationId
                             └ 선언: string
   13 IsSakura               varint WriteBool      IsSakura
                             └ 선언: bool
   14 BuffIndex              fix32  WriteSFixed32  BuffIndex
                             └ 선언: int
   15 CanRangeDamage         varint WriteBool      CanRangeDamage
                             └ 선언: bool
   18 RelativeBuffUids       len    MAP            map<int, long> entry{1:fix32 key, 2:fix64 val}
                             └ 선언: MapField<int, long>
   49 Chain                  len    REPEATED       Message<buff_source>
                             └ 선언: RepeatedField<buff_source>
   50 Source                 len    WriteMessage   (IMessage
                             └ 선언: buff_source
   51 Key                    len    WriteString    Key
                             └ 선언: string

===== HeroCureNumChangeS2C =====
    1 PlayerId               fix64  WriteSFixed64  PlayerId
                             └ 선언: long
    2 ChangeNum              fix32  WriteSFixed32  ChangeNum
                             └ 선언: int
    3 OriNum                 fix32  WriteSFixed32  OriNum
                             └ 선언: int
    4 CurrNum                fix32  WriteSFixed32  CurrNum
                             └ 선언: int

===== MonsterRefreshS2C =====
    1 Monster                len    WriteMessage   (IMessage
                             └ 선언: Player
```
