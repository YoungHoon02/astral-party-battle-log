# 아키텍처 및 제약

현재 구현 구조와 변경 시 지켜야 할 기술 제약을 정리한 개발자용 문서입니다.
일반적인 BepInEx 모딩과 전제가 다르므로 코드를 수정하기 전에 먼저 읽어 주세요.

기여 규칙과 지켜야 할 경계는 [CONTRIBUTING.md](../CONTRIBUTING.md)에 따로 있다.
조사 과정은 [FINDINGS.md](FINDINGS.md), 현재 구현과 다를 수 있는 초기 설계는
[LOGGER-DESIGN.md](LOGGER-DESIGN.md)에 보관되어 있습니다.

---

## 이 게임의 가장 중요한 제약

**게임 로직에 Harmony를 걸 수 없다.** 게임 코드는 `AstralParty.Runtime`이라는
HybridCLR 핫업데이트 어셈블리(타입 9,803개)이고, `Assembly-CSharp.dll`에는 런처밖에
없다. 이유는 두 겹이다 — interop stub이 없어 `typeof(X)`를 쓸 수 없고, HybridCLR
인터프리터의 `methodPointer`가 시그니처별 공유 브릿지라 디투어가 무관한 메서드까지
잡는다.

**그래서 AOT 계층만 패치한다.** 이 플러그인은 `Il2CppSystem.Net.Sockets.Socket`의
`BeginReceive`/`EndReceive`만 Postfix로 건드린다. 오버레이와 `Overlay.SyncWithAnimation`이 켜져
있으면 `GameObject.SetActive`에 화면 신호용 Postfix가 추가된다. 설치에 실패하면 연출 길이 추정으로 동작한다([SIGNAL-GATING.md](SIGNAL-GATING.md)).
측정 4에서 쓰던 텍스트 setter 후킹과 오브젝트 전수 기록은 제거했다.

## 절대 하면 안 되는 것 (크래시 확정)

같은 게임의 AnimSpeed 모드에서 실측으로 확인된 것 — Il2CppInterop의
"Class::Init signatures have been exhausted" 버그 때문에 **IL2CPP 쪽에 새
델리게이트/타입을 등록하는 모든 행위가 AccessViolationException을 유발한다**:

- `ClassInjector.RegisterTypeInIl2Cpp<T>()` — 금지
- IL2CPP 쪽 C# 이벤트에 `+=` 구독 — 금지
- 코루틴 `WrapToIl2Cpp()` — 금지

**따라서 `OnGUI` MonoBehaviour를 심는 IMGUI 오버레이는 만들 수 없다.** 대신
**uGUI로 만든다** — `Canvas`/`Image`/`Text`는 게임에 이미 존재하는 컴포넌트라
`AddComponent`로 붙이면 되고 타입 등록이 필요 없다. `UI/LogOverlay.cs` 참고.

매 프레임 로직도 같은 이유로 `Update`를 만들 수 없어서, `UnityEngine.Time.deltaTime`
getter에 Postfix를 걸고 `Time.frameCount`로 중복을 걷어낸다 (`UI/FramePump.cs`).
getter는 한 프레임에 수십 번 불리므로 Postfix 본문은 프레임 번호 비교로 끝나야 한다.

`Core.Net.RPCMsgManager`의 콜백에 직접 붙는 것도 안 된다. 콜백이 `+=`가 아니라
**단일 슬롯 대입**이라 게임 핸들러를 교체해버리고, 델리게이트 등록 자체가 위 금지
사항에 걸린다.

## 아키텍처

```
Socket.BeginReceive Postfix ─┐
                             ├─> SocketTap ─> FrameReassembler ─> BattleLogger
Socket.EndReceive   Postfix ─┘   (소켓별 상태)   (35바이트 BE 헤더)   (허용목록 디코드)
```

- `Net/SocketTap.cs` — 소켓 포인터를 키로 `(버퍼, 오프셋)`을 기억했다가 EndReceive가
  알려준 바이트 수만큼 꺼낸다. 전부 Postfix, 상태 변경 없음.
- `Net/FrameReassembler.cs` — TCP 스트림에서 프레임을 잘라낸다. 헤더가 말이 안 되는
  소켓(HTTP 등)은 `Rejected`로 표시하고 이후 입력을 버린다.
- `Proto/ProtoReader.cs` — 최소 protobuf 리더. 게임 파서를 쓰지 않는다.
- `Log/Opcodes.cs` — cmdID 상수와 허용목록.
- `Log/BattleLogger.cs` — 디코드 + 렌더링.

- `Log/Roster.cs` — playerId → `1P` / `마법 찻주전자1` 표시명.
- `Log/NameTable.cs` — id → 이름 (`names.tsv`).
- `UI/LogOverlay.cs` — 인게임 오버레이 (uGUI).
- `UI/FramePump.cs` — 프레임당 한 번 오버레이를 갱신하는 후크.

프레임 헤더 형식(35바이트, 빅엔디안)은 [LOGGER-DESIGN.md](LOGGER-DESIGN.md)에 있다.

### 소켓 상태의 수명

`SocketTap`은 소켓 포인터를 키로 재조립기를 들고 있다. **IL2CPP는 dispose된 객체의
주소를 재사용하므로**, 끊긴 소켓의 상태가 남아 있으면 재접속이 만든 새 소켓이 죽은
소켓의 반쪽 프레임을 물려받는다. 그러면 프레임 경계가 어긋나 큰 메시지(방 명단 등)가
통째로 유실된다. 그래서 두 경로에서 버린다:

- `EndReceive`가 `ObjectDisposedException`을 던지면 Postfix가 불리지 않는다 —
  `Finalizer`에서 버린다. 예외는 삼키지 않고 게임에 그대로 돌려준다.
- 0바이트 수신(FIN)은 예외가 없어 Finalizer도 타지 않는다 — `NoteEndReceive`에서 버린다.

핵심 메시지 넷:

- **`UpdateHeroAttrS2C` (1040)** — `{playerId, CauseOrigin cause, repeated HeroAttrEffect}`.
  "행동 하나 + 그 결과 전부"가 한 메시지에 들어있고, `HeroHpChangeS2C`의 `killer`
  필드로 가해자까지 알 수 있다. HP/공/방/버프/회복/반격이 여기로 온다.
- **`BattleS2C` (1007)** — PK 본체. 같은 `battleId`로 진행 중 여러 번 오고
  `isEnd == true`인 것만 확정 결과다. `fightBack`(반격) / `isPursuit`(추격) /
  `chainAttackDamage`(연계)가 여기 있다.
- **`HeroSkillMoveEffectS2C` (1096)** — `{1:playerId, 2:map<int32, {1: repeated
  UpdateHeroAttrS2C}>}`. **알맹이가 `UpdateHeroAttrS2C` 그 자체인 봉투**라 같은
  디코더를 재사용한다. 이동 중에 발동하는 스킬의 결과가 여기 담겨 오고, **1040으로
  따로 오지 않는다** — 안 풀면 통째로 사라진다 (v1.15까지 그랬다).
- **`LandBuffsS2C` (1013)** — 맵 칸에 놓인 버프 = **소환물**. 게임도 이걸
  `GameLogic.SummonLogic`이 받아서 칸 위 오브젝트를 만들고 지운다.
  로그에는 **`{캐릭터} 스킬 사용` 한 줄만** 남긴다 (아래 참고).

## 이 게임 protobuf의 함정 (실측)

**숫자 필드가 varint가 아니라 `sfixed32`/`sfixed64`다.** enum만 varint를 쓴다.
varint만 읽는 디코더를 쓰면 모든 값이 조용히 0으로 나온다 — 에러도 안 난다.
`ProtoReader.TryReadNumber(wire, out value)`로 wire 0/1/5를 전부 처리할 것.

실제 바이트 예 (`HeroHpChangeS2C`):
```
15 f9ffffff   field2 changeHp  wire5(fixed32 LE) = -7
1d 0a000000   field3 oriHp     = 10
45 07000000   field8 damageType= 7 (Battle)
49 8527160000000000  field9 killer  wire1(fixed64) = 1451909
```

id는 fixed64, 수치는 fixed32(부호 있음), enum은 varint로 보면 대체로 맞는다.

## 플레이어 이름

`RunningGameS2C`(1003) / `StartGameS2C`(5020)의 `party.model.Room`에서 명단을 얻는다:
`Room {9: repeated Player players, 10: repeated Player monsters}`,
`Player {1:Id, 2:Nick, 6:Slot, 20:IsBot}`. 몹은 `MonsterRefreshS2C`(1018)로 따로 온다.

**표시명은 캐릭터 이름을 쓴다** (`Z3000`, `패니`). 슬롯 번호(1P/2P)는 판마다 바뀌고
화면에서도 캐릭터로 인식하므로 로그에서 쓸모가 없다. 캐릭터 선택 전에는 `HeroId`가
비어 있어 닉네임으로 시작했다가 선택이 끝나면 바뀐다. 같은 캐릭터를 두 명이 고른
경우에만 뒤에 번호를 붙인다. 몹은 반대로 항상 번호를 붙인다 (`마법 찻주전자1`).

`Roster`가 `Player`에서 읽는 건 `Id/Nick/Slot/IsBot` 네 개, 그리고 몹 이름표 키인
`Hero.HeroId`(필드 10 안의 필드 2) 하나뿐이다. **`Hero`는 손패(`Cards`, 필드 8)도
품고 있으므로 거기서 다른 필드를 읽는 코드를 추가하지 말 것.**

**`Player.Id`는 매치용 슬롯이 아니라 영구 계정 uid다.** 그래서 로그 본문에는 uid를
쓰지 않고 `1P(닉네임)` 표시명만 쓴다 — 로그를 공유했을 때 남의 계정 정보가 같이
나가지 않게. 디버깅용으로 `LogPlayerIds` 설정을 켜면 참가자 줄에만 uid가 붙는다.

