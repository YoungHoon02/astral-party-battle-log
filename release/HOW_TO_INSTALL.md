# Astral Party Battle Log 설치 방법

## 준비

- Astral Party가 설치되어 있어야 합니다.
- 게임에 **BepInEx 6 Unity IL2CPP**가 필요합니다. 아직 설치하지 않았다면
  [BepInEx 공식 설치 안내](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html)에
  따라 게임 실행 파일이 있는 폴더에 설치하고 게임을 한 번 실행하세요.
- 파일을 복사하거나 업데이트하기 전에 게임을 종료하세요.

## 설치 또는 업데이트

1. 릴리스의 `AstralPartyBattleLog-v*.zip` 파일을 받습니다.
2. ZIP을 열고 그 안의 `BepInEx` 폴더를 게임 실행 파일(`AstralParty_INT.exe`)이 있는
   폴더에 덮어씁니다. 기존 버전이 있다면 DLL 덮어쓰기를 허용하세요.
3. 최종 경로가 `게임 폴더/BepInEx/plugins/AstralPartyBattleLog/AstralPartyBattleLog.dll`
   인지 확인하세요. `BepInEx/BepInEx`처럼 폴더가 중첩되면 로드되지 않습니다.
4. 게임을 실행합니다. 전투 화면에서 로그창이 보이며, 기본 단축키 **F9**로 켜고 끌 수
   있습니다.

`names.tsv`는 따로 복사할 필요가 없습니다. 첫 실행 때 플러그인이 게임 데이터에서
생성합니다. 기존 설정은 `BepInEx/config/astralparty.battlelog.cfg`에 남습니다.

## 제거

게임을 종료한 뒤 `BepInEx/plugins/AstralPartyBattleLog/` 폴더를 삭제하세요. 설정도
지우려면 `BepInEx/config/astralparty.battlelog.cfg`를 별도로 삭제하세요.
