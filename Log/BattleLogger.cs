using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AstralPartyBattleLog.Net;
using AstralPartyBattleLog.Proto;
using BepInEx.Logging;

namespace AstralPartyBattleLog.Log;

/// <summary>허용목록 프레임을 디코딩해 로그 줄로 만든다. 소켓 IO 스레드에서 호출된다.</summary>
internal sealed class BattleLogger
{
    private readonly ManualLogSource _log;
    private readonly LogFileWriter? _file;
    private readonly bool _traceUnknown;
    private readonly HashSet<int> _hexDump;
    private readonly bool _logPlayerIds;
    private readonly bool _logCards;

    // 마지막 값은 주사위 줄이면 캐릭터 주사위 눈을 두 자리씩 담은 값("5+2" → 205, 모르면 0),
    // PK 줄이면 반격 여부(1/0), 차례 시작 줄이면 차례 배너가 뜨지 않는지(1/0)다. 화면 신호 짝짓기용(docs/SIGNAL-GATING.md).
    public Action<string, LineKind, long, int, int>? Mirror;
    public Action<LineKind, long, int>? MirrorAdvance;
    public Action<int, long>? MirrorNewPage;
    public Action? MirrorClear;
    public Action<int, int>? MirrorSync;

    private LineKind _kind;
    private long _group;
    private long _groupSeq;
    private int _units;
    private int _detail;
    private bool _kindFromCause;

    // PK 피해를 PK 줄과 같은 그룹에 넣는다. 따로 두면 PK 연출이 끝난 뒤에야 피해가 보인다.
    private long _battleGroup = -1;

    private readonly NameTable _names;
    private readonly Roster _roster;
    private readonly RosterCache? _cache;

    private long _cacheRoom;
    private readonly LandMap _lands = new();
    private int _round;
    private string RoundTag => _round > 0 ? $"[R{_round}]" : "[R?]";
    private long _turnOwner;
    private long _roomId;

    private readonly Dictionary<long, (long Node, long BuffId)> _landSummons = new();
    private readonly Dictionary<long, (long Pid, BuffInfo Info)> _buffs = new();
    private long _saidSkillUse;
    private GoldHold? _goldHold;

    private static readonly TimeSpan GoldPairWindow = TimeSpan.FromMilliseconds(600);

    private sealed class GoldHold
    {
        public long Actor, Pid, Change, Ori, Curr, CauseSource, CauseId;
        public string CauseText = "", Header = "", Line = "";
        public DateTime At;
        public LineKind Kind;
        public long Group;
    }

    private (long Pid, long Change, long Ori, long Curr)? _goldSeen;
    private bool _goldSeenMany;

    // 한 메시지에 같은 스킬·같은 대상이 두 번 오기도 한다(실측: `스킬 발동 "훔치기" → ?43838` 두 줄).
    private readonly HashSet<(long SkillId, long Pid)> _skillFired = new();
    private string? _causeSkillLine;

    // 한 PK에 여러 장 낼 수 있으므로(RoundStartS2C.useCardMaxNum) 같은 uid 반복만 막는다.
    private readonly HashSet<(long Pid, long CardUid)> _cardSubmits = new();
    private bool _causeNamesRelic;

    private long _saidSource = -1;
    private long _saidId;
    private long _saidActor;
    private DateTime _saidAt;

    // 한 행동의 메시지는 수 ms 안에 몰려 온다. 창이 없으면 한참 뒤의 무관한 줄이 머리줄 없이 붙는다.
    private static readonly TimeSpan SaidWindow = TimeSpan.FromSeconds(2);

    private readonly Dictionary<long, DateTime> _downAt = new();
    private readonly Dictionary<long, (int Index, int Count)> _downFold = new();
    private static readonly TimeSpan DownWindow = TimeSpan.FromSeconds(3);

    private bool _finishPending;

    private readonly HashSet<long> _msgTargets = new();

    private (byte[] Body, long Group, LineKind Kind, int Units, long BattleGroup)? _deferred;
    private string? _saidCardName;

    private static readonly Dictionary<int, (long BuffId, string Fallback)> Stacks = new()
    {
        [15] = (10004, "치유"),
        [16] = (10005, "스타라이트"),
        [17] = (10006, "표식"),
        [21] = (10008, "반격"),
        [22] = (10009, "개조"),
        [28] = (10012, "정신"),
        [31] = (10014, "에너지"),
        [32] = (10013, "증거"),
    };

    public BattleLogger(ManualLogSource log, string? filePath, bool traceUnknown,
                        HashSet<int> hexDump, bool logPlayerIds, bool logCards,
                        NameTable names, RosterCache? cache = null)
    {
        _log = log;
        if (filePath is not null) _file = new LogFileWriter(filePath, log.LogWarning);
        _traceUnknown = traceUnknown;
        _hexDump = hexDump;
        _logPlayerIds = logPlayerIds;
        _logCards = logCards;
        _names = names;
        _cache = cache;
        _cache?.Load();
        _cacheRoom = cache?.RoomId ?? 0;
        _roster = new Roster(names);
        _roster.Cache = cache;
    }

    private static void Add(List<string> lines, string line)
    {
        if (line.Length > 0) lines.Add(line);
    }

    private static bool IsNumber(int wire) =>
        wire == ProtoReader.WireVarint || wire == ProtoReader.WireFixed32 || wire == ProtoReader.WireFixed64;