명단에 없는 id는 `?{id}`로 남긴다. 몹으로 단정하지 않는 게 중요하다 — 그렇게 두면
명단이 늦게 도착했을 때 플레이어가 조용히 몹으로 찍히고, 로그만 봐서는 모른다.
`?`가 보이면 `Room` 수집이 덜 된 것이다.

- 서버는 같은 개체를 `Players`와 `Monsters` 양쪽에 실어 보낸다. 한 번 플레이어로 잡힌
  id는 몹으로 되돌리지 않는다.
- 번호를 매길 인원수는 **(몹 여부, 이름)** 으로 센다. PvE 적이 플레이어 캐릭터를 쓰기도
  해서, 한 통에 세면 그 캐릭터를 고른 플레이어까지 번호가 붙는다. 번호 판정은 출력할 때
  하므로 둘째가 나타나면 첫째도 소급해서 번호가 붙는다.
- **캐릭터가 정해지기 전 명단은 참가자 줄로 찍지 않는다.** 명단은 선택 전후로 두 번
  오는데 첫 벌에는 계정 닉네임이 들어간다 — 같은 사람이 두 번 나오고, 로그를 공유하면
  남의 계정 정보가 같이 나간다.
- 참가자 줄에 스킬 목록을 미리 적는다. 패시브는 발동해도 "스킬 썼다"가 오지 않으므로
  (아래 "프로토콜에서 밟은 지뢰"), 나중의 버프 줄을 패시브로 짚어낼 단서가 된다.
- **맵이 새로 내보낸 몬스터는 등록(`MonsterRefreshS2C`)보다 첫 버프가 먼저 온다**
  (실측: 도둑의 "훔치기", 타락한 봉황의 "잿불"이 `?3187`, `?3500`으로 찍혔다). 그래서
  모르는 대상이 든 `UpdateHeroAttr`은 몬스터 등록이 아닌 다음 프레임까지 한 번
  미룬다(`_deferred`).

### 재접속 이름표 캐시

판 도중 재접속하거나 게임을 다시 켜면 명단(`RunningGameS2C`)을 놓쳐 이름이 전부
`?id`가 된다. 플러그인 폴더의 `roster-cache.tsv`가 이를 메운다.

- **playerId를 파일에 남기지 않는다.** 키는 id의 SHA-256 앞 8바이트이고, 조회할 때
  들어온 id를 같은 방식으로 해시해 맞춘다. 되돌릴 수단이 없으므로 파일만 얻어서는 계정
  id를 복원할 수 없다. 한 판에 수십 명이라 충돌은 사실상 없다.
- 값(캐릭터·몹 이름표, 슬롯)과 방 번호는 계정과 무관한 값이라 그대로 적는다.
- 번호(`Ordinal`)는 캐시에 담지 않고 되살릴 때 새로 매긴다 — 되살아나는 순서가 원래
  등록 순서와 다를 수 있다.
- **수명을 짧게 두는 것이 이 캐시의 안전장치다.** 새 판 시작, 판 이탈(씬 전환), 캐시와
  다른 방을 처음 채택할 때(크래시로 이탈 신호를 놓친 경우) 지운다.

## 카드/스킬/버프 이름표

로그의 `card#21004`를 `카드 "왕의 힘"`으로 바꾸는 데이터는 플러그인 폴더의
`names.tsv`(`kind`⇥`id`⇥`name`)에서 읽는다. **코드가 아니라 데이터로 둔 이유**는
게임 업데이트로 id가 바뀌면 스크립트만 다시 돌리면 되기 때문이다. 파일이 없으면
숫자로만 표시하고, 그게 정상 동작이다.

이름표는 소켓 스레드가 읽고 메인 스레드(`NameTable.Adopt`)가 쓴다. **사전을 통째로
갈아끼우기만 하므로 잠금이 없다** — 항목을 하나씩 더하는 식으로 바꾸면 그때부터 잠금이
필요하다.

출처는 Addressables 라벨 `GameData_INT`의 TextAsset(protobuf)이다. 정보표와
문자열표가 나뉘어 있어 두 번 조인한다:

```
Card    { 1: repeated CardInfoConfigure   {1:Id, 2:NameID} }
STRCard { 1: repeated STRCardLocalConfigure
          {1:Id, 2:간체, 3:영어, 4:일어, 5:번체, 6:한국어} }
```

정보표는 전부 `{1: repeated XxxInfoConfigure}` 꼴이고 항목의 id는 필드 1이다.
이름 필드 번호만 표마다 다르다 (`tools/extract_names.py`의 `TABLES` 참고):

| 종류 | 정보표 | 이름 필드 | 비고 |
| --- | --- | --- | --- |
| card | Card | 2 | |
| skill | Skill | 4 | |
| buff | Buff | **14** | `NameId` — 대문자 `NameID`가 아니다. 15(`DescId`)를 쓰면 설명문이 나온다 |
| event | Event | 5 | |
| land | Land | 5 | id 필드 이름은 LandType이지만 번호는 1 |
| relic | Relic | 7 | **인게임 용어는 "칩"** — 표 이름만 relic |
| cardtype | Card | (필드 9 열거형) | 문자열표와 조인하지 않는다. 색상용 `Attack`/`Defend`/`Other` |
| relicgrade | Relic | (필드 2 열거형) | 칩 등급. 색상용 `Blue`/`Purple`/`Orange` |
| monster | Monster | 3 | `Hero.HeroId`가 키다 |
| character | Character | 4 | 플레이어 캐릭터 |
| charskill | Character | (18/19 액티브, **20/21 packed 패시브**) | 이름이 아니라 **대응표** — `heroId → 스킬 이름 목록` |
| monskill | Monster | (17 액티브, **18 packed 패시브**) | 〃 |

**`charskill`/`monskill`은 문자열표와 직접 조인하지 않는다** — 먼저 만든 `skill`
이름을 재사용해 쉼표로 이어 붙인다. 패시브 필드(20/21/18)는 **packed repeated
sfixed32**라 숫자로 읽으면 통째로 건너뛴다.

**한글패치가 깔린 INT 빌드는 영어 슬롯(필드 3)에 한국어를 넣는다.** 그래서
한국어(6) → 영어(3) → 간체(2) 순으로 비어있지 않은 것을 고른다.

### 만드는 곳이 둘이다 — 한쪽을 고치면 다른 쪽도 고칠 것

| | 어디서 | 언제 |
| --- | --- | --- |
| `Log/NameHarvest.cs` | **Addressables로 직접 로드** | 배포본. `names.tsv`가 없으면 첫 실행에 |
| `tools/extract_names.py` | 번들 파일 (UnityPy) | 개발용 오프라인 |

**표 구성과 파싱은 `Log/NameConfig.cs` 한 곳에 있다** — Unity에 의존하지 않게
분리해 둔 이유는 **오프라인에서 대조할 수 있게** 하려는 것이다. 파서가 조용히
틀리면 이름이 전부 사라지는데 게임을 켜보기 전에는 알 수가 없다. 검증은
`Proto/ProtoReader.cs` + `Log/NameConfig.cs`만 링크한 콘솔 프로젝트로
`extract_names.py` 결과와 행 단위 비교했다 (**1010행 완전 일치**).

**`names.tsv`는 리포에 커밋하지 않는다.** 게임 텍스트 + 한글패치 번역문이라
재배포할 수 없다. 그래서 플러그인이 각자의 게임에서 직접 만든다.

### 설정 에셋은 "주워 담을" 수 없다 (실측으로 폐기한 접근)

게임의 `StaticConfigure.InitAsync`는 이렇게 생겼다:

```csharp
AddressableHelper.LoadAssetsAsync<TextAsset>({"GameData_INT"}, OnConfigureLoaded, …)
…
Addressables.Release<IList<TextAsset>>(awaiter.GetResult());   // 파싱하자마자 놓는다
```

`OnConfigureLoaded`가 `case "Card":` → `CardConfigure.Parser.ParseFrom(asset.bytes)`로
파싱해 정적 필드에 넣고, **원본 TextAsset은 곧바로 해제된다.**

처음엔 폰트를 찾을 때처럼 `Resources.FindObjectsOfTypeAll<TextAsset>()`로 주워
담으려 했는데 **구조적으로 불가능했다.** BepInEx 체인로더는 Unity 런타임이 올라온
뒤에야 플러그인을 로드해서, 우리 첫 스캔이 그 이후다:

```
Scanning for config assets: 0/16 captured, 4925 TextAssets visible (scan 1, frame 384).
```

**TextAsset 4925개가 멀쩡히 보이는데 설정 에셋만 없다.** 찾는 방법이 잘못된 게
아니라 이미 해제된 뒤인 것이고, 스캔 간격을 아무리 좁혀도 소용없다.

### 그래서 같은 라벨로 우리가 직접 부른다

```csharp
Il2CppSystem.Object key = (Il2CppSystem.String)"GameData_INT";
_handle = Addressables.LoadAssetsAsync<TextAsset>(key, null);
```

**핸들을 우리가 쥐고 있는 동안은 해제되지 않으므로 타이밍 경합이 사라진다.**
캐시에서 읽는 것이라 다운로드도 없다. 지켜야 할 것 셋:

- **콜백 자리에 `null`을 넘긴다.** IL2CPP 쪽 델리게이트 등록은 이 게임에서 확정
  크래시다. 콜백 없이 핸들만 받고 `IsDone`을 프레임 펌프에서 폴링하면 델리게이트도
  코루틴도 필요 없다.
- **다 읽으면 반드시 `Release`한다.** 안 놓으면 설정 번들이 통째로 메모리에 남는다.
  게임이 곧바로 놓는 이유도 그것이다. 시간 초과(20초) 경로에서도 놓는다.
- **`Addressables` 초기화 전에 부르면 실패한다.** `WarmupFrames`만큼 기다린다.

