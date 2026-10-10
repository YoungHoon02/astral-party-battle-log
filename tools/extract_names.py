#!/usr/bin/env python3
"""
게임 에셋에서 카드/스킬/버프 이름표를 뽑아 names.tsv를 만든다.

로그에 찍히는 `card#21004` 같은 id를 사람이 읽는 이름으로 바꾸기 위한 것.
플러그인은 이 파일을 읽기만 하고, 없으면 그냥 id로 표시한다.

동작 원리
---------
설정 테이블은 Addressables 라벨 `GameData_INT`의 TextAsset이고 내용은 protobuf다
(`StaticConfigure.OnConfigureLoaded`가 에셋 이름으로 분기한다). 정보표와 문자열표가
나뉘어 있어서 두 번 조인한다:

    Card  { 1: repeated CardInfoConfigure  {1:Id, 2:NameID} }
    STRCard { 1: repeated STRCardLocalConfigure
              {1:Id, 2:간체, 3:영어, 4:일본어, 5:번체, 6:한국어} }

주의: 한글패치가 깔린 INT 빌드는 **영어 슬롯(필드 3)에 한국어를 넣는다.** 그래서
한국어(6) → 영어(3) → 간체(2) 순으로 비어있지 않은 것을 고른다.

사용법
------
1) 게임을 한 번 실행해 Addressables 캐시를 채운다 (이미 플레이했으면 되어 있음)
2) 에셋 추출:
     python <skills>/unity-bundle-inspector/scripts/scan_bundles.py \
       "%USERPROFILE%/AppData/LocalLow/feimo/AstralParty_INT/com.unity.addressables" \
       --text -o gamedata --grep '^(Card|Skill|Buff|STRCard|STRSkill|STRBuff)$'
3) 이 스크립트:
     python tools/extract_names.py gamedata names.tsv

게임이 업데이트되면 id가 바뀔 수 있으므로 다시 돌리면 된다.
"""
import os
import struct
import sys


class Reader:
    def __init__(self, buf, start=0, end=None):
        self.b = buf
        self.p = start
        self.e = len(buf) if end is None else end

    def varint(self):
        v = shift = 0
        while self.p < self.e:
            c = self.b[self.p]
            self.p += 1
            v |= (c & 0x7F) << shift
            if not c & 0x80:
                return v
            shift += 7
        return v

    def tag(self):
        if self.p >= self.e:
            return None, None
        t = self.varint()
        return t >> 3, t & 7

    def number(self, wire):
        # 이 게임의 .proto는 숫자를 sfixed32/sfixed64로 쓴다. enum만 varint.
        if wire == 0:
            return self.varint()
        if wire == 5:
            v = struct.unpack_from("<i", self.b, self.p)[0]
            self.p += 4
            return v
        if wire == 1:
            v = struct.unpack_from("<q", self.b, self.p)[0]
            self.p += 8
            return v
        raise ValueError(f"unexpected wire type {wire}")

    def chunk(self):
        n = self.varint()
        start = self.p
        self.p += n
        return start, n

    def skip(self, wire):
        if wire in (0, 1, 5):
            self.number(wire)
        elif wire == 2:
            self.chunk()
        else:
            raise ValueError(f"unexpected wire type {wire}")

    def fields(self):
        while self.p < self.e:
            f, w = self.tag()
            if f is None:
                return
            yield f, w


def parse_info_table(data, name_field, list_field=1):
    """Configure { <list_field>: repeated Item {1:Id, <name_field>:NameID} } -> {id: nameId}"""
    out = {}
    r = Reader(data)
    for f, w in r.fields():
        if f != list_field or w != 2:
            r.skip(w)
            continue
        start, n = r.chunk()
        item = Reader(data, start, start + n)
        ident = name_id = 0
        for sf, sw in item.fields():
            if sw == 2:
                item.chunk()
                continue
            v = item.number(sw)
            if sf == 1:
                ident = v
            elif sf == name_field:
                name_id = v
        if ident and name_id:
            out[ident] = name_id
    return out


# 색상용 열거형 표. (로그 종류, 정보표 에셋, 열거형 필드 번호, 값 이름, 기본값)
#
# 이름표와 달리 문자열표와 조인하지 않는다 — 열거형 값 자체가 의미이고, 색은
# 플러그인의 Palette가 정한다.
ENUM_TABLES = [
    # CardInfoConfigure.CardType(9): None/Attack/Defend/Effect/Counter/Event/Luck/Jinx/Curse
    # 게임은 공격 FF0000 / 방어 0099FF / 그 외 00CC00으로 칠한다
    # (GameLogic.BattleCardMessage.GetCardMsg).
    ("cardtype", "Card", 9, {1: "Attack", 2: "Defend"}, "Other"),
    # RelicInfoConfigure.RelicQualityType(2): None/Blue/Purple/Orange — 칩 등급.
    # 게임은 #004DFF / #9700E6 / #EE8F00으로 칠한다
    # (GameLogic.BattleRelicMessage).
    ("relicgrade", "Relic", 2, {1: "Blue", 2: "Purple", 3: "Orange"}, "None"),
]