    public void OnFrame(FrameHeader header, byte[] body)
    {
        int cmdId = header.CmdId;
        int errId = header.ErrId;

        if (TimingTrace.Enabled)
            TimingTrace.Write($"frame cmd={cmdId} {Op.Name(cmdId)} up={header.UpSn} "
                              + $"down={header.DownSn} err={errId} len={body.Length}");

        if (_goldHold is { } waiting && DateTime.UtcNow - waiting.At > GoldPairWindow) FlushGold();

        if (_deferred is { } deferred && cmdId != Op.MonsterRefresh) FlushDeferred(deferred);

        _group = ++_groupSeq;
        _kind = KindOf(cmdId);
        _units = 0;
        _detail = 0;
        _kindFromCause = cmdId == Op.UpdateHeroAttr;

        if (_finishPending && cmdId is not (Op.UpdateHeroAttr or Op.HeroSkillMoveEffect or Op.LandBuffs))
        {
            _finishPending = false;
            Emit("──────── 게임 종료 ────────");
        }

        if (header.UpSn != 0 && Op.TimingSync.Contains(cmdId)) MirrorSync?.Invoke(cmdId, body.Length);

        if (Op.GameEntry.Contains(cmdId))
        {
            if (errId == 0) BeginGame(Op.Name(cmdId));
            else Diag($"[game] {Op.Name(cmdId)}({cmdId}) err={errId}, ignored");
            return;
        }

        if (!Op.Allowed.Contains(cmdId))
        {
            if (_traceUnknown) Diag($"skip cmd={cmdId,-5} len={body.Length}");
            return;
        }

        if (cmdId != Op.UpdateHeroAttr) _battleGroup = -1;

        // 허용목록 검사 뒤에 있어야 카드 메시지가 덤프되지 않는다.
        if (_hexDump.Contains(cmdId))
            Diag($"dump {Op.Name(cmdId)}({cmdId}) len={body.Length} {Hex(body, 96)}");

        switch (cmdId)
        {
            case Op.StartGame:
                if (errId != 0)
                {
                    Diag($"[game] StartGame(5020) err={errId}, ignored");
                    break;
                }
                // StartGameS2C가 이번 판 Room을 싣고 오므로 먼저 비우고 읽는다.
                BeginGame("StartGame");
                goto case Op.RunningGame;
            case Op.RunningGame:
                if (errId != 0)
                {
                    Diag($"[game] RunningGame(1003) err={errId}, ignored");
                    break;
                }
                TrackRoom(Roster.ReadRoomId(body));
                _lands.Update(body);
                int printed = 0;
                if (_roster.Update(body))
                    foreach (long id in _roster.LastChanged)
                    {
                        if (!_roster.HasHero(id)) continue;
                        string skills = _roster.Skills(id);
                        Emit($"· 참가자 {_roster.Describe(id, _logPlayerIds)}"
                             + (skills.Length > 0 ? $"  [{skills}]" : ""));
                        printed++;
                    }
                Diag($"[game] {Op.Name(cmdId)}({cmdId}) roster changed={_roster.LastChanged.Count} printed={printed}");
                if (_roster.LastChanged.Count > 0 && _cache is not null)
                {
                    _cache.RoomId = _roomId;
                    _cache.Save();
                }
                break;
            case Op.MonsterRefresh:
                if (_roster.UpdateMonster(body)) _cache?.Save();
                break;
            case Op.BattleUseCard: DecodeBattleUseCard(body); break;
            case Op.UseEffectCard: DecodeUseEffectCard(body); break;
            case Op.SelectRelic: DecodeSelectRelic(body); break;
            case Op.RoundStart: DecodeRoundStart(body); break;
            case Op.GameRoundChange:
                _round = (int)ReadNumberField(body, 1);
                NewPage();
                break;
            case Op.ActionStartNotify: DecodeActionStart(body); break;
            case Op.Battle: DecodeBattle(body); break;
            case Op.GameFinish:
                Diag("[game] GameFinish(1016)");
                _finishPending = true;
                break;
            case Op.UpdateHeroAttr:
                if (HasUnknownTarget(body))
                {
                    _deferred = (body, _group, _kind, _units, _battleGroup);
                    break;
                }
                DecodeUpdateHeroAttr(new ProtoReader(body, 0, body.Length));
                break;
            case Op.HeroSkillMoveEffect: DecodeSkillMoveEffect(body); break;
            case Op.LandBuffs: DecodeLandBuffs(body); break;
            case Op.ThrowDice: DecodeThrowDice(body); break;
            case Op.MoveAgain: DecodeMoveAgain(body); break;
        }
    }

    private void FlushDeferred((byte[] Body, long Group, LineKind Kind, int Units, long BattleGroup) d)
    {
        _deferred = null;
        (long group, LineKind kind, int units, bool fromCause, long battleGroup) =
            (_group, _kind, _units, _kindFromCause, _battleGroup);
        (_group, _kind, _units, _kindFromCause, _battleGroup) = (d.Group, d.Kind, d.Units, true, d.BattleGroup);
        DecodeUpdateHeroAttr(new ProtoReader(d.Body, 0, d.Body.Length));
        (_group, _kind, _units, _kindFromCause, _battleGroup) = (group, kind, units, fromCause, battleGroup);
    }