안전 근거: `LoadAssetsAsync<TextAsset>`와 `AsyncOperationHandle<IList<TextAsset>>`
제네릭 인스턴스를 **게임 자신이 쓰고 있어서** IL2CPP 메타데이터에 이미 존재한다.
새 타입을 만드는 게 아니라 있는 걸 부르는 것이다.

함정 하나 — **interop 인터페이스는 상속 멤버를 물려받지 않는다.** `IList<T>`에는
`Count`가 없고 `ICollection<T>`에만 있어서 한 겹 캐스팅해야 한다.

성공하면 `NameTable.Adopt`로 **재시작 없이** 그 판부터 이름이 나온다.
실측 결과 오프라인 추출기와 **1010행 완전 일치**했다.

오프라인 재생성 (게임을 한 번 실행해 Addressables 캐시가 찬 뒤에):

```bash
python <skills>/unity-bundle-inspector/scripts/scan_bundles.py \
  "$USERPROFILE/AppData/LocalLow/feimo/AstralParty_INT/com.unity.addressables" \
  --text -o gamedata   --grep '^(Card|Skill|Buff|Event|Land|Relic|Monster|Character|STR(Card|Skill|Buff|Event|Land|Relic|Monster|Character))$'
python tools/extract_names.py gamedata names.tsv
```

## 로그에 이름을 못 붙이는 id (정상)

- `heroBuff#12086685` — 런타임 버프 **인스턴스 uid**. 설정 id가 아니라 이름표가 없다.
- `BattleUseCardS2C.CardId`와 `BattleRole.useCards` — 둘 다 매치 안에서만 유효한
  **카드 uid**다. 클라가 `BattleUseCardC2S`에 `CardUid`를 실어 보내고 서버가 그대로
  돌려준다. 헥스로 확인: `useCards`는 packed `repeated sfixed32`이고
  `len=4 hex=03000000` = uid 3, 같은 판 `BattleUseCardS2C`가 보낸 값과 일치했다.
  **즉 PK에서 낸 카드의 종류는 서버가 보내지 않는다.** 로그에는 "카드 제출"까지만 남긴다.
  (`UseEffectCardS2C.CardId`는 반대로 **설정 id**라 이름이 붙는다.)

## 프로토콜에서 밟은 지뢰

- **`HeroBuffChangeS2C`의 필드 4는 `repeated Buff`가 아니라 `map<int64, Buff>`다.**
  맵 항목은 `{1:key, 2:value}`라 그 자리에서 바로 `Buff`로 읽으면 안쪽 Buff(필드 2,
  길이형)가 통째로 건너뛰어지고 `BuffId`가 0으로 나온다. v1.17까지 그랬고,
  그래서 필드 4로 온 버프는 **이름이 한 번도 안 붙었다**. 한 겹 벗기고 읽을 것.
- **`Oper`는 `{0:Noop, 1:Insert, 2:Delete, 3:Update}`인데 Noop이 "아무 일 없음"이
  아니다.** 게임의 `GameLogic/BuffLogic`을 보면 op별로 읽는 필드가 다르다:

  | op | 게임이 하는 일 | 읽는 필드 |
  | --- | --- | --- |
  | Noop(0) | `UpdateBuff(model.Buffs)` | **필드 4 — 그 플레이어의 전체 목록** |
  | Insert(1) | `InsertBuff(model.Buff)` | 필드 2 |
  | Delete(2) | `DeleteBuff(model.Buff)` | 필드 2 |
  | Update(3) | `ChangeBuff(model.Buff)` | 필드 2 |

  즉 **Noop = 전체 목록 갱신**이다. 이걸 동사로 찍으면 `버프 렌` 한 마디만 남는다.
  `_buffs`(uid → 대상·버프)와 diff를 떠서 **바뀐 것만** 남길 것.
- **패시브 스킬이 발동해도 "스킬 썼다"는 메시지가 없다.** `UseEffectCardS2C`의
  `UseSkill` 분기는 **액티브 스킬만** 탄다. 패시브는 버프로만 오고, 그 버프의
  `Buff.Source.s == skill`(1)이 유일한 단서다. `SkillTriggerNotifyS2C`와
  `BuffTriggerNotifyS2C`는 정의만 있고 **RPC 디스패치가 없다** (cmdid 표에 없음) —
  이 빌드에서는 쓸 수 없다. 실측 한 판(4명/5라운드)에서 패시브 6개가 전부 버프로만
  나왔다: 전설의 상인·방화벽·줏대 없음·죄인 심판·진범 발견·무한한 육체.
  그래서 **새로 걸리는 스킬 출처 버프는 `스킬 발동`으로 찍는다**(v1.23.0).
  갱신·해제는 발동이 아니므로 건드리지 않는다.
- **`UseEffectCardS2C`(5056)는 두 가지 일을 한다.** 게임의 `GameLogic/CardLogic`이
  `UseSkill`(필드 8, bool)로 갈라진다:

  | UseSkill | 게임이 하는 일 |
  | --- | --- |
  | false | `cardActions[CardId].CardCallBack(PlayerId, TargetIds, ...)` |
  | true | `TriggerSkill(PlayerId, SkillId, SkillCds)` |

  그래서 `cardId <= 0`이면 버리는 규칙에 **캐릭터 스킬 사용이 통째로 걸려 사라졌다**
  (v1.18까지). 스킬 쪽은 playerId도 skillId도 메시지가 직접 주므로 `LandBuffsS2C`로
  추측하는 것보다 정확하다 — 추측 경로는 `_saidSkillUse`로 뒤로 물러난다.
- **`UseEffectCardS2C.TargetIds`(필드 3)는 packed `repeated sfixed64`다.** 숫자
  wire로만 읽는 루프에 넣으면 통째로 건너뛴다 — 대상이 한 번도 안 나왔다 (v1.18까지).
  `BattleRole.useCards`(5)와 `ThrowDiceS2C.vals`(1)도 같은 packed 형태다.
- **버프가 풀릴 때 서버는 uid만 보낸다.** 게임도 `_buffDict.Remove(Buff.UniqueId)`로
  지우기만 해서 `BuffId`를 안 싣는다. 그래서 걸릴 때 `_buffs`에 기억해두지 않으면
  `버프 해제 렌`까지만 쓰고 **무슨 버프가 풀렸는지 영영 말할 수 없다**
  (렌의 "쉴드"가 이 경우였다).
- 변화량 0인 HP 알림이 자주 온다 (이미 최대 체력인데 회복 등). 그대로 찍으면 의미 없는
  줄만 쌓여서 `change == 0 && ori == curr`이면 버린다.
- `BattleRole.Point`는 이름과 달리 **주사위 값**이고, **`isEnd` 프레임의 `atk`/`def`에는
  그게 이미 합산돼 있다.** PK 피해량이 `공격자.atk - 방어자.def`로 떨어지는 것으로
  확인된다 — 주사위가 따로였다면 `(atk+point) - (def+point)`여야 하는데 안 맞는다:

  | 실측 줄 | 피해 | `atk - def` | `(atk+p) - (def+p)` |
  | --- | --- | --- | --- |
  | `ATK10 DICE6` vs `DEF3 DICE2` | 7 | **7** ✓ | 11 ✗ |
  | `ATK15 DICE6` vs `DEF2 DICE1` | 13 | **13** ✓ | 18 ✗ |

  같은 종류 몹의 기본 DEF가 일치하는 것으로 한 번 더 확인된다 — 마법 찻주전자의
  `DEF3 DICE2` / `DEF6 DICE5` / `DEF2 DICE1`이 전부 기본 1이다.

  **그래서 로그에는 빼서 찍는다** (`ATK9 DICE4`, 합이 13). 주의: 게임의
  `GameLogic/TutorialLogic`에 `atk + point` 하는 코드가 있어서 반대로 읽기 쉬운데,
  그건 주사위가 아직 반영 안 된 **진행 중** 모델이다.
- 버프의 이름 필드는 **`NameId`(14)**다. 대문자 `NameID`가 아니라서 필드 목록을
  grep할 때 놓치기 쉽고, 그 바람에 `DescId`(15)를 쓰면 로그가 설명문으로 덮인다
  ("공격력 +5" 대신 "왕의 힘"이 나와야 한다). 이름표에 없는 버프는 id가
  `출처id * 100 + 일련번호` 꼴인 걸 이용해 `[카드이름]`으로 대체한다.
- 몹 번호는 **종류별로** 1, 2, 3을 매긴다. 게임 UI가 그렇게 표시한다
  (전체 등장 순서로 A, B, C를 매기면 인게임 표기와 어긋난다).
- **`LandBuffsS2C`는 칸 단위로 *전체 목록*을 보낸다.** 게임의
  `SummonLogic.UpdateLandBuffs`가 새 목록에 없는 기존 소환물을 `CloseSummon`으로
  닫는 걸로 확인했다. 그래서 "새로 생긴 게 있나"를 알려면 `_landSummons`(uid → 칸)와
  diff를 뜨는 수밖에 없다. 필드 2(BuffArray)가 아예 없는 wrap은 **빈 목록이 아니다** —
  그걸 빈 목록으로 보면 멀쩡한 소환물을 지워버린다.
- **`buff_source.source`와 `CauseOrigin.source`는 다른 열거형이다.** 이름이 비슷해서
  섞어 쓰기 쉬운데 번호가 안 맞는다 (relic이 여기선 6, 저기선 17). 각각
  `BuffOrigin.Describe` / `CauseSource.Describe`로 나눠뒀다.
- `Buff`의 `PlayerId`는 protobuf 필드가 아니라 **클라이언트 쪽 평범한 필드**다
  (FieldNumber가 없다). 즉 **소환물을 누가 놨는지는 선에 실려 오지 않는다.**
  `Buff.Source`(50)가 알려주는 건 "어느 스킬/카드에서 나왔나"까지다.