def parse_enum_table(data, field, names, default):
    """정보표에서 {id: 열거형 이름}을 뽑는다. 문자열표를 거치지 않는다."""
    out = {}
    r = Reader(data)
    for f, w in r.fields():
        if f != 1 or w != 2:
            r.skip(w)
            continue
        start, n = r.chunk()
        item = Reader(data, start, start + n)
        ident = value = 0
        for sf, sw in item.fields():
            if sw == 2:
                item.chunk()
                continue
            v = item.number(sw)
            if sf == 1:
                ident = v
            elif sf == field:
                value = v
        if ident:
            out[ident] = names.get(value, default)
    return out


def parse_string_table(data):
    """STRxConfigure { 1: repeated Local {1:Id, 2:cn, 3:en, 4:jp, 5:tw, 6:ko} }"""
    out = {}
    r = Reader(data)
    for f, w in r.fields():
        if f != 1 or w != 2:
            r.skip(w)
            continue
        start, n = r.chunk()
        item = Reader(data, start, start + n)
        ident = 0
        texts = {}
        for sf, sw in item.fields():
            if sw == 2:
                s, ln = item.chunk()
                texts[sf] = data[s:s + ln].decode("utf-8", "replace")
            else:
                v = item.number(sw)
                if sf == 1:
                    ident = v
        if ident:
            # 한글패치는 영어 슬롯을 덮어쓴다. 한국어 → 영어 → 간체 순.
            for slot in (6, 3, 2):
                text = texts.get(slot, "").strip()
                if text:
                    out[ident] = text
                    break
    return out


# 캐릭터/몹이 가진 스킬. (종류, 정보표 에셋, 액티브 필드들, packed 패시브 필드들)
#
# **이건 이름이 아니라 대응표다** — `charskill <heroId> <스킬이름, 스킬이름>` 꼴로
# 넣는다. 로그의 참가자 줄에 "이 캐릭터가 무슨 스킬을 갖고 있나"를 보여주려는 것.
# 패시브는 발동해도 서버가 "스킬 썼다"는 메시지를 보내지 않고 버프로만 오기 때문에,
# 미리 알고 있어야 로그를 읽을 때 짚어낼 수 있다.
#
# CharacterInfoConfigure {18:ActiveSkill, 19:PveActiveSkill,
#                         20:PassiveSkills, 21:PvePassiveSkills}
# MonsterInfoConfigure   {17:ActiveSkill, 18:PassiveSkills}
# 20/21/18은 **packed repeated sfixed32**다 — 숫자로 읽으면 통째로 건너뛴다.
SKILL_TABLES = [
    ("charskill", "Character", {18, 19}, {20, 21}),
    ("monskill", "Monster", {17}, {18}),
]


def parse_skill_table(data, active_fields, packed_fields):
    """{id: [스킬 id, ...]} — 중복은 없애고 등장 순서를 지킨다."""
    out = {}
    r = Reader(data)
    for f, w in r.fields():
        if f != 1 or w != 2:
            r.skip(w)
            continue
        start, n = r.chunk()
        item = Reader(data, start, start + n)
        ident = 0
        skills = []
        for sf, sw in item.fields():
            if sw == 2:
                cs, cn = item.chunk()
                if sf in packed_fields:
                    for off in range(cs, cs + cn, 4):
                        skills.append(struct.unpack_from("<i", data, off)[0])
                continue
            v = item.number(sw)
            if sf == 1:
                ident = v
            elif sf in active_fields and v:
                skills.append(v)
        if ident and skills:
            out[ident] = list(dict.fromkeys(s for s in skills if s))
    return out


# (로그에 쓰는 종류 이름, 정보표 에셋, 이름 필드 번호, 문자열표 에셋)
#
# 정보표는 전부 `{1: repeated XxxInfoConfigure}` 꼴이고, 항목의 필드 1이 id다
# (Land만 id 필드 이름이 LandType이지만 번호는 똑같이 1).
#
# 버프의 이름 필드는 `NameId`(14)다 — 대문자 `NameID`가 아니라서 처음에 못 찾고
# `DescId`(15)를 썼다가 로그가 설명문으로 덮였다. 14를 쓰면 "왕의 힘", "반격" 같은
# 짧은 이름이 나온다.
def clean(text):
    """Log/NameConfig.cs의 Clean과 같아야 한다."""
    return text.replace("\t", " ").replace("\r", " ").replace("\n", " ").strip()