    private bool HasUnknownTarget(byte[] body)
    {
        if (_roster.IsEmpty) return false;
        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            if (field == 4 && wire == ProtoReader.WireLength && r.TryReadMessage(out var effect))
            {
                while (effect.NextField(out int f, out int w))
                {
                    if (f == 1 && IsNumber(w))
                    {
                        if (effect.TryReadNumber(w, out long target) && target != 0 && !_roster.Knows(target))
                            return true;
                        break;
                    }
                    if (!effect.Skip(w)) break;
                }
            }
            else if (!r.Skip(wire))
            {
                break;
            }
        }
        return false;
    }

    private void DecodeRoundStart(byte[] body)
    {
        var r = new ProtoReader(body, 0, body.Length);
        long round = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            if (field == 1) round = v;
        }
        _round = (int)round;
        // 필드 5(PlayerId)는 첫 행동자가 아니라 받는 사람 자신이다(docs/protocol-fields.md).
        _turnOwner = 0;
        _saidSkillUse = 0;
        _cardSubmits.Clear();
        NewPage();
    }

    private void Said(long source, long id, long actor)
    {
        _saidSource = source;
        _saidId = id;
        _saidActor = actor;
        _saidAt = DateTime.UtcNow;
    }

    private void DecodeActionStart(byte[] body)
    {
        var r = new ProtoReader(body, 0, body.Length);
        long pid = 0, die = 0, hospital = 0, stop = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            switch (field)
            {
                case 1: pid = v; break;
                case 2: die = v; break;
                case 3: hospital = v; break;
                case 4: stop = v; break;
            }
        }
        _turnOwner = pid;
        Said(-1, 0, 0);
        _saidSkillUse = 0;
        _cardSubmits.Clear();
        var note = new StringBuilder();
        if (die != 0) note.Append(" [기절]");
        if (hospital != 0) note.Append(" [회복]");
        if (stop != 0) note.Append(" [턴중단]");
        // 회복 중인 차례는 게임이 차례 배너 대신 생각 중 팁을 띄운다. 화면 신호 짝짓기에서 뺀다.
        _detail = hospital != 0 ? 1 : 0;
        Emit($"· {_roster.Name(pid)} 행동 시작{note}");
    }

    private void DecodeThrowDice(byte[] body)
    {
        long movePoint = 0, pid = 0;
        var vals = new List<long>();
        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            if (field == 1 && wire == ProtoReader.WireLength)
            {
                if (!r.TryReadMessage(out var packed)) break;
                while (packed.HasMore && packed.TryReadFixed32(out long v)) vals.Add(v);
                continue;
            }
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long n)) break;
            switch (field)
            {
                case 1: vals.Add(n); break;
                case 2: movePoint = n; break;
                case 3: pid = n; break;
            }
        }

        var sb = new StringBuilder($"{RoundTag} {_roster.Name(pid)} 주사위");
        if (vals.Count > 0) sb.Append($" {string.Join("+", vals)}");

        long steps = movePoint;
        if (steps == 0) foreach (long v in vals) steps += v;
        _units = (int)steps;
        // 화면 오브젝트 이름에 눈이 실리는 것은 캐릭터 주사위에서만 확인했다. 여러 개면 눈마다 따로 실린다.
        if (vals.Count is >= 1 and <= 4 && vals.All(v => v is > 0 and < 100) && _roster.IsCharacter(pid))
        {
            int code = 0;
            for (int i = vals.Count - 1; i >= 0; i--) code = code * 100 + (int)vals[i];
            _detail = code;
        }
        if (steps != 0) sb.Append($" → {steps}칸");
        Emit(sb.ToString());
    }

    private void DecodeMoveAgain(byte[] body)
    {
        long pid = 0, movePoint = 0;
        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            if (field == 1) pid = v;
            else if (field == 2) movePoint = v;
        }
        _units = (int)movePoint;
        if (movePoint != 0) Emit($"{RoundTag} {_roster.Name(pid)} 추가 이동 {movePoint}칸");
    }

    // 공개 전에는 cardId가 0으로 오므로 0 이하는 버린다.
    private void DecodeBattleUseCard(byte[] body)
    {
        long pid = 0, cardId = 0, noCard = 0;
        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            switch (field)
            {
                case 1: pid = v; break;
                case 2: cardId = v; break;
                case 3: noCard = v; break;
            }
        }
        // uid는 종류를 알 수 없는 런타임 값이라 진단에만 남긴다.
        if (_traceUnknown) Diag($"battle-card pid={pid} cardUid={cardId} noCard={noCard}");

        if (noCard != 0)
        {
            if (_logCards) Emit($"{RoundTag} {_roster.Name(pid)} 카드 안 냄");
            else AdvanceOverlay(_kind);
            return;
        }
        if (cardId > 0 && _cardSubmits.Add((pid, cardId)))
        {
            if (_logCards) Emit($"{RoundTag} {_roster.Name(pid)} 카드 제출");
            else AdvanceOverlay(_kind);
        }
    }

    private void DecodeUseEffectCard(byte[] body)
    {
        long pid = 0, cardId = 0, skillId = 0, useSkill = 0;
        var targets = new List<long>();
        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            // 필드 3은 packed repeated sfixed64다. 숫자 wire로만 읽으면 통째로 건너뛴다.
            if (field == 3 && wire == ProtoReader.WireLength)
            {
                if (!r.TryReadMessage(out var packed)) break;
                while (packed.HasMore && packed.TryReadFixed64(out long t)) targets.Add(t);
                continue;
            }
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            switch (field)
            {
                case 1: pid = v; break;
                case 2: cardId = v; break;
                case 3: targets.Add(v); break;
                case 8: useSkill = v; break;
                case 9: skillId = v; break;
            }
        }

        if (useSkill != 0)
        {
            // 스킬은 카드가 아니다 — LogCards와 무관하게 남긴다.
            var skill = new StringBuilder($"{RoundTag} {_roster.Name(pid)} 스킬 사용");
            if (_names.Lookup("skill", skillId) is { } name) skill.Append($" \"{name}\"");
            AppendTargets(skill, targets);
            _kind = LineKind.Skill;
            Emit(skill.ToString());
            _saidSkillUse = pid;
            _activeSkillId = skillId;
            _activeSkillCaster = pid;
            _activeSkillAt = DateTime.UtcNow;
            Said(SkillCause, skillId, pid);
            return;
        }

        if (cardId <= 0) return;
        // LogCards가 꺼져 있어도 발표한 것으로 둬야 뒤따르는 머리줄이 카드를 드러내지 않는다.
        Said(CardCause, cardId, pid);
        _saidCardName = _logCards ? Palette.Strip(CardLabel(cardId)) : null;
        if (!_logCards) { AdvanceOverlay(_kind); return; }
        var sb = new StringBuilder($"{RoundTag} {_roster.Name(pid)} 효과카드 {CardLabel(cardId)}");
        AppendTargets(sb, targets);
        Emit(sb.ToString());
    }

    private void DecodeSelectRelic(byte[] body)
    {
        long pid = 0, relicId = 0, reroll = 0;
        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            switch (field)
            {
                case 1: pid = v; break;
                case 2: relicId = v; break;
                case 3: reroll = v; break;
            }
        }
        if (reroll != 0 || relicId == 0) return;

        string text = RelicText(relicId);
        Emit($"{RoundTag} {_roster.Name(pid)} 칩 획득 "
             + (text.Length > 0 ? text : $"#{relicId}"));
        Said(RelicCause, relicId, pid);
    }

    private void AppendTargets(StringBuilder sb, List<long> targets)
    {
        if (targets.Count == 0) return;
        sb.Append(" → ");
        for (int i = 0; i < targets.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(_roster.Name(targets[i]));
        }
    }

    private void DecodeBattle(byte[] body)
    {
        var outer = new ProtoReader(body, 0, body.Length);
        if (!outer.NextField(out int f, out int w) || f != 1 || w != ProtoReader.WireLength) return;
        if (!outer.TryReadMessage(out var b)) return;

        Role attacker = default, defender = default;
        long battleId = 0, isEnd = 0, isPursuit = 0, fightBack = 0, skillPlayer = 0;

        while (b.NextField(out int field, out int wire))
        {
            if (field == 2 && wire == ProtoReader.WireLength && b.TryReadMessage(out var atk))
                attacker = DecodeRole(atk);
            else if (field == 3 && wire == ProtoReader.WireLength && b.TryReadMessage(out var def))
                defender = DecodeRole(def);
            else if (IsNumber(wire))
            {
                if (!b.TryReadNumber(wire, out long v)) return;
                switch (field)
                {
                    case 1: battleId = v; break;
                    case 5: isEnd = v; break;
                    case 7: isPursuit = v; break;
                    case 8: fightBack = v; break;
                    case 9: skillPlayer = v; break;
                }
            }
            else if (!b.Skip(wire)) return;
        }

        if (TimingTrace.Enabled)
            TimingTrace.Write($"battle id={battleId} end={isEnd} "
                              + $"{Palette.Strip(_roster.Name(attacker.PlayerId))} vs "
                              + $"{Palette.Strip(_roster.Name(defender.PlayerId))} pursuit={isPursuit} back={fightBack}");

        if (isEnd == 0)
        {
            if (_traceUnknown) Diag($"battle #{battleId} in progress, len={body.Length}");
            return;
        }

        var tags = new StringBuilder();
        if (fightBack != 0) tags.Append(" [반격]");
        _detail = fightBack != 0 ? 1 : 0;
        if (isPursuit != 0) tags.Append(" [추격]");
        if (skillPlayer != 0) tags.Append($" [스킬 {_roster.Name(skillPlayer)}]");

        Emit($"{RoundTag} PK{tags}  {attacker.Format(_roster, _names, attacking: true)}"
             + $"  vs  {defender.Format(_roster, _names, attacking: false)}");
        Said(BattleCause, 0, attacker.PlayerId);
        _battleGroup = _group;
    }

    private readonly struct Role
    {
        public readonly long PlayerId, Atk, Def, Point, Dodge, HeroId, ChainAttacker, ChainDamage;

        public Role(long playerId, long atk, long def, long point, long dodge,
                    long heroId, long chainAttacker, long chainDamage)
        {
            PlayerId = playerId; Atk = atk; Def = def; Point = point; Dodge = dodge;
            HeroId = heroId; ChainAttacker = chainAttacker; ChainDamage = chainDamage;
        }

        public string Format(Roster roster, NameTable names, bool attacking)
        {
            string who = roster.Name(PlayerId);
            var sb = new StringBuilder(who);
            if (HeroId != 0 && Palette.Strip(who).StartsWith("?", System.StringComparison.Ordinal))
            {
                string? hero = names.Lookup("character", HeroId) ?? names.Lookup("monster", HeroId);
                sb.Append(hero is null ? $"(hero{HeroId})" : $"({hero})");
            }
            // isEnd 프레임의 Atk/Def에는 주사위(Point)가 이미 합산돼 있다.
            sb.Append(attacking
                ? $" {Palette.Attack($"ATK{Atk - Point}")}"
                : $" {Palette.Defense($"DEF{Def - Point}")}");
            if (Point != 0) sb.Append($" DICE{Point}");
            if (Dodge != 0) sb.Append(" [회피]");
            if (ChainDamage != 0) sb.Append($" 연계{ChainDamage}({roster.Name(ChainAttacker)})");
            return sb.ToString();
        }
    }

    private static Role DecodeRole(ProtoReader r)
    {
        long pid = 0, atk = 0, def = 0, point = 0, dodge = 0, heroId = 0, chain = 0, chainDmg = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            switch (field)
            {
                case 1: pid = v; break;
                case 2: atk = v; break;
                case 3: def = v; break;
                case 6: point = v; break;
                case 7: dodge = v; break;
                case 13: heroId = v; break;
                case 15: chain = v; break;
                case 16: chainDmg = v; break;
            }
        }
        return new Role(pid, atk, def, point, dodge, heroId, chain, chainDmg);
    }

    // HeroSkillMoveEffectS2C가 이 메시지를 봉투에 담아 오므로 byte[]가 아니라 ProtoReader를 받는다.
    private void DecodeUpdateHeroAttr(ProtoReader r)
    {
        long actor = 0, causeSource = 0, causeId = 0;
        string cause = "";
        var lines = new List<string>();
        _goldSeen = null;
        _goldSeenMany = false;
        _causeNamesRelic = false;
        _msgActor = 0;
        _skillFired.Clear();
        _causeSkillLine = null;
        _downFold.Clear();
        _msgTargets.Clear();

        while (r.NextField(out int field, out int wire))
        {
            if (field == 1 && IsNumber(wire))
            {
                if (!r.TryReadNumber(wire, out actor)) return;
                _msgActor = actor;
            }
            else if (field == 2 && wire == ProtoReader.WireLength && r.TryReadMessage(out var causeMsg))
            {
                (cause, causeSource, causeId) = DecodeCause(causeMsg);
                _causeNamesRelic = causeSource == RelicCause && RelicText(causeId).Length > 0;
            }
            else if (field == 4 && wire == ProtoReader.WireLength && r.TryReadMessage(out var effect))
            {
                DecodeEffect(effect, lines, causeSource, causeId);
            }
            else if (!r.Skip(wire))
            {
                return;
            }
        }

        if (_kindFromCause)
        {
            _kind = KindOfCause(causeSource);
            if (causeSource == BattleCause && _battleGroup >= 0) _group = _battleGroup;
        }

        if (lines.Count == 0 && _causeSkillLine is null) return;

        long who = actor != 0 ? actor
            : _msgTargets.Count == 0 || _msgTargets.Contains(_turnOwner) ? _turnOwner
            : _msgTargets.Count == 1 ? _msgTargets.First()
            : 0;
        bool alreadySaid = causeSource == _saidSource
                           && (causeSource == BattleCause || (causeId == _saidId && who == _saidActor))
                           && DateTime.UtcNow - _saidAt < SaidWindow;

        string header;
        if (_causeSkillLine is { } skillLine)
        {
            lines.Remove(skillLine);
            header = $"{RoundTag} {skillLine}";
            alreadySaid = false;   // 사건 자체라 문맥 때문에 지우면 안 된다
        }
        else
        {
            string subject = who != 0 ? $" {_roster.Name(who)}" : "";
            header = cause.Length > 0
                ? $"{RoundTag}{subject} ({cause})"
                : $"{RoundTag}{subject}";
        }

        if (!alreadySaid && cause.Length == 0 && _saidSource == CardCause && _saidCardName is { } card
            && DateTime.UtcNow - _saidAt < SaidWindow
            && lines.TrueForAll(l => Palette.Strip(l).Contains(card)))
            alreadySaid = true;

        if (!alreadySaid && lines.Count == 1 && _goldSeen is { } gold)
        {
            if (TryMergeGold(gold, causeSource, causeId)) return;
            FlushGold();
            _goldHold = new GoldHold
            {
                Actor = who, Pid = gold.Pid, Change = gold.Change,
                Ori = gold.Ori, Curr = gold.Curr,
                CauseSource = causeSource, CauseId = causeId, CauseText = cause,
                Header = header, Line = lines[0], At = DateTime.UtcNow,
                Kind = _kind, Group = _group,
            };
            return;
        }

        if (!alreadySaid)
        {
            Emit(header);
            Said(causeSource, causeId, who);
        }
        foreach (var line in lines) Emit("        " + line);
    }

    // 네 조건 중 하나라도 빼면 무관한 두 사람의 수입/지출로 없는 송금을 만들어낸다.
    private bool TryMergeGold((long Pid, long Change, long Ori, long Curr) g,
                              long causeSource, long causeId)
    {
        if (_goldHold is not { } held) return false;
        if (held.CauseSource != causeSource || held.CauseId != causeId) return false;
        if (held.Pid == g.Pid || g.Change == 0 || held.Change != -g.Change) return false;
        if (DateTime.UtcNow - held.At > GoldPairWindow) return false;

        _goldHold = null;

        bool heldPays = held.Change < 0;
        long fromPid = heldPays ? held.Pid : g.Pid;
        long toPid = heldPays ? g.Pid : held.Pid;
        string fromBal = heldPays ? $"{held.Ori}→{held.Curr}" : $"{g.Ori}→{g.Curr}";
        string toBal = heldPays ? $"{g.Ori}→{g.Curr}" : $"{held.Ori}→{held.Curr}";

        var sb = new StringBuilder($"{RoundTag} {_roster.Name(fromPid)} → {_roster.Name(toPid)} ");
        sb.Append(Palette.Gold($"{Math.Abs(g.Change)}골드"));
        if (held.CauseText.Length > 0) sb.Append($" ({held.CauseText})");
        sb.Append($"  [{fromBal}, {toBal}]");
        Emit(sb.ToString());
        Said(causeSource, causeId, fromPid);
        return true;
    }

    private void FlushGold()
    {
        if (_goldHold is not { } held) return;
        _goldHold = null;
        EmitLine(held.Header, held.Kind, held.Group);
        Said(held.CauseSource, held.CauseId, held.Actor);
        EmitLine("        " + held.Line, held.Kind, held.Group);
    }

    private void DecodeSkillMoveEffect(byte[] body)
    {
        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            if (field != 2 || wire != ProtoReader.WireLength) { if (!r.Skip(wire)) return; continue; }
            if (!r.TryReadMessage(out var entry)) return;   // 맵 항목 {1:key, 2:value}

            while (entry.NextField(out int ef, out int ew))
            {
                if (ef != 2 || ew != ProtoReader.WireLength) { if (!entry.Skip(ew)) return; continue; }
                if (!entry.TryReadMessage(out var effect)) return;

                while (effect.NextField(out int xf, out int xw))
                {
                    if (xf == 1 && xw == ProtoReader.WireLength)
                    {
                        if (!effect.TryReadMessage(out var update)) return;
                        DecodeUpdateHeroAttr(update);
                    }
                    else if (!effect.Skip(xw)) return;
                }
            }
        }
    }

    private void DecodeLandBuffs(byte[] body)
    {
        bool added = false;
        long sourceKind = 0;

        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            if (field == 1 && wire == ProtoReader.WireLength)
            {
                if (!r.TryReadMessage(out var wrap)) break;
                if (DecodeLandBuffWrap(wrap, ref sourceKind)) added = true;
            }
            else if (!r.Skip(wire))
            {
                break;
            }
        }

        if (!added) return;
        if (sourceKind == 2 || sourceKind == 3) return;

        long who = _turnOwner;
        if (who == 0 || who == _saidSkillUse) return;
        _saidSkillUse = who;
        Emit($"{RoundTag} {_roster.Name(who)} 스킬 사용");
    }

    private bool DecodeLandBuffWrap(ProtoReader wrap, ref long sourceKind)
    {
        long node = 0;
        bool haveArray = false;
        var seen = new List<BuffInfo>();

        while (wrap.NextField(out int field, out int wire))
        {
            if (field == 1 && IsNumber(wire))
            {
                if (!wrap.TryReadNumber(wire, out node)) return false;
            }
            else if (field == 2 && wire == ProtoReader.WireLength)
            {
                if (!wrap.TryReadMessage(out var array)) return false;
                haveArray = true;
                while (array.NextField(out int af, out int aw))
                {
                    if (af != 1 || aw != ProtoReader.WireLength) { if (!array.Skip(aw)) return false; continue; }
                    if (!array.TryReadMessage(out var entry)) return false;
                    while (entry.NextField(out int ef, out int ew))
                    {
                        if (ef == 2 && ew == ProtoReader.WireLength)
                        {
                            if (!entry.TryReadMessage(out var buff)) return false;
                            seen.Add(ReadBuff(buff));
                        }
                        else if (!entry.Skip(ew))
                        {
                            return false;
                        }
                    }
                }
            }
            else if (!wrap.Skip(wire))
            {
                return false;
            }
        }

        // 필드 2가 없는 wrap은 빈 목록이 아니다. 빈 목록으로 보면 멀쩡한 소환물을 지운다.
        if (node == 0 || !haveArray) return false;

        var now = new HashSet<long>();
        foreach (var b in seen) if (b.Uid != 0) now.Add(b.Uid);

        var gone = new List<long>();
        foreach (var known in _landSummons)
            if (known.Value.Node == node && !now.Contains(known.Key)) gone.Add(known.Key);
        foreach (long uid in gone) _landSummons.Remove(uid);

        bool added = false;
        foreach (var b in seen)
        {
            if (b.Uid == 0 || !_landSummons.TryAdd(b.Uid, (node, b.Id))) continue;
            added = true;
            if (sourceKind == 0) sourceKind = b.SourceKind;
        }
        return added;
    }

    private const long SkillCause = 1;
    private const long CardCause = 2;
    private const long RelicCause = 17;
    private const long LandBuffCause = 8;
    private const long LandCause = 9;
    private const long BattleCause = 12;

    private (string Text, long Source, long Id) DecodeCause(ProtoReader r)
    {
        long s = 0, id = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            if (field == 1) s = v;
            else if (field == 3) id = v;
        }
        (string label, string? table) = CauseSource.Describe(s);
        if (label.Length == 0) return ("", s, id);

        // land의 id는 LandType이 아니라 맵 칸 번호다.
        if (table == "land")
        {
            long? landType = _lands.TypeOf(id);
            if (landType is null) return ($"{label} {id}번 칸", s, id);
            return _names.Lookup("land", landType.Value) is { } landName
                ? ($"{label} \"{landName}\"", s, id)
                : ($"{label} {id}번 칸", s, id);
        }
        if (table is null) return (label, s, id);
        if (_names.Lookup(table, id) is { } name)
        {
            string text = table switch
            {
                "card" => CardText(id, name),
                "event" => Palette.Event($"\"{name}\""),
                "relic" => RelicText(id),
                _ => $"\"{name}\"",
            };
            return ($"{label} {text}", s, id);
        }
        // 이름표에 없는 설정 id는 숫자를 남긴다 — names.tsv를 갱신해야 한다는 신호다.
        return (id != 0 ? $"{label} #{id}" : label, s, id);
    }

    private string RelicText(long id) =>
        _names.Lookup("relic", id) is { } name
            ? Palette.Relic($"\"{name}\"", _names.Lookup("relicgrade", id))
            : "";

    private void DecodeEffect(ProtoReader r, List<string> lines, long causeSource, long causeId)
    {
        long target = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (field == 1 && IsNumber(wire))
            {
                if (!r.TryReadNumber(wire, out target)) return;
                if (target != 0) _msgTargets.Add(target);
            }
            else if (wire == ProtoReader.WireLength && (field is 2 or 3 or 4 or 5 or 6 or 11 || Stacks.ContainsKey(field)))
            {
                if (!r.TryReadMessage(out var sub)) return;
                switch (field)
                {
                    case 2: Add(lines, DecodeGold(sub, target)); break;
                    case 3: Add(lines, DecodeHp(sub, target, causeSource)); break;
                    case 4: Add(lines, DecodeStat(sub, target, 5, attack: true)); break;
                    case 5: Add(lines, DecodeStat(sub, target, 5, attack: false)); break;
                    case 6: DecodeBuff(sub, target, causeSource, causeId, lines); break;
                    case 11: Add(lines, DecodeLevel(sub, target)); break;
                    default:
                        var (buffId, fallback) = Stacks[field];
                        Add(lines, DecodeNum(sub, target, _names.Lookup("buff", buffId) ?? fallback));
                        break;
                }
            }
            // 필드 9(Card)는 손패라 디코더를 두지 않는다.
            else if (!r.Skip(wire))
            {
                return;
            }
        }
    }

    private string DecodeGold(ProtoReader r, long fallbackTarget)
    {
        long pid = fallbackTarget, change = 0, ori = 0, curr = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            switch (field)
            {
                case 1: pid = v; break;
                case 2: change = v; break;
                case 3: ori = v; break;
                case 4: curr = v; break;
            }
        }
        if (change == 0 && ori == curr) return "";

        if (_goldSeen is null && !_goldSeenMany) _goldSeen = (pid, change, ori, curr);
        else { _goldSeen = null; _goldSeenMany = true; }

        return GoldLine(pid, change, ori, curr);
    }

    private string GoldLine(long pid, long change, long ori, long curr) =>
        $"{_roster.Name(pid)} {Palette.Gold($"골드 {ori}→{curr}")}"
        + $"  {Palette.Delta($"{change:+0;-0}", change < 0)}";

    // 증감은 게임이 화면에 띄우는 RealChangeHp를 쓴다 (BattleProperty.OnLifeChanged).
    private string DecodeHp(ProtoReader r, long fallbackTarget, long causeSource)
    {
        long pid = fallbackTarget, change = 0, ori = 0, curr = 0, max = 0, kind = 0, killer = 0;
        long real = 0, realHp = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            switch (field)
            {
                case 1: pid = v; break;
                case 2: change = v; break;
                case 3: ori = v; break;
                case 4: curr = v; break;
                case 5: real = v; break;
                case 6: realHp = v; break;
                case 7: max = v; break;
                case 8: kind = v; break;
                case 9: killer = v; break;
            }
        }

        // 최대 체력에서 회복을 받아도 real이 0이 아니다(실측 10건 전부 +2/+3). 넘친 회복만 버리고
        // 전후가 같은 그 밖의 경우는 정체를 몰라 남긴다.
        string filtered = ori != curr ? ""
            : real == 0 ? "no-change"
            : real > 0 && max > 0 && curr >= max ? "max-heal"
            : "";

        // 걸러낸 메시지야말로 원본 값을 봐야 하므로 진단은 필터보다 먼저 남긴다.
        if (_traceUnknown && (filtered.Length > 0 || ori == curr || change != real
                              || (realHp != 0 && realHp != curr)))
            Diag($"hp filtered={(filtered.Length > 0 ? filtered : "no")} pid={pid} "
                 + $"ori={ori} curr={curr} change={change} real={real} realHp={realHp} "
                 + $"max={max} dmg={kind} killer={killer}");

        if (filtered.Length > 0) return "";

        if (curr <= 0 && ori > 0) _downAt[pid] = DateTime.UtcNow;
        else if (curr > 0) _downAt.Remove(pid);

        // RealChangeHp 없이 HP만 움직였다면 전후 차이가 유일한 사실이다.
        long delta = real != 0 ? real : curr - ori;

        var sb = new StringBuilder($"{_roster.Name(pid)} HP {ori}→{curr}");
        if (max > 0) sb.Append($"/{max}");
        sb.Append($"  {Palette.Delta($"{delta:+0;-0}", delta < 0)}");
        bool sameAsCause = kind == 0 || Array.IndexOf(DamageKind.EquivalentCause(kind), causeSource) >= 0;
        if (!sameAsCause) sb.Append($" [{DamageKind.Name(kind)}]");
        if (killer != 0 && killer != pid && killer != _saidActor)
            sb.Append($"  ← {_roster.Name(killer)}");
        return sb.ToString();
    }

    private string DecodeStat(ProtoReader r, long fallbackTarget, int valueField, bool attack)
    {
        long pid = fallbackTarget, curr = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            if (field == 1) pid = v;
            else if (field == valueField) curr = v;
        }
        string tinted = attack ? Palette.Attack($"ATK → {curr}") : Palette.Defense($"DEF → {curr}");
        return $"{_roster.Name(pid)} {tinted}";
    }

    private void DecodeBuff(ProtoReader r, long fallbackTarget, long causeSource, long causeId,
                            List<string> lines)
    {
        long pid = fallbackTarget, op = 0;
        BuffInfo? single = null;
        var list = new List<BuffInfo>();
        bool haveList = false;

        while (r.NextField(out int field, out int wire))
        {
            if (field == 2 && wire == ProtoReader.WireLength)
            {
                if (!r.TryReadMessage(out var buff)) break;
                single = ReadBuff(buff);
            }
            else if (field == 4 && wire == ProtoReader.WireLength)
            {
                // map 항목 {1:key, 2:value}라 한 겹 벗겨야 한다. 바로 읽으면 BuffId가 0이 된다.
                if (!r.TryReadMessage(out var entry)) break;
                haveList = true;
                long key = 0;
                while (entry.NextField(out int ef, out int ew))
                {
                    if (ef == 2 && ew == ProtoReader.WireLength)
                    {
                        if (!entry.TryReadMessage(out var buff)) break;
                        var info = ReadBuff(buff);
                        list.Add(info.Uid != 0 ? info : info.WithUid(key));
                    }
                    else if (IsNumber(ew))
                    {
                        if (!entry.TryReadNumber(ew, out key)) break;
                    }
                    else if (!entry.Skip(ew)) break;
                }
            }
            else if (IsNumber(wire))
            {
                if (!r.TryReadNumber(wire, out long v)) break;
                if (field == 1) pid = v;
                else if (field == 3) op = v;
            }
            else if (!r.Skip(wire)) break;
        }

        // 줄은 버리되 기억은 한다. 안 그러면 다음 전체 목록 갱신에서 `버프 부여`로 다시 나온다.
        if (causeSource == RelicCause && _causeNamesRelic)
        {
            Remember(pid, single);
            foreach (var b0 in list) Remember(pid, b0);
            return;
        }

        // Oper { 0:Noop, 1:Insert, 2:Delete, 3:Update } — Noop은 필드 4로 온 전체 목록이다.
        if (op == 0 && haveList) { RefreshBuffs(pid, list, causeSource, causeId, lines); return; }

        if (single is not { } b) return;

        // Delete는 uid만 온다.
        if (op == 2)
        {
            if (b.Id == 0 && _buffs.TryGetValue(b.Uid, out var known)) b = known.Info;
            _buffs.Remove(b.Uid);
            AddLose(lines, pid, b, causeSource, causeId);
            return;
        }

        bool isNew = b.Uid == 0 || !_buffs.ContainsKey(b.Uid);
        Remember(pid, b);
        // op 0이 목록 없이 오기도 한다. 그땐 처음 보는 것이면 획득으로 친다.
        Add(lines, BuffLine(pid, b, op == 3 ? BuffEvent.Update
                                  : op == 1 || isNew ? BuffEvent.Gain
                                  : BuffEvent.Update, causeSource, causeId));
    }

    private enum BuffEvent { Gain, Update, Lose }

    private void AddLose(List<string> lines, long pid, BuffInfo b, long causeSource, long causeId)
    {
        string line = BuffLine(pid, b, BuffEvent.Lose, causeSource, causeId);
        if (line.Length == 0) return;
        bool down = _downAt.TryGetValue(pid, out var at) && DateTime.UtcNow - at < DownWindow;
        if (!down || b.SourceKind == RelicOrigin)
        {
            lines.Add(line);
            return;
        }

        if (!_downFold.TryGetValue(pid, out var fold))
        {
            lines.Add(line);
            _downFold[pid] = (lines.Count - 1, 1);
            return;
        }
        _downFold[pid] = (fold.Index, fold.Count + 1);
        lines[fold.Index] = $"버프 해제 {_roster.Name(pid)} {fold.Count + 1}개 (쓰러짐)";
    }

    private void Remember(long pid, BuffInfo? info)
    {
        if (info is { } b && b.Uid != 0 && b.Id != 0) _buffs[b.Uid] = (pid, b);
    }

    // _saidSource로 대신하면 안 된다. 승격된 `스킬 발동` 머리줄도 Said(SkillCause)를 불러
    // 바로 다음 발동이 스스로에게 억제된다.
    private long _activeSkillId;
    private long _activeSkillCaster;
    private DateTime _activeSkillAt;

    private long _msgActor;

    // 실측 메아리는 전부 1ms 안(11건). SaidWindow(2초)를 쓰면 다음 턴 남의 발동까지 삼킨다.
    private static readonly TimeSpan SkillEchoWindow = TimeSpan.FromMilliseconds(250);

    private bool AlreadySaidSkill(long skillId) =>
        _activeSkillId == skillId && skillId != 0
        && (_msgActor == 0 || _msgActor == _activeSkillCaster)
        && DateTime.UtcNow - _activeSkillAt < SkillEchoWindow;

    private bool AlreadySaidRelic(long relicId, long pid) =>
        _saidSource == RelicCause && _saidId == relicId && _saidActor == pid
        && DateTime.UtcNow - _saidAt < SaidWindow;

    private string BuffLine(long pid, BuffInfo b, BuffEvent kind, long causeSource, long causeId)
    {
        if (b.SourceKind == RelicOrigin)
        {
            if (kind == BuffEvent.Update) return "";
            if (kind == BuffEvent.Gain && AlreadySaidRelic(b.SourceId, pid)) return "";

            string verb = kind == BuffEvent.Gain ? "칩 획득" : "칩 잃음";
            string? chip = _names.Lookup("relic", b.SourceId);
            string label = chip is null
                ? ""
                : " " + Palette.Relic($"\"{chip}\"", _names.Lookup("relicgrade", b.SourceId));
            return $"{verb} {_roster.Name(pid)}{label}";
        }

        // 서버가 패시브 발동을 따로 알리지 않으므로 새로 걸린 스킬 출처 버프가 유일한 신호다.
        // 이름은 시전자가 아니라 버프 대상이라 주어 자리에 두지 않는다.
        if (b.SourceKind == SkillOrigin && kind == BuffEvent.Gain
            && _names.Lookup("skill", b.SourceId) is { } skill)
        {
            if (AlreadySaidSkill(b.SourceId)) return "";
            if (!_skillFired.Add((b.SourceId, pid))) return "";

            var line = new StringBuilder($"스킬 발동 \"{skill}\" → {_roster.Name(pid)}");
            if (b.KeepRound != 0) line.Append($" {b.KeepRound}턴");
            string text = line.ToString();

            if (causeSource == SkillCause && causeId == b.SourceId && _causeSkillLine is null)
                _causeSkillLine = text;
            return text;
        }

        var sb = new StringBuilder(kind switch
        {
            BuffEvent.Gain => $"버프 부여 {_roster.Name(pid)}",
            BuffEvent.Lose => $"버프 해제 {_roster.Name(pid)}",
            _ => $"버프 갱신 {_roster.Name(pid)}",
        });
        sb.Append(DescribeBuff(b));
        if (b.KeepRound != 0) sb.Append($" {b.KeepRound}턴");
        return sb.ToString();
    }

    // buff_source.source 값. CauseOrigin.source와 다른 열거형이다 (relic이 여기선 6, 저기선 17).
    private const long SkillOrigin = 1;
    private const long RelicOrigin = 6;

    private void RefreshBuffs(long pid, List<BuffInfo> now, long causeSource, long causeId,
                              List<string> lines)
    {
        var present = new HashSet<long>();
        foreach (var b in now) if (b.Uid != 0) present.Add(b.Uid);

        var gone = new List<BuffInfo>();
        foreach (var known in _buffs)
            if (known.Value.Pid == pid && !present.Contains(known.Key)) gone.Add(known.Value.Info);
        foreach (var b in gone)
        {
            _buffs.Remove(b.Uid);
            AddLose(lines, pid, b, causeSource, causeId);
        }

        foreach (var b in now)
        {
            if (b.Uid == 0 || b.Id == 0) continue;
            bool isNew = !_buffs.ContainsKey(b.Uid);
            _buffs[b.Uid] = (pid, b);
            if (!isNew) continue;
            Add(lines, BuffLine(pid, b, BuffEvent.Gain, causeSource, causeId));
        }
    }

    private readonly struct BuffInfo
    {
        public readonly long Uid, Id, KeepRound, SourceKind, SourceId;

        public BuffInfo(long uid, long id, long keepRound, long srcKind, long srcId)
        {
            Uid = uid; Id = id; KeepRound = keepRound; SourceKind = srcKind; SourceId = srcId;
        }

        public BuffInfo WithUid(long uid) => new(uid, Id, KeepRound, SourceKind, SourceId);
    }

    private static BuffInfo ReadBuff(ProtoReader buff)
    {
        long uid = 0, id = 0, keep = 0, srcKind = 0, srcId = 0;
        while (buff.NextField(out int bf, out int bw))
        {
            if (bf == 50 && bw == ProtoReader.WireLength)
            {
                if (!buff.TryReadMessage(out var src)) break;
                while (src.NextField(out int sf, out int sw))
                {
                    if (!IsNumber(sw)) { if (!src.Skip(sw)) break; continue; }
                    if (!src.TryReadNumber(sw, out long sv)) break;
                    if (sf == 1) srcKind = sv;
                    else if (sf == 2) srcId = sv;
                }
                continue;
            }
            if (!IsNumber(bw)) { if (!buff.Skip(bw)) break; continue; }
            if (!buff.TryReadNumber(bw, out long bv)) break;
            switch (bf)
            {
                case 1: uid = bv; break;
                case 2: id = bv; break;
                case 5: keep = bv; break;
            }
        }
        return new BuffInfo(uid, id, keep, srcKind, srcId);
    }

    private string DecodeNum(ProtoReader r, long fallbackTarget, string label)
    {
        long pid = fallbackTarget, change = 0, ori = 0, curr = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            switch (field)
            {
                case 1: pid = v; break;
                case 2: change = v; break;
                case 3: ori = v; break;
                case 4: curr = v; break;
            }
        }
        return $"{label} {_roster.Name(pid)} {ori}→{curr} ({change:+0;-0;0})";
    }

    private string DecodeLevel(ProtoReader r, long fallbackTarget)
    {
        long pid = fallbackTarget, lv = 0;
        while (r.NextField(out int field, out int wire))
        {
            if (!IsNumber(wire)) { if (!r.Skip(wire)) break; continue; }
            if (!r.TryReadNumber(wire, out long v)) break;
            if (field == 1) pid = v;
            else if (field == 2) lv = v;
        }
        return $"{_roster.Name(pid)} 레벨 업 → Lv{lv}";
    }

    private static long ReadNumberField(byte[] body, int wanted)
    {
        var r = new ProtoReader(body, 0, body.Length);
        while (r.NextField(out int field, out int wire))
        {
            if (field == wanted && IsNumber(wire))
                return r.TryReadNumber(wire, out long v) ? v : 0;
            if (!r.Skip(wire)) break;
        }
        return 0;
    }

    private string DescribeBuff(BuffInfo b)
    {
        if (_names.Lookup("buff", b.Id) is { } name) return $" \"{name}\"";

        (_, string? table) = BuffOrigin.Describe(b.SourceKind);
        if (table is not null && _names.Lookup(table, b.SourceId) is { } from) return $" [{from}]";

        // 이름표에 없는 버프 id는 `출처id * 100 + 일련번호` 꼴이다.
        long guess = b.Id / 100;
        foreach (string t in BuffSourceTables)
            if (_names.Lookup(t, guess) is { } origin) return $" [{origin}]";
        return "";
    }

    private static readonly string[] BuffSourceTables = { "skill", "card", "relic", "event" };

    private string CardLabel(long cardId) =>
        _names.Lookup("card", cardId) is { } n ? CardText(cardId, n) : $"카드 #{cardId}";

    private string CardText(long cardId, string name) =>
        Palette.Card($"\"{name}\"", _names.Lookup("cardtype", cardId));

    private static string Hex(byte[] b, int max)
    {
        int n = Math.Min(b.Length, max);
        var sb = new StringBuilder(n * 2 + 8);
        for (int i = 0; i < n; i++) sb.Append(b[i].ToString("x2"));
        if (n < b.Length) sb.Append("...");
        return sb.ToString();
    }

    private void NewPage()
    {
        FlushGold();
        Diag($"[game] round {_round}");
        try { MirrorNewPage?.Invoke(_round, _group); } catch { }
        WriteFile($"──────── Round {_round} ────────");
    }

    public void LeftGame() => _cache?.Discard();

    private void BeginGame(string why)
    {
        Diag($"[game] new game ({why}); roster/round/state reset");
        _cache?.Discard();
        Reset();
        _roomId = 0;
        MirrorClear?.Invoke();
    }

    private void TrackRoom(long roomId)
    {
        if (roomId == 0)
        {
            Diag("[game] room message without room id");
            return;
        }
        if (_roomId == roomId) return;

        if (_roomId == 0)
        {
            Diag("[game] room adopted for this game");
            if (_cacheRoom != roomId) _cache?.Discard();
        }
        else
        {
            BeginGame("room changed without a start signal");
        }
        _roomId = roomId;
        _cacheRoom = roomId;
    }

    private void Reset()
    {
        _roster.Clear();
        _lands.Clear();
        _landSummons.Clear();
        _buffs.Clear();
        _goldHold = null;   // 지난 판 줄이다. 파일을 비운 뒤에 나오면 안 된다
        _goldSeen = null;
        _goldSeenMany = false;
        _round = 0;
        _turnOwner = 0;
        _saidSkillUse = 0;
        _cardSubmits.Clear();
        _skillFired.Clear();
        _causeSkillLine = null;
        _causeNamesRelic = false;
        _downAt.Clear();
        _downFold.Clear();
        _finishPending = false;
        _msgTargets.Clear();
        _saidCardName = null;
        _deferred = null;
        _saidSource = -1;
        _saidId = 0;
        _saidActor = 0;
        _saidAt = default;
        _activeSkillId = 0;
        _activeSkillCaster = 0;
        _activeSkillAt = default;
        _msgActor = 0;
        _file?.Truncate();
    }

    public void Close() => _file?.Close();

    private void Emit(string line)
    {
        FlushGold();
        EmitLine(line);
    }

    private void EmitLine(string line) => EmitLine(line, _kind, _group, _units);

    private void AdvanceOverlay(LineKind kind)
    {
        try { MirrorAdvance?.Invoke(kind, _group, _units); } catch { }
    }

    private void EmitLine(string line, LineKind kind, long group, int units = 0)
    {
        string plain = Palette.Strip(line);
        try { Mirror?.Invoke(line, kind, group, units, kind == _kind ? _detail : 0); } catch { }
        WriteFile(plain);
    }

    private static LineKind KindOf(int cmdId) => cmdId switch
    {
        Op.RoundStart or Op.GameRoundChange => LineKind.Round,
        Op.ActionStartNotify => LineKind.Turn,
        Op.ThrowDice => LineKind.Dice,
        Op.MoveAgain => LineKind.Move,
        Op.Battle => LineKind.Attack,
        Op.BattleUseCard => LineKind.Submit,
        Op.UseEffectCard => LineKind.Card,
        Op.LandBuffs => LineKind.Skill,
        Op.SelectRelic => LineKind.Relic,
        Op.UpdateHeroAttr => LineKind.Effect,
        _ => LineKind.Immediate,
    };

    // 결과에 행동의 연출 길이를 또 붙이면 같은 연출을 두 번 기다리게 된다.
    private static LineKind KindOfCause(long causeSource) => causeSource switch
    {
        BattleCause => LineKind.Hit,
        LandCause or LandBuffCause => LineKind.Land,
        _ => LineKind.Effect,
    };

    private void Diag(string line) => _log.LogInfo(line);

    private void WriteFile(string plain) => _file?.Append(plain);
}