- **`CauseOrigin.Id`가 land(9)일 때 그 값은 LandType이 아니라 맵 칸 번호다.** 그대로
  LandType으로 조회하면 번호가 우연히 유효 범위(1~27)에 들어 **조용히 엉뚱한 땅 이름이
  붙는다.** 대응표는 `Room.Lands`(필드 12, `map<int32, BaseLand>`)에 있다 (`LandMap`).
  이름표가 없는 원인 종류(`CauseSource.Describe`의 표가 `null`)는 id가 런타임 고유값이라
  숫자를 찍지 않는다.
- 캐릭터별 스택(치유·스타라이트·표식 등, `HeroAttrEffect` 필드 15~32)은 게임이 고정 id
  버프로 화면에 띄운다(`BattleProperty.RegisterPropertyBuff`). 그래서 그 버프 이름을 쓴다.
- `ThrowDiceS2C.movePoint`가 비어 올 때가 있다. 눈의 합으로 메운다.
- 판 도중 합류하면 라운드 전환을 놓쳐 라운드가 0으로 남는다. 0라운드는 없으므로 첫
  전환을 받기 전에는 `[R?]`로 찍는다.

## 인게임 오버레이

`UI/LogOverlay.cs`. 설계는 **astral-party-korean-patch**의 `OverlayUi`를 참조했다
(<https://github.com/maynut02/astral-party-korean-patch>, 작성자 허락 받음).
배포할 일이 있으면 크레딧을 남길 것.

가져온 것 세 가지:

1. **IMGUI가 아니라 uGUI.** `new GameObject` → `Canvas`(ScreenSpaceOverlay) +
   `CanvasScaler`(1920×1080 기준) + `Image` + `Text`. 전부 기존 컴포넌트라
   `ClassInjector` 없이 된다.
2. **폰트는 게임 것을 그대로 쓴다.** `Resources.FindObjectsOfTypeAll<Font>()`로
   `Afacad-Regular`를 찾고, 못 찾으면 내장 `Arial.ttf`로 떨어진다. 한글패치가 이
   폰트를 한글 지원 폰트로 교체하므로 같은 것을 쓰면 오버레이도 한글이 나온다.
   제네릭 0-인자 오버로드는 interop에서 해석이 모호해 **리플렉션으로** 부른다.
3. `sortingOrder`는 32750 — 한글패치 오버레이(32760)보다 한 칸 아래에 둔다.

주의할 점:

- 로그 줄은 **소켓 IO 스레드**에서 오고 Unity 객체는 메인 스레드에서만 만질 수 있다.
  `ConcurrentQueue`로 넘기고 `Pump()`가 프레임마다 비운다.
- Unity 객체는 "가짜 null"이라 `??` 널 병합이 제대로 동작하지 않는다.
  `transform.TryCast<RectTransform>()`처럼 interop 방식으로 가져올 것.
- `raycastTarget = false` — 오버레이가 게임 클릭을 가로채면 안 된다.
- **창 크기는 `FontSize` × 배수로 고정이다 — 높이도 폭도.** 한때 매번 내용에
  맞췄더니(`Resize(shown)`) 로그가 올 때마다 창이 커졌다 작아져서 읽기 어렵다는 요청이
  있었다. 처음엔 높이만 `FontSize` 배수(`LineHeight = FontSize * 1.45`)로 고정하고
  폭은 별도 `Overlay.Width` 설정값(고정 상수)으로 뒀는데, 폰트 크기를 키우면 그
  상수가 안 따라와 줄이 잘리는 문제가 있었다. 그래서 런타임에 실제 렌더 폭
  (`Text.preferredWidth`)이 얼마나 필요한지 임시 진단 로그(`[width-probe]`)로
  한 판(3라운드까지) 재봤더니 FontSize 22에서 최대 619px(비율 28.15)이 나왔다.
  **`29`(단일 배수)로 정했다가 리뷰에서 문제를 지적받아 고쳤다**: 실측값 619엔 본문
  좌우 여백(`PadX × 2 = 24`) 또는 헤더 왼쪽 여백(`HeaderLeft + PadX = 42`, 그립 자리
  포함)이 이미 섞여 있는데, 그건 폰트 크기와 무관한 고정값이다. 단일 배수로 두면
  FontSize가 작아질수록 이 여백까지 같이 줄어들어, 최소 허용치인 FontSize 8에서
  실제 필요 폭(약 240px)보다 패널이 좁아져(232px) 잘릴 수 있었다. 그래서 지금은
  `Width = FontSize * TextWidthPerFontSize(32) + WidthMargin(HeaderLeft+PadX=42)`로
  비례항과 고정 여백을 분리한다 — 높이 계산도 본문 높이와 `PadY * 2` (고정)를
  따로 더한다. `Overlay.Width` 설정은 없고, 폭은
  `FontSize`에서 자동 계산된다 — 폰트를 키우면 폭도 같이 커진다.
  초기 계수 28은 3라운드 표본에서 잡았고, 긴 스킬 PK 줄이 잘리는 실게임 화면을
  확인한 뒤 32로 올렸다. 다른 긴 줄이 넘치면 `RectMask2D`가 조용히 자른다.
  높이의 `FontSize * 1.45`는 창이 내용에 맞춰 늘던 시절의 넉넉한 추정이었는데, 창을
  고정한 뒤로는 그대로 실제 높이가 되어 마지막 줄 아래에 빈 공간이 남았다. 지금은 오버레이를
  만들 때와 폰트가 바뀔 때 `Text.preferredHeight`로 한 줄·두 줄 높이를 재서
  `첫 줄 + 줄 간격 × (줄 수 - 1)`로 잡는다. 잰 값은 BepInEx 로그에
  `Overlay line height measured`로 남고, 재기 전에는 옛 배수를 쓴다.
  폴백 폰트(`Afacad-Regular`를 못 찾아 `Arial.ttf`로 갈 때, `ResolveFont`/
  `RefreshFont` 참고)로 넘어가면 글자 폭 자체가 달라져 이 계수가 안 맞을 수 있는데,
  아직 그 화면에서 실측하지 않았다. 크기 조절 손잡이와 "넓어지기만 하는 폭"도
  검토했지만 넣지 않았다.
  - 긴 줄은 패널의 **`RectMask2D`로 자른다.** 게임에 이미 있는 uGUI 컴포넌트라
    `AddComponent`로 붙는다. 본문은 `UpperLeft` 정렬이라 줄이 적으면 아래가 빈다.
  - 여백 상수(`PadX`/`PadY`)를 한 곳에 모아뒀다 — 머리줄 오프셋과 본문 오프셋,
    높이 계산이 **같은 값을 써야** 아귀가 맞는다. 예전엔 12/10/8/28이 흩어져 있었다.
  - 줄바꿈(`HorizontalWrapMode.Wrap`)은 켜지 말 것. 스크롤이 세는 **논리 줄 수**와
    화면에 그려지는 줄 수가 어긋나 페이지 계산이 깨진다.
- **`CanvasScaler.matchWidthOrHeight`는 1(높이)이다.** 0.5로 폭을 섞으면
  울트라와이드에서 배율만 올라가고 글자는 그만큼 길어지지 않아 빈 배경이 더 넓어진다.
- 오버레이 생성이 한 번 실패하면 플래그를 세우고 다시 시도하지 않는다. 파일 로그는
  계속 남으므로 기능이 완전히 죽지는 않는다.

### 창 이동 (그립 드래그)

- **입력은 폴링한다.** `EventTrigger`, `IDragHandler`를 구현한 MonoBehaviour,
  `onClick += ...` 같은 구독은 전부 타입 주입이나 IL2CPP 델리게이트가 필요하다.
  대신 `FramePump → LogOverlay.Pump()`에서 매 프레임 `Input.GetMouseButtonDown/
  GetMouseButton/GetMouseButtonUp(0)`과 `Input.mousePosition`을 읽는다.
- **드래그는 그립에서 누른 순간에만 시작한다.** 판정은 휠과 같은
  `RectTransformUtility.RectangleContainsScreenPoint`(24×24 영역)다. 본문·머리줄 클릭은
  무시한다. 시작 시 마우스 위치와 창 좌상단 위치를 잡아두고 이동량만 더하므로
  잡은 지점과 창의 상대 위치가 유지된다. 버튼이 떨어지거나 창이 숨으면 끝난다.
- **`raycastTarget`은 그립도 false다.** 오버레이는 입력을 소비하지 않는다. 즉 그립
  아래에 게임 버튼이 있으면 **게임도 같은 클릭을 받는다.** 막으려면 raycast를 켜야 하는데
  그러면 창 전체가 게임 클릭을 상시 가로챌 위험이 생긴다. 실측에서 문제가 되면 그립을
  조작 UI와 겹치지 않게 두거나 수정키 조합을 추가하는 쪽으로 간다.
- **배율은 `Screen.height / 1080`으로 직접 계산한다.** `matchWidthOrHeight = 1`이면
  CanvasScaler가 쓰는 값과 같다. `canvas.scaleFactor`는 스케일러 Update가 돌기 전엔
  1이라 생성 직후 보정에 쓰면 낮은 해상도에서 위치가 틀어진다.
- **좌표 기준은 창의 좌상단이다.** 패널의 앵커와 pivot이 둘 다 화면 좌상단이고, 저장값은
  `(왼쪽에서의 x, 위에서 아래로의 y)`, 높이 1080 캔버스 단위다. 가로 폭은 화면 비율에 따라
  `Screen.width / 배율`이다(16:9면 1920, 3440×1440이면 2580).
  처음 구현은 좌하단 기준이었는데, 로그 양에 따라 창 높이가 바뀌면 그립이 있는 위쪽
  모서리가 오르내려서 바꿨다.
- **경계는 창 크기(`PanelSize`)로 계산한다.** 크기가 고정이라 로그 양과 무관하다.
  화면이 바뀌면 위치를 다시 보정하며, 표시 위치가 안쪽으로 밀려도
  원하는 위치(`_desired`)는 유지한다.
- 창이 화면보다 크면 **위쪽과 왼쪽을 우선**한다 — 그립이 그쪽에 있으므로 어떤 경우에도
  다시 잡을 수 있다.
- **원하는 위치와 표시 위치를 분리한다.** `_desired`는 사용자가 둔 자리이고, 화면에는
  `ClampToScreen(_desired)`를 놓는다. 해상도가 바뀌면(`TrackScreenSize`) 다시 보정할 뿐
  `_desired`는 건드리지 않는다 — 낮췄다 올리면 원래 자리로 돌아온다. 설정 파일의 큰
  값도 같은 경로로 화면 안에 들어온다.
- **기본 위치는 `-1` 센티널이다.** 설정값이 음수·NaN·무한대이면 "저장된 위치 없음"으로
  보고, 창의 좌하단이 화면 좌하단에서 (24, 24) 떨어지는 자리에 띄운다. 이 자리는
  `Lines`/`FontSize`에 따라 달라지므로 고정 숫자를 기본값으로 쓸 수 없다. 그립을
  누르기만 하고 움직이지 않으면 기본 위치 상태를 유지하고 저장하지 않는다.
- **저장은 드래그가 끝날 때 한 번.** 정수로 반올림해 마지막 저장값과 다를 때만
  저장 콜백을 부른다. X/Y를 따로 대입하면 파일을 두 번 쓰므로
  `Plugin.SaveTogether`가 `SaveOnConfigSet`을 잠깐 끄고 `Config.Save()`를 한 번 한다.
  저장 예외는 경고만 남긴다 — `Pump`의 catch까지 올라가면 오버레이 전체가 꺼진다.

### 자동 표시

뜨는 것과 숨는 것 모두 **씬 전환**을 기준으로 한다 (`OnSceneChanged`).

- **판 시작 신호(`StartGameS2C`/`MatchSuccessS2C`/`SingleCampaignS2C`)는 픽창에서 온다.**
  그래서 이 신호(`LogOverlay.GameStarted`)에서는
  페이지를 비우고 "전투 대기"(`_awaitingBattle`)로 두며, 그때의 씬을 픽창(`_pickScene`)으로
  기억한다. 창은 띄우지 않는다 — 판 시작에 바로 띄웠더니 픽창을 가렸다.
- **픽창을 벗어나는 씬 전환에서 뜬다.** 그 씬을 전투 씬(`_battleScene`)으로 기록한다.
  실측 로그에서도 참가자 목록이 올라온 뒤 씬이 한 번 바뀌고 전투가 이어진다.
- **1라운드 신호는 전투 씬이 로드되기 전에 온다 (실측).** 참가자 목록 3초 뒤 Round 1이
  왔고 씬 전환은 그 뒤였다. 한때 라운드 페이지에서 현재 씬을 전투 씬으로 기록했더니
  픽창 씬이 기록되어, 곧 이어진 진짜 전투 씬 전환을 "판을 나감"으로 읽고 창을 숨겼다.
  그래서 **전투 대기 중에 받은 라운드는 창을 띄우지도, 씬을 기록하지도 않는다.** 대기 중
  두 번째 라운드까지 씬 전환이 없으면 그때는 씬이 안 바뀌는 판으로 보고 띄운다.
- **라운드를 받기 전의 씬 전환은 로딩으로 본다** (`_roundSeen == false`). 숨기지 않고
  전투 씬 기록만 새 씬으로 옮긴다. 전투 씬에서 받은 라운드마다 현재 씬을 다시 기록한다.
- **판을 나간 것**: 라운드를 받은 뒤 전투 씬을 벗어났거나, **픽창 씬으로 돌아왔을 때**
  (라운드 여부 무관). 창을 숨긴다.
- **재접속(`RunningGameS2C`)은 판 시작 신호가 없다.** 대기 상태가 아니므로 라운드
  페이지에서 곧바로 띄운다.
- **`_userHidden`(F9로 끔)은 판에 들어가는 순간(`EnterGame`) 풀린다.** 예전엔 전투 씬을
  벗어날 때만 풀려서, 로비에서 F9를 누르면 그 뒤 모든 판에서 창이 자동으로 뜨지 않았다.
  픽창이나 판 안에서 끈 것은 그 판이 끝날 때까지 유지한다.
- 전제: 로비·방·픽창이 한 씬이다 (실측 로그에서 실행 후 전투까지 전환은 한 번). 로비가
  픽창과 다른 씬이라면, 전투 씬 첫 라운드 전에 나갈 때 창이 남을 수 있다. 픽창에서
  나가면 씬이 안 바뀌므로 창은 뜨지 않는다.

## 색상

오버레이는 Unity 레거시 `Text`의 리치 텍스트 태그(`<color=#RRGGBB>`)로 칠한다.
**줄은 태그가 붙은 채로 한 번만 만들고, 파일·콘솔로 나갈 때 `Palette.Strip`이
걷어낸다.** 같은 줄을 두 벌 만들지 않으려는 것이다 — 색을 추가할 때 이 구조를 지킬 것.

플레이어 슬롯 색은 **게임 상수를 그대로 가져왔다** (`Core.GameConfig.slotColor`,
게임도 `GameConfig.HTMLStringRGB(slot)`으로 플레이어 이름을 이 색으로 칠한다):

| 슬롯 | 색 |
| --- | --- |
| 1P | `#FF4646` 빨강 |
| 2P | `#94FF46` 연두 |
| 3P | `#4386F4` 파랑 |
| 4P | `#FFB346` 주황 |
| 5P | `#A053D4` 보라 |

몹은 `#B9C2D0` 회색으로 빼둔다.

**카드 이름 색도 게임 상수를 그대로 가져왔다** —
`GameLogic.BattleCardMessage.GetCardMsg`가 채팅에 카드 이름을 칠하는 색이고, 종류는
셋뿐이다:

| CardType | 색 | |
| --- | --- | --- |
| Attack | `#FF0000` | 공격 카드 10장 |
| Defend | `#0099FF` | 방어 카드 3장 |
| 그 외 | `#00CC00` | 버프/효과 계열 — Effect 50, Counter 3, Curse 3 |

**카드에는 `Event` 종류가 없다** (실측 분포 위 참고). 게임에서 노랗게 보이는 건
이벤트 *칸*이라 별개 축이고, 그래서 이벤트 색(`#FFCC33`)은 카드가 아니라
`CauseOrigin`이 event일 때만 쓴다. 이 노랑은 상수를 찾지 못해 **고른 값**이다.

종류는 `names.tsv`의 `cardtype` 행에서 온다 (`CardInfoConfigure.CardType`, 필드 9).
데이터에는 `Attack`/`Defend`/`Other`라는 **의미만** 넣고 색은 `Palette.Card`가 정한다.

**칩 등급 색도 게임 상수다** — `GameLogic.BattleRelicMessage`가 채팅에 칩 이름을
이 색으로 칠한다. `RelicInfoConfigure.RelicQualityType`(필드 2)이 출처이고
`names.tsv`의 `relicgrade` 행으로 들어온다 (검증: `복싱 글러브 - 초급/중급/고급`이
정확히 Blue/Purple/Orange).

| 등급 | 게임 값 | 쓰는 값 |
| --- | --- | --- |
| Blue | `#004DFF` | `#4D8BFF` — 원본이 어두운 패널에서 안 보여 한 톤 올렸다 |
| Purple | `#9700E6` | 그대로 |
| Orange | `#EE8F00` | 그대로 |

공격/방어 능력치 색(`#FF7B7B` / `#6FB6FF`), HP 증감 색(`#FF9A8A` / `#8BE0A0`),
골드(`#D9A441`)는 **게임 상수를 찾지 못해 고른 값이다.** `atkColor`/`goldColor`
같은 상수가 없었다. 골드는 이벤트 노랑(`#FFCC33`)보다 한 단계 어둡게 잡아 같은
줄에 나와도 구분되게 했다.

## 인게임 용어

코드 안의 테이블 이름과 **화면에 보이는 용어가 다른 것**이 있다. 사용자 표기를 따른다.

| 내부 | 인게임 |
| --- | --- |
| relic | **칩** |
| `BattleRole.Point` | **주사위** |

## 중복을 줄인 규칙 (v1.5.0)

같은 사실이 여러 줄에 나오면 오버레이가 금방 가득 찬다. 줄인 곳:

- **PK 역할 줄의 캐릭터 괄호를 뺐다.** 표시명이 이미 캐릭터 이름이라
  `토노 한나1(토노 한나)`가 됐다. 명단에서 못 찾은 `?id`일 때만 힌트로 붙인다.
  (색상 태그가 붙은 뒤로 `who.StartsWith(hero)` 비교가 깨졌던 게 원인 — 비교 전에
  `Palette.Strip`을 거칠 것.)
- **PK 역할 줄의 `HP±N`을 뺐다.** 뒤따르는 HP 줄이 전/후/최대까지 보여준다.
- **`cause`가 battle이면 머리줄을 안 찍는다.** 직전 PK 블록이 "누가 무엇을"을 이미
  말했다. 효과 줄만 같은 들여쓰기로 이어 붙어 한 덩어리로 읽힌다.
- **`damageType`이 원인과 같은 말이면 태그를 뺀다.** `(카드 "레이저")` 바로 아래에
  `[카드]`를 또 적을 이유가 없다. 주의: `DamageType`과 `CauseOrigin.source`는 **다른
  열거형이라 번호가 안 맞는다** (DamageType.Event=5인데 source.event=3). 같은 뜻인 짝을
  `DamageKind.EquivalentCause`에 적어두고 비교한다.
- **`DamageType`은 한국어로 찍는다.** 전엔 `[Card]`/`[Land]`처럼 enum 이름이 그대로
  나갔다. 대부분은 위 규칙으로 아예 사라지고, 원인과 다를 때만 `[칩]` 같은 식으로 남는다.
- **능력치는 `ATK7 DICE5` / `DEF3 DICE2`**로 쓴다 (`atk`/`def`/`주사위` 혼용 금지).
  **PK 줄에서 공격자는 ATK만, 방어자는 DEF만** 보여준다 — 피해가
  `공격자.ATK - 방어자.DEF`로 떨어지므로 나머지 반쪽은 그 교전과 무관하다.
  반격은 역할이 뒤바뀐 별도 PK 줄로 나오므로 거기서 다시 보인다.
- **칩을 골랐을 때 나오는 버프 줄은 통째로 버린다 (v1.16.0).** 서버는 칩의 능력을
  `HeroBuffChangeS2C`로 보내지만, 머리줄이 이미 누가 무슨 칩을 얻었는지 다 말했다.
  버프 이름도 대개 칩 이름과 같아서 보탤 게 없다. 능력치 변화는 ATK/DEF 효과로 따로
  오므로 정보가 사라지지 않는다.

  **줄만 버리고 `_buffs`에는 기억해야 한다 (v1.22.0).** 안 그러면 다음 전체 목록
  갱신(Noop)에서 그 칩 버프가 처음 보는 것으로 잡혀 `버프 부여 낸시 루 [타겟 보드]`로
  다시 나온다. 실측으로 겪은 증상이다.
- **칩에서 온 버프는 "칩 획득"으로 쓴다 (v1.22.0).** `Buff.Source.s == relic`(6)이면
  버프가 스스로 출처를 말해주는 것이므로 동사를 바꾸고 이름도 `relic` 표에서 가져온다
  (`BuffLine`). 칩을 **고를 때**는 머리줄이 이름을 말해주지만, 체크 포인트처럼
  **원인 없이 오는 경로**가 있어서 그땐 이 줄이 유일한 단서다.
- **소환물은 하나씩 적지 않는다 (v1.16.1).** `LandBuffsS2C`를 그대로 풀면 스킬 한
  번에 이런 게 나온다:
  ```
  [R1] 30번 칸("몬스터 돌격 게이트") 소환물 #1292201 설치  [소환]
  [R1] 31번 칸("횡재") 소환물 #1292201 설치  [소환]
  ... 여섯 줄
  ```
  칸 여섯 개가 한꺼번에 깔리고, 소환물 버프 id는 `names.tsv`에 없어서 읽을 수 없는
  숫자만 남는다. 필요한 건 **`[R1] 사이크스 스킬 사용` 한 줄**이다.
  - 시전자는 `_turnOwner`로 본다 — `LandBuffsS2C`에는 playerId가 없다.
  - 한 스킬에 이 메시지가 **연달아 두세 개** 오므로 `_saidSkillUse`로 한 행동에
    한 번만 찍는다. 턴/라운드가 바뀔 때 푼다.
  - `Buff.Source`가 카드/이벤트면 아무것도 안 찍는다. 그건 이미 그쪽 줄이 말했고
    "스킬 사용"이라고 적으면 틀린 말이 된다.
  - 사라지는 소환물도 줄을 안 남긴다. 다만 `_landSummons`에서는 빼야 같은 칸에
    다시 소환됐을 때 "새로 생김"으로 잡힌다.
- **송금은 한 줄로 합친다 (v1.17.0).** 보낸 쪽과 받은 쪽이 **서로 다른 메시지로
  온다** — 한 `UpdateHeroAttr` 안에 둘 다 들어있지 않다 (실측):
  ```
  [R1] 루루 (상점 구매)          [R1] 패니 (상점 구매)
          루루 골드 12→7  -5              패니 골드 7→12  +5
  ```
  그래서 골드 한 건만 말하는 독립된 메시지를 `_goldHold`에 600ms 붙들었다가 짝이
  오면 `[R1] 루루 → 패니 5골드 (상점 구매)  [12→7, 7→12]`로 합친다.
  - **짝의 조건은 넷 다** — 같은 원인 · 다른 사람 · 크기가 같고 부호가 반대 ·
    같은 시간대. 하나라도 빼면 무관한 두 사람의 수입/지출이 우연히 맞아떨어질 때
    **없는 송금을 만들어낸다.**
  - 짝이 없는 경우가 더 흔하다 (상점에 낸 돈, 라운드 수입). 그땐 붙들어 둔 줄을
    **원래 모양 그대로** 내보낸다.
  - 푸는 곳: `Emit`(출력이 이 한 곳으로 모이므로 여기서 풀면 순서가 안 뒤집힌다),
    `OnFrame` 첫머리의 시간 초과, `NewPage`, `Reset`. **`Emit`은 `FlushGold` →
    `EmitLine` 두 단계다** — `FlushGold` 자신은 `EmitLine`을 불러야 재귀가 안 난다.
  - 머리줄이 이미 딴 데 딸려 있으면(`alreadySaid`) 합치지 않는다. 그 덩어리에서
    줄 하나만 빼내 위로 올리면 문맥이 끊긴다.
  - 한 메시지에 골드 변화가 둘 이상이면 어느 쪽이 송금인지 알 수 없어 합치지 않는다.
- **쓰러지면 게임이 걸린 버프를 한꺼번에 지운다.** 그 해제는 사건이 아니라 정리라서,
  HP가 0이 된 뒤 3초(`DownWindow`) 안의 해제는 대상별 한 줄
  (`버프 해제 X N개 (쓰러짐)`)로 합친다. 칩 버프는 합치지 않는다. 창을 두는 이유는
  `SaidWindow`와 같다 — 한참 뒤의 해제까지 쓰러짐 탓으로 돌리지 않게.
- **효과카드가 건 버프는 원인 없이 따로 온다.** 줄이 전부 그 카드 이름을 담고 있으면
  직전 카드 줄에 머리줄 없이 잇는다.
- **카드 기록(`LogCards`)을 꺼도 효과카드는 원인을 말한 것으로 둔다.** 그러지 않으면
  뒤따르는 `UpdateHeroAttr`의 머리줄이 `(카드 "이름")`으로 카드를 그대로 드러낸다.
- **행위자가 없으면 차례 주인은 추측일 뿐이다.** 효과를 받은 사람이 차례 주인이 아니면
  틀린 주어가 된다 (실측: 파파라 차례에 사이크스가 받은 처치 보상이 `[R7] 파파라` 아래에
  찍혔다). 그래서 대상 중에 차례 주인이 없으면, 대상이 한 명일 때 그 사람을 주어로 쓰고
  여럿이면 주어를 비운다.
- **게임 종료 구분선은 미룬다.** 종료와 같은 순간에 정리성 갱신(회복량 0, 버프 해제)이
  뒤따라 온다. 구분선이 그 앞에 끼지 않게 `UpdateHeroAttr`/`HeroSkillMoveEffect`/
  `LandBuffs`가 아닌 메시지가 처음 올 때 내보낸다.

## 칩 획득은 SelectRelicS2C가 확정한다 (v0.1.3)

**버프에서 추론하면 안 된다.** 한때 "처음 보는 칩 버프가 생겼는가"로 획득을 가렸는데,
그러면 버프를 안 만드는 칩·uid가 0인 칩·획득과 버프가 따로 오는 칩을 놓친다.

게임은 `SelectRelicS2C`(5212)에서만 `UpdateSelectedRelic`을 부른다
(`GameLogic/RelicLogic.OnSelectRelicS2CServerCallBack`):

```csharp
if (... && errid == 0 && !model.IsReroll)
{
    if (model.RelicId != 0)
        GetPlayerDataById(model.PlayerId).UpdateSelectedRelic(model.RelicId);
}
```

그래서 **획득 조건은 `!IsReroll && RelicId != 0`**이고, 이 메시지가 유일한 확정
신호다. `BuffLine`의 `칩 획득`은 이게 안 오는 경로(체크포인트 등)를 위한 보조로만
남기고, 방금 5212가 말했으면 생략한다.

`UpdateHeroAttr`의 relic 원인은 획득과 발동을 구별하지 못하므로
(이미 가진 칩이 발동할 때도 같은 원인이 붙는다) 효과 줄이 없으면 그냥 버린다.

## HP 증감은 RealChangeHp다 (v0.1.3)

`HeroHpChangeS2C`에는 `ChangeHp`(2)와 `RealChangeHp`(5)가 따로 있다. **화면에 뜨는
숫자는 RealChangeHp**이고, 실제 HP도 그걸로 잡는다 — `Core.Unit/BattleProperty.OnLifeChanged`:

```csharp
curHp = (HpChange.RealHp == 0) ? (HP.Value + HpChange.RealChangeHp) : HpChange.RealHp;
if (_characterInst != null && (HpChange.RealChangeHp != 0 || !isFight))
    _characterInst.signal.attrChange.Dispatch((HpChange.OriHp, HpChange.CurrHp, HpChange.RealChangeHp, 5), "");
```

**틀렸던 가정 (v0.1.3 후보에서 폐기):** "`RealChangeHp`를 쓰면 최대 체력에서 받은 회복
줄이 사라진다"고 적었는데 실측에서 10건이 전부 통과했다. 서버는 **넘친 회복도
`RealChangeHp`에 싣는다** — 10건 모두 `CurrHp == MaxHp`였고 `real`이 +2/+3이었다.

그래서 필터는 `real == 0`이 아니라 **"전후가 같은데 이미 최대 체력인 증가"**를 버린다.
전후가 같은 그 밖의 경우는 정체를 몰라 남긴다.

**아직 미확정인 것:**

- 화면의 최종 HP가 `CurrHp`인지 `RealHp`인지 (지금은 `OriHp`/`CurrHp`로 찍는다)
- `RealChangeHp`가 실제 반영량인지 효과가 주장하는 변화량인지
- `ori == curr && real != 0`이 넘친 회복 말고 또 언제 나오는지

`TraceFrames`를 켜면 **필터에 걸린 것까지 포함해** 진단이 남는다:

```
hp filtered=max-heal pid=... ori=9 curr=9 change=2 real=2 realHp=... max=9 dmg=... killer=...
```

표본을 모으기 전에는 `HP 9→9 [회복 +2 초과]` 같은 표기를 넣지 않는다.

## 스킬 메아리 억제는 휴리스틱이다 (v0.1.3)

액티브 스킬은 `스킬 사용` 줄을 낸 뒤 **같은 스킬의 버프**를 또 만든다. 그래서
`스킬 발동` 줄이 겹친다(실측 20줄 중 11줄).

겹치는 자리가 셋이라 막는 곳도 셋이다.

| 겹침 | 막는 곳 |
| --- | --- |
| 액티브 `스킬 사용` ↔ 뒤따르는 버프 | `AlreadySaidSkill` (같은 스킬 id + 같은 행위자 + 250ms) |
| 한 메시지 안에서 같은 (스킬, 대상) | `_skillFired` (대상이 다르면 각각 남긴다) |
| 원인 머리줄 `(스킬 "X")` ↔ 본문 `스킬 발동 "X"` | 본문을 머리줄 자리로 **끌어올린다** |

셋째를 "본문을 지운다"로 하면 효과 줄이 없을 때 `lines.Count == 0`에 걸려 **사건이
통째로 사라진다.** 본문에는 대상이 있고 머리줄에는 없으므로 본문을 남기는 쪽이 맞다.

억제는 **같은 스킬 id + 같은 행위자 + 250ms 안**으로만 한다. 실측에서 메아리는 전부
1ms 안에 왔고, `SaidWindow`(2초)를 그대로 쓰면 다음 턴 남의 발동까지 삼킨다.

버프의 **대상**은 시전자가 아니지만, 메시지의 **행위자**(`UpdateHeroAttrS2C` 필드 1)는
시전자다 — 실측에서 메아리 줄이 `스킬 사용` 바로 아래에 머리줄 없이 붙었다(같은 원인·
같은 주체로 판정된 것). 그래서 행위자가 다르면 같은 스킬이라도 독립 발동으로 본다
(v0.1.6). 행위자가 없는 메시지에서는 여전히 시간만 본다 — **그 경우에 한해 다른 사람의
같은 스킬 발동이 가려질 수 있는 휴리스틱이다.**

`스킬 발동` 줄의 이름은 **시전자가 아니라 버프 대상**이다. 주어 자리에 놓지 말고
`스킬 발동 "허약의 표식" → 마법 찻주전자1`로 대상임을 드러낼 것.

## v0.1.3 수정 후 실측

최종 후보 DLL로 한 판을 더 진행해 591줄 로그를 확인했다. 이 표는 수정 전 재현 수치와
섞지 않은 **수정 후 결과**다.

| 확인 항목 | 결과 |
| --- | --- |
| 칩 획득 / 발동 / `칩 갱신` | 14건 / 7건 / 0건 |
| 임시 참가자 줄(계정 닉네임·숫자 id) | 0건 |
| 전후 HP가 같은 증감 줄 | 0건 |
| 동일한 스킬 발동의 완전 중복 | 0건 |

검증 DLL의 SHA-256은
`f3ebdde1d10f773e6cc5fcea1cb880dc8aa599786d2b6b41ec3a7cf376f66684`다.
화면의 최종 HP가 `CurrHp`인지 `RealHp`인지와 최대 체력이 아닌 HP 무변화의 의미는 이
표본만으로 확정하지 않았다.

## 칩·스킬 줄에서 걷어낸 것 (v0.1.2)

실측 한 판(1155줄)에서 불필요한 줄 89개의 원인을 찾았다.

- **원인 종류만으로 칩 획득과 발동을 가르면 안 된다.** `CauseOrigin.source == relic`(17)은
  칩을 *얻을* 때만이 아니라 **이미 가진 칩이 발동할 때도** 붙는다. 그래서 "능력치 없는
  칩 획득을 살리자"고 둔 예외(`lines.Count == 0 && cause != relic`이면 return)가 효과 줄
  없는 빈 머리줄을 통과시켰다 — **한 판에 63줄**, 그중 24줄이 `손전등 - 강력` 하나다.

  (v0.1.3에서 판별을 `SelectRelicS2C`로 옮겼다 — 위 절 참고. 효과 줄이 없으면 버린다.)
- **원인이 칩 이름을 안 알려주면(`id == 0`) 버프 줄을 버리지 않는다.** 그땐 `Buff.Source`가
  유일한 단서다. 이름 없는 `(칩)` 머리줄이 12개 있었다.
- **`칩 갱신`은 아예 쓰지 않는다.** 사용자에게 사건이 아니고(이미 가진 칩이다), 달라진
  값은 바로 옆 ATK/DEF 줄이 보여준다. 라운드마다 갱신만 오는 칩이 있어서
  (`8면체 주사위` 한 판에 15줄) 숫자 없는 줄만 쌓였다. `칩 획득`/`칩 잃음`만 남긴다.
- **"스킬 출처 버프가 새로 걸림 = 패시브"는 틀린 전제다.** 액티브 스킬도 같은 버프를
  만들어서 `스킬 사용 "X"` 바로 뒤에 `스킬 발동 ... "X"`가 또 붙었다 (이름 붙은 20줄 중
  **11줄**). 방금 `Said(SkillCause, skillId, ...)`로 발표한 스킬이면 생략한다.
- **`스킬 발동` 줄의 이름은 시전자가 아니라 버프 대상이다.** `BuffLine`의 `pid`를 주어
  자리에 놓는 바람에 카이세이가 건 스킬이 `스킬 발동 마법 찻주전자1 "허약의 표식"`으로
  나왔다 — 44줄 중 **22줄**이 몹을 주어로 달고 있었다. 누가 걸었는지는 선에 실려 오지
  않으므로(`Buff.Source`는 어느 스킬인지까지만 준다) 주어를 만들지 말고
  `스킬 발동 "허약의 표식" → 마법 찻주전자1`로 대상임을 드러낼 것.
- **HP가 그대로인데 증감만 오는 알림을 버린다.** (v0.1.3에서 판정을 `RealChangeHp`로
  바꿨다 — 위 절 참고.)

## 같은 사건을 두 번 말하지 않는다 (v1.8.0)

`UseEffectCardS2C`가 `효과카드 "레이저"`를 찍고, 곧바로 오는 `UpdateHeroAttr`의
`cause`가 또 `(카드 "레이저")`를 찍는다 — 같은 사건인데 줄이 둘, 행위자 이름이 셋.

그래서 **직전에 발표한 원인을 `_saidSource/_saidId/_saidActor`에 기억해두고**, cause가
그것과 같으면 머리줄을 건너뛴다. **주체(`_saidActor`)까지 같아야 한다** — 칩 선택은
네 명이 같은 밀리초에 몰려 오고 칩 풀이 공유라 같은 칩 id가 겹칠 수 있는데, 주체를
안 보면 둘째 사람의 머리줄이 사라지고 그 효과가 첫째 사람 밑에 붙는다.
PK(battle)만 예외다 — 머리줄은 공격자 이름인데 효과는 방어자에게 나는 게 정상이라서. 효과 줄만 들여쓰기로 이어 붙어 한 덩어리로 읽힌다.
가해자(`killer`)가 그 덩어리의 주체와 같을 때 `← 누구`도 생략한다.

기억은 `ActionStartNotify`(턴 전환)에서 버린다. 안 버리면 다음 턴의 같은 카드가
머리줄 없이 나와 문맥이 사라진다.

발표하는 쪽: `DecodeUseEffectCard`(카드), `DecodeBattle`(PK), 머리줄을 찍은
`DecodeUpdateHeroAttr` 자신. 새 줄을 추가할 때 `Said(...)`를 부를지 판단할 것.

## 라운드별 페이지

오버레이는 라운드 하나가 한 페이지고, 그 안에서는 **줄 단위로 스크롤한다**
(`_view` = 라운드, `_offset` = 맨 위에 보이는 줄 번호). 플레이어 4명 + 몹이면 라운드
하나가 50줄을 넘어서, 라운드 단위로만 넘기면 대부분을 볼 수 없다.

**스크롤은 마우스 휠이다.** `Input.mouseScrollDelta`를 폴링하되, **커서가 창 위에 있을
때만** 먹는다 (`RectTransformUtility.RectangleContainsScreenPoint`) — 안 그러면 게임
화면을 돌리려는 휠질까지 로그를 스크롤한다.

스크롤은 라운드 경계를 **연속으로** 넘는다 — 라운드 맨 위에서 더 올리면 남은 양만큼
이전 라운드의 끝에서 마저 움직인다. 맨 끝을 보고 있으면 새 줄을 따라가고, 위로
올리면 따라가기를 멈춘다 (머리줄에 `(최신 아님)` 표시).

머리줄은 `Round 1   13-26/57` — 라운드 번호와 지금 보이는 줄 범위다.

`RoundStart`/`GameRoundChange`에서 `MirrorNewPage(round)`로 페이지를 연다.

**창은 기본으로 숨어 있다가 라운드가 시작되면 저절로 뜬다.** 게임을 켜자마자 로비에
전투 로그창이 떠 있으면 방해만 된다. 씬 전환이 아니라 라운드 시작을 신호로 쓰는데,
전투 씬 로드는 라운드 시작보다 먼저 끝나기 때문이다.

`OpenPage(0)`은 라운드 신호보다 줄이 먼저 올 때(방 입장 시 참가자 목록) 만들어지는
임시 페이지라 **자동으로 띄우지 않는다.** 여기서 띄우면 "게임 키자마자 뜬다"는 그
문제가 그대로 돌아온다. 씬을 나가면 `LeaveScene()`이 비우고 숨긴다.

사용자가 직접 끈 경우(`_userHidden`)는 자동으로 다시 켜지 않는다. 그 플래그는
씬을 나갈 때 풀린다.

**오버레이는 입력을 가로채지 않는다.** 폴링만 하므로 같은 키(`ToggleKey`)가 게임에도
그대로 전달된다. 그래서 키 페이징은 넣지 않았다 — 방향키가 게임 조작과 겹친다.

소켓 스레드 → 메인 스레드 통로는 `ConcurrentQueue<Signal>`이고 `Signal`은
`Line/Page/Clear` 세 가지다. **줄과 경계를 같은 큐로 보내야 순서가 어긋나지 않는다** —
별도 플래그로 알리면 안 된다.

## 언제 비우는가 — 둘을 구분할 것

게임에 리플레이가 있어서 지난 판 기록을 들고 있을 이유가 없다. 다만 **화면을 비우는
것과 상태를 비우는 것은 시점이 다르다.**

| | 시점 | 하는 일 |
| --- | --- | --- |
| 숨기기 | **전투 씬을 벗어날 때** | 창만 숨긴다. 내용은 그대로 |
| 비우기 | 새 판 시작 신호 (아래 "판 시작 신호") | 페이지·명단·라운드·판별 상태·`battle-log.txt` |

**씬 전환 자체를 "게임을 나갔다"로 보면 안 된다.** 로딩 씬을 거치느라 전투 *중에도*
씬이 한 번 더 바뀌고, 거기에 걸어두면 창이 꺼지고 페이지가 날아간다 (실측: 자동으로
안 켜지고, 그 뒤 줄들이 임시 페이지를 만들어 머리줄이 `Round 0`으로 나왔다).

그래서 라운드가 시작될 때 그 시점의 씬 이름을 `_battleScene`에 기억해 두고,
**그 씬을 벗어날 때만** 나간 것으로 본다. 내용 비우기는 씬과 무관하게
판 시작 신호가 담당한다 — 판이 끝난 뒤 결과 화면에서도 마지막 로그를 볼 수 있다.

**상태를 씬 전환에 걸면 안 된다.** 씬 전환은 게임에 *들어갈* 때도 일어나서, 방에서
받아둔 명단과 라운드를 통째로 날려버린다. v1.7.0에서 실제로 겪었다 — 이름이 전부
`?1451909`로, 라운드가 전부 0으로 나왔다 (페이지도 지워져서 `OpenPage(0)` 기본 페이지가
만들어졌다). 판의 시작은 씬이 아니라 판 시작 신호다.

### 판 시작 신호

**`StartGameS2C`는 사용자 방에서만 온다.** 한때 이것 하나만 판 시작으로 봤더니, 게임을
끄지 않고 매칭으로 연속 플레이할 때 초기화가 한 번도 안 돌았다 (실측: 두 번째·세 번째
판에서 `battle-log.txt`가 비워지지 않았고, 참가자가 `린2`/`보니2`로, 새 판 첫 줄이 지난
판의 `[R5]`로, 몹 번호가 `마법 찻주전자9`까지 이어졌다. 지난 판에도 있던 내 캐릭터는
"바뀐 것 없음"이라 참가자 줄에서 빠졌다).

디컴파일 결과 방을 만들고 픽창(`RoomStateType.CHOICE`)을 여는 메시지는 셋이다:

| 메시지 | cmdID | 게임 쪽 처리 | 우리가 읽는 것 |
| --- | --- | --- | --- |
| `StartGameS2C` | 5020 | `RoomLogic` — 사용자 방 시작 | 허용목록. `Reset()` 후 Room 명단 |
| `MatchSuccessS2C` | 5226 | `MatchLogic` — 매칭 성사 | **opcode와 ERR만** (`Op.GameEntry`) |
| `SingleCampaignS2C` | 5230 | `RoomLogic` — 싱글 캠페인 | **opcode와 ERR만** (`Op.GameEntry`) |

- 5226/5230은 본문(방 전체, 손패 포함)을 읽을 이유가 없어 허용목록에 넣지 않았다.
  헤더만 보고 `BeginGame`을 부른다. 명단은 뒤이어 오는 `RunningGameS2C`에서 읽는다.
- **헤더의 ERR(오프셋 33)이 0이 아니면 무시한다.** 게임도 실패 응답에서는 방 설정
  화면만 새로 그린다. 이를 위해 `SocketTap.OnFrame`이 `(cmdId, errId, body)`를 넘긴다.
- **안전망: 방이 바뀌면 새 판이다.** `RunningGameS2C`는 한 판에 여러 번 오므로 그
  자체로는 신호가 아니다. 대신 `Room.Id`(필드 1, sfixed64)를 기억해 두고, 다른 방이
  오면 그때 한 번 초기화한다. 매칭·싱글은 판마다 방을 새로 만들고, 같은 방에서 다시
  하는 판은 `StartGameS2C`가 맡는다. 판 시작 신호 직후와 플러그인이 판 중간에 켜진
  경우는 방을 모르는 상태(`_roomId == 0`)라, 처음 본 방을 그대로 받아들인다 — 그래서
  신호 뒤 첫 `RunningGameS2C`가 두 번 초기화하지 않는다. 방 id는 출력하지 않는다.
- `StartGameS2C` 처리에서 **`Reset()`을 먼저 부르고 나서 Room을 읽어야 한다** — 그
  메시지 자체가 이번 판 명단을 싣고 오기 때문이다.
- `Reset()`은 명단·이름별 인원수·몹 번호·라운드·차례·버프/소환물·골드 짝·스킬/카드
  중복 판정·머리줄 문맥까지 **한 판짜리 상태를 전부** 비운다. 필드를 새로 추가하면
  여기도 추가할 것.
- **진단**: BepInEx 로그에 `[game]` 줄이 남는다 — 판 시작(어느 신호였는지), 방 채택/변경,
  `RunningGame`마다 명단 변경 수와 출력 수, 라운드, `GameFinish`, 무시한 실패 응답.
  opcode와 상태 전환만 적고 uid·방 id·본문은 적지 않는다. 연속 플레이에서 새 판이
  제대로 잡히는지는 이 줄들로 확인한다.
- 방 id 안전망으로 새 판을 잡으면 오버레이는 그 순간을 픽창으로 보고 씬 전환을
  기다린다. 이미 전투 씬이라면 두 번째 라운드에서 뜬다 (위 "자동 표시").

같은 판 안의 번호는 유지된다. 같은 캐릭터를 둘이 고르면 `린1`/`린2`, 몹은 나올 때마다
`마법 찻주전자1`, `2`, … 번호는 **그 종류에서 아직 안 쓴 가장 작은 수**다 — 인원수+1로
매기면 픽창에서 캐릭터를 바꿨다가 다른 사람이 같은 캐릭터를 고를 때 번호가 겹친다.

- 씬 감지는 `SceneManager.GetActiveScene()` **폴링**이다. 호출은 안전하고, 금지된 건
  `sceneLoaded += ...` 같은 IL2CPP 이벤트 구독이다.
- 그래서 `FramePump`는 오버레이를 꺼도 **항상** 패치한다.
- 소켓 스레드에서 오버레이를 비울 땐 `LogOverlay.GameStarted()`(큐 경유)를 쓴다.
  리스트를 직접 비우면 다른 스레드에서 만지게 된다.

## 로그가 애니메이션보다 빠르다

> v0.1.6부터 오버레이는 실측한 연출 길이로 순차 재생 시점을 추정한다.
> 아래는 이전 즉시 표시 방식의 판단과 배경이다. 현재 방식과 검증 상태는
> [OVERLAY-TIMING.md](OVERLAY-TIMING.md)에 기록했다.

네트워크 계층에서 읽으므로 **서버 패킷이 도착한 순간** 줄이 만들어지고, 게임은 그
뒤에 애니메이션을 재생한다. 봇과 하면 서버가 몰아서 보내기 때문에 특히 두드러진다.

**서버가 보내는 대로 바로 찍는다.** 예전에는 턴 경계까지 줄을 모아뒀다가 내보내는
장치가 있었는데(`HoldUntilTurnChange`/`QuietFlushMs`/`MaxHoldSeconds`), 모아 보내면
자기 턴 내내 화면이 비어 있어서 너무 늦다는 판단으로 꺼둔 채 쓰다가 v1.20.0에서
들어냈다. 되살릴 일이 있으면 `LogOverlay`에 staging 리스트를 다시 두고
`Signal.Flush`를 추가하는 형태가 된다.

**PvP에서는 이게 작은 정보 우위가 될 수 있다** — 애니메이션이 끝나기 전에 결과를
아는 것이므로. 이 모드를 쓴다는 건 그걸 받아들인다는 뜻이다.

참고로 이 게임에는 **행동 종료 메시지가 없다** (`ActionStartNotifyS2C`의 짝이 없고
`ActionOverTimeLogS2C`는 타임아웃 기록용이다). 확인함 — 그래서 "행동이 끝났다"를
알려면 다음 턴 시작을 기다리는 수밖에 없었다.

## 검증 로그 읽기

게임 실행 후:
- `BepInEx/LogOutput.log` — 플러그인 로드 여부, 패치 성공/실패
- `BepInEx/plugins/AstralPartyBattleLog/battle-log.txt` — 전투 로그

전투 로그 줄은 **오버레이와 `battle-log.txt`로만** 가고 BepInEx 로그로는 보내지 않는다 —
한 판에 수백 줄이라 다른 로그가 파묻힌다. 진단 출력(`[game]`, `TraceFrames` 등)은
반대로 BepInEx 로그로만 간다.

진단 옵션 둘 다 기본 꺼짐 (`BepInEx/config/astralparty.battlelog.cfg`):

- `TraceFrames` — 허용목록 밖 프레임의 **opcode와 길이만** 남긴다. 새 메시지를
  찾을 때 켠다. 본문은 파싱하지 않는다.
- `HexDumpOpcodes` — 지정한 cmdID의 본문을 hex로 덤프한다. 디코딩이 안 맞을 때
  쓴다. 허용목록 안의 opcode만 대상이 되므로 카드 메시지는 덤프되지 않는다.
  필드 번호와 wire type을 손으로 읽는 게 가장 빠른 진단이다.