CHAT_INFO = "Chat"
CHAT_STRINGS = "STRChat"
# ChatConfigure {1:Infos, 2:InfoDict, 3:Marks, 4:MarkDict} — 마커는 field 3 목록이고
# ChatMarkConfigure {1:ID, 2:ChatID}의 ChatID로 문자열표와 조인한다.
CHAT_MARK_LIST = 3
CHAT_MARK_TEXT = 2

# Log/NameConfig.cs의 Schema와 같이 올린다. 낮으면 플러그인이 names.tsv를 다시 만든다.
SCHEMA = 2

TABLES = [
    ("card", "Card", 2, "STRCard"),
    ("skill", "Skill", 4, "STRSkill"),
    ("buff", "Buff", 14, "STRBuff"),
    ("event", "Event", 5, "STREvent"),
    ("land", "Land", 5, "STRLand"),
    ("relic", "Relic", 7, "STRRelic"),
    ("monster", "Monster", 3, "STRMonster"),
    ("character", "Character", 4, "STRCharacter"),
]


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    src, dest = sys.argv[1], sys.argv[2]

    rows = []
    for kind, info_name, name_field, str_name in TABLES:
        info_path = os.path.join(src, info_name)
        str_path = os.path.join(src, str_name)
        if not (os.path.exists(info_path) and os.path.exists(str_path)):
            print(f"  건너뜀: {kind} ({info_name}/{str_name} 없음)", file=sys.stderr)
            continue

        ids = parse_info_table(open(info_path, "rb").read(), name_field)
        texts = parse_string_table(open(str_path, "rb").read())
        hit = 0
        for ident, name_id in sorted(ids.items()):
            text = texts.get(name_id, "")
            if not text:
                continue
            rows.append((kind, ident, text.replace("\t", " ").replace("\n", " ")))
            hit += 1
        print(f"  {kind}: {hit}/{len(ids)}개 이름 확보", file=sys.stderr)

    # 색상용 열거형들 (카드 종류, 칩 등급).
    for kind, info_name, field, names, default in ENUM_TABLES:
        path = os.path.join(src, info_name)
        if not os.path.exists(path):
            continue
        values = parse_enum_table(open(path, "rb").read(), field, names, default)
        for ident, value in sorted(values.items()):
            rows.append((kind, ident, value))
        print(f"  {kind}: {len(values)}개", file=sys.stderr)

    # 캐릭터/몹 → 스킬 이름 목록. 위에서 만든 skill 이름을 그대로 쓴다.
    skill_names = {ident: text for kind, ident, text in rows if kind == "skill"}
    for kind, info_name, active_fields, packed_fields in SKILL_TABLES:
        path = os.path.join(src, info_name)
        if not os.path.exists(path):
            print(f"  건너뜀: {kind} ({info_name} 없음)", file=sys.stderr)
            continue
        table = parse_skill_table(open(path, "rb").read(), active_fields, packed_fields)
        hit = 0
        for ident, skills in sorted(table.items()):
            labels = [skill_names[s] for s in skills if s in skill_names]
            if not labels:
                continue
            rows.append((kind, ident, ", ".join(dict.fromkeys(labels))))
            hit += 1
        print(f"  {kind}: {hit}/{len(table)}개", file=sys.stderr)

    # 빠른 대화 문구는 id 그대로, 마커는 마커 id → 문구 id → 문자열로 두 번 찾는다.
    chat_str_path = os.path.join(src, CHAT_STRINGS)
    if os.path.exists(chat_str_path):
        texts = parse_string_table(open(chat_str_path, "rb").read())
        for ident, text in sorted(texts.items()):
            rows.append(("chat", ident, clean(text)))
        print(f"  chat: {len(texts)}개", file=sys.stderr)
        chat_info_path = os.path.join(src, CHAT_INFO)
        if os.path.exists(chat_info_path):
            marks = parse_info_table(open(chat_info_path, "rb").read(), CHAT_MARK_TEXT, CHAT_MARK_LIST)
            hit = 0
            for ident, chat_id in sorted(marks.items()):
                if chat_id in texts:
                    rows.append(("chatmark", ident, clean(texts[chat_id])))
                    hit += 1
            print(f"  chatmark: {hit}/{len(marks)}개", file=sys.stderr)
    else:
        print(f"  건너뜀: chat ({CHAT_STRINGS} 없음)", file=sys.stderr)

    with open(dest, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(f"# schema {SCHEMA}\n")
        fh.write("# kind\tid\tname — tools/extract_names.py 가 생성. 직접 고치지 말 것\n")
        for kind, ident, text in rows:
            fh.write(f"{kind}\t{ident}\t{text}\n")
    print(f"-> {dest} ({len(rows)}줄)", file=sys.stderr)


if __name__ == "__main__":
    main()
