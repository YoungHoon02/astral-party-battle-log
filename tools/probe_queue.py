"""TraceTiming 로그의 대기열 추적 줄과 오버레이 표시 시각을 대조한다.

사용법:
    python tools/probe_queue.py <BepInEx/LogOutput.log> [--expect-queue] [--margin-ms 100]

`[timing] … frame/queue/show/gate` 줄로 메시지마다 처리 구간 [시작, 끝]을 복원하고,
show·게이트 줄이 기준 메시지의 구간과 어긋나는지 본다. 설계는 docs/QUEUE-SYNC.md 참고.

종료 코드: 0 통과(또는 보고만), 1 --expect-queue 기준 위반, 2 판정할 자료가 없음
(queue 줄이 하나도 없거나, --expect-queue인데 규칙 적용 구간이 없음).

--expect-queue가 위반으로 세는 것은 show 줄이 처리 시작보다, 결과 줄이 처리 끝보다 이른 경우뿐이다.
화면 신호 게이트의 밖·판정불가 수는 표로만 보고하고 종료 코드에는 넣지 않는다. 게이트 신호는 1단계
분석에서 사람이 판단하는 항목이기 때문이다. --messages를 주면 메시지별 표를 덧붙인다.
"""
import argparse
import re
import sys
from bisect import bisect_left
from collections import Counter, namedtuple
from dataclasses import dataclass, field

TIMING = re.compile(r"\[timing\] (\d\d):(\d\d):(\d\d)\.(\d{3}) (.*)")
KV = re.compile(r"(\w+)=(\S+)")

RESULT_CMDS = {1040, 1013, 1096, 5044}
OPENER_CMDS = {5022, 1096, 5044}
FROZEN_RULE_SECONDS = 60.0
DAY = 86400.0

Window = namedtuple("Window", "start end")
Frame = namedtuple("Frame", "t cmd name down")
Show = namedtuple("Show", "t kind why ref grp")
Gate = namedtuple("Gate", "t kind by ref grp why")


@dataclass
class Log:
    frames: dict = field(default_factory=dict)
    heads: list = field(default_factory=list)
    empties: list = field(default_factory=list)
    unmatched: list = field(default_factory=list)
    states: list = field(default_factory=list)
    links: dict = field(default_factory=dict)
    shows: list = field(default_factory=list)
    gates: list = field(default_factory=list)
    last_t: float = 0.0
    last_queue_t: float = 0.0

    @property
    def queue_lines(self):
        return (len(self.heads) + len(self.empties) + len(self.unmatched)
                + len(self.states) + len(self.links))


@dataclass
class Report:
    heads: int = 0
    unmatched: Counter = field(default_factory=Counter)
    state_reasons: Counter = field(default_factory=Counter)
    live_ratio: float = None
    frozen_ratio: float = None
    messages: dict = field(default_factory=dict)
    early: dict = field(default_factory=dict)
    gates: dict = field(default_factory=dict)
    result_lag: list = field(default_factory=list)
    queue_end_result: list = field(default_factory=list)
    queue_end_other: dict = field(default_factory=dict)
    span: float = None
    open_items: int = 0
    rows: list = field(default_factory=list)
    no_rule_period: bool = False
    result_early: dict = field(default_factory=dict)
    unknown_end: int = 0
    violations: list = field(default_factory=list)
    expect_queue: bool = False
    margin: float = 0.1


def _int(text):
    try:
        return int(text)
    except (TypeError, ValueError):
        return None


def _parse_queue(log, t, body, kv):
    # 잘린 줄은 필드가 빠져 있으므로 읽을 수 없으면 건너뛴다.
    if body.startswith("queue head "):
        seq, cmd = _int(kv.get("seq")), _int(kv.get("cmd"))
        if seq is None or cmd is None:
            return False
        log.heads.append((t, seq, cmd))
    elif body.startswith("queue empty "):
        done = _int(kv.get("done"))
        if done is None:
            return False
        log.empties.append((t, done))
    elif body.startswith("queue unmatched "):
        cmd = _int(kv.get("cmd"))
        if cmd is None:
            return False
        log.unmatched.append((t, cmd))
    elif body.startswith("queue state="):
        state = kv.get("state")
        if not state:
            return False
        log.states.append((t, state, kv.get("why", "")))
    elif body.startswith("queue link "):
        seq, end = _int(kv.get("seq")), _int(kv.get("end"))
        if seq is None or end is None:
            return False
        log.links[seq] = end
    else:
        return False
    return True


def parse(lines):
    log = Log()
    offset, prev = 0.0, None
    for line in lines:
        m = TIMING.search(line)
        if not m:
            continue
        h, mi, s, milli, body = m.groups()
        t = int(h) * 3600 + int(mi) * 60 + int(s) + int(milli) / 1000
        # 자정을 넘으면 시각이 크게 뒤로 가므로 하루를 더한다. 소폭 역전은 정렬 흔들림으로 보고 둔다.
        if prev is not None and t + offset < prev - 12 * 3600:
            offset += DAY
        t += offset
        prev = t
        log.last_t = max(log.last_t, t)
        kv = dict(KV.findall(body))

        if body.startswith("frame "):
            seq = _int(kv.get("seq"))
            cmd = _int(kv.get("cmd"))
            if seq is not None and cmd is not None:
                name = re.match(r"frame cmd=\d+ (\S+)", body)
                log.frames[seq] = Frame(t, cmd, name.group(1) if name else "", _int(kv.get("down")))
        elif body.startswith("queue "):
            if not _parse_queue(log, t, body, kv):
                continue
            log.last_queue_t = max(log.last_queue_t, t)
        elif body.startswith("show "):
            log.shows.append(Show(t, kv.get("kind", ""), kv.get("why", ""), _int(kv.get("ref")), kv.get("grp")))
        elif body.startswith("gate release "):
            log.gates.append(Gate(t, kv.get("kind", ""), kv.get("by", ""), _int(kv.get("ref")), kv.get("grp"), kv.get("why", "")))
    return log


def windows(log):
    result = {}
    seqs = sorted(set(log.frames) | {seq for _, seq, _ in log.heads})
    events = [(t, "head", seq) for t, seq, _ in log.heads] + [(t, "empty", done) for t, done in log.empties]
    events.sort(key=lambda e: e[0])
    frontier = 0

    def close(lo, hi_exclusive, t):
        for s in seqs[bisect_left(seqs, lo):bisect_left(seqs, hi_exclusive)]:
            start = result[s].start if s in result else t
            result[s] = Window(start, t)

    for t, kind, n in events:
        if kind == "head":
            close(frontier, n, t)
            if n not in result:
                result[n] = Window(t, None)
            frontier = max(frontier, n)
        else:
            close(frontier, n + 1, t)
            frontier = max(frontier, n + 1)
    return result


def _end_ref(log, ref):
    cmd = log.frames[ref].cmd if ref in log.frames else None
    if cmd in OPENER_CMDS:
        return log.links.get(ref)
    return ref


def _end_time(log, wins, ref):
    end_ref = _end_ref(log, ref)
    w = wins.get(end_ref) if end_ref is not None else None
    return None if w is None else w.end


def _rule_periods(log):
    periods = []
    states = log.states
    for i, (t, state, _) in enumerate(states):
        nxt = states[i + 1][0] if i + 1 < len(states) else None
        if state == "live":
            periods.append((t, nxt if nxt is not None else float("inf")))
        elif state == "frozen":
            limit = t + FROZEN_RULE_SECONDS
            periods.append((t, min(limit, nxt) if nxt is not None else limit))
    return periods


def _in_periods(periods, t):
    return any(a <= t <= b for a, b in periods)


def _ratios(log):
    live_starts = [t for t, s, _ in log.states if s == "live"]
    if not live_starts:
        return None, None, None
    span = log.last_queue_t - live_starts[0]
    if span <= 0:
        return None, None, None
    live = frozen = 0.0
    for i, (t, state, _) in enumerate(log.states):
        if t < live_starts[0]:
            continue
        end = log.states[i + 1][0] if i + 1 < len(log.states) else log.last_queue_t
        if state == "live":
            live += end - t
        elif state == "frozen":
            frozen += end - t
    return live / span, frozen / span, span


def evaluate(log, wins, margin_ms=100, expect_queue=False):
    margin = margin_ms / 1000
    r = Report(expect_queue=expect_queue, margin=margin)
    r.heads = len(log.heads)
    for _, cmd in log.unmatched:
        r.unmatched[cmd] += 1
    for _, _, why in log.states:
        r.state_reasons[why] += 1
    r.live_ratio, r.frozen_ratio, r.span = _ratios(log)

    for seq, w in wins.items():
        f = log.frames.get(seq)
        if f is None:
            continue
        entry = r.messages.setdefault(f.cmd, {"count": 0, "wait": [], "dwell": []})
        entry["count"] += 1
        entry["wait"].append(w.start - f.t)
        if w.end is not None:
            entry["dwell"].append(w.end - w.start)
        else:
            r.open_items += 1
        r.rows.append((seq, f.cmd, f.t, w.start, w.end, w.start - f.t))
    r.rows.sort()

    periods = _rule_periods(log) if expect_queue else []
    r.no_rule_period = expect_queue and not periods

    for s in log.shows:
        if s.ref is None or s.ref not in wins:
            continue
        w = wins[s.ref]
        lead = w.start - s.t
        if lead > margin:
            r.early.setdefault(s.kind, []).append(lead)
            if expect_queue and _in_periods(periods, s.t):
                r.violations.append(f"처리 시작보다 {lead:.3f}초 이른 줄 kind={s.kind} ref={s.ref}")

        cmd = log.frames[s.ref].cmd if s.ref in log.frames else None
        is_result = cmd in RESULT_CMDS
        end = _end_time(log, wins, s.ref)
        if end is None:
            if is_result:
                r.unknown_end += 1
            continue
        if s.why == "queue-end":
            delay = s.t - end
            if is_result:
                r.queue_end_result.append(delay)
            else:
                r.queue_end_other.setdefault(f"show {s.kind}", []).append(delay)
        if not is_result:
            continue
        r.result_lag.append(end - s.t)
        if s.t < end:
            r.result_early.setdefault(cmd, []).append(end - s.t)
            if expect_queue and s.t < end - margin and _in_periods(periods, s.t):
                r.violations.append(f"처리 끝보다 {end - s.t:.3f}초 이른 결과 줄 cmd={cmd} ref={s.ref}")

    for g in log.gates:
        if g.why == "queue-end" and g.ref in wins:
            end = _end_time(log, wins, g.ref)
            if end is not None:
                r.queue_end_other.setdefault(f"gate {g.kind}", []).append(g.t - end)
        if g.by != "signal":
            continue
        tally = r.gates.setdefault(g.kind, {"in": 0, "out": 0, "unknown": 0})
        end = _end_time(log, wins, g.ref) if g.ref in wins else None
        if end is None:
            tally["unknown"] += 1
        elif wins[g.ref].start - margin <= g.t <= end + margin:
            tally["in"] += 1
        else:
            tally["out"] += 1
    return r


def _dist(values):
    if not values:
        return "-"
    v = sorted(values)
    return f"n={len(v)} 최소 {v[0]:.2f}s 중앙 {v[len(v) // 2]:.2f}s 최대 {v[-1]:.2f}s"


def _pct(x):
    return "-" if x is None else f"{x * 100:.1f}%"


def render(r, messages=False):
    out = ["== 추적 ==",
           f"맨 앞 변화 {r.heads}회, 어긋남 {sum(r.unmatched.values())}회"
           + (" (cmd별 " + ", ".join(f"{c}:{n}" for c, n in sorted(r.unmatched.items())) + ")" if r.unmatched else ""),
           f"맞음 비율 {_pct(r.live_ratio)}, 멈춤 비율 {_pct(r.frozen_ratio)}"
           + ("" if r.span is None else f" (분모: 첫 live부터 마지막 queue 줄까지 {r.span:.1f}s)"),
           "상태 바뀜 사유: " + (", ".join(f"{w}:{n}" for w, n in sorted(r.state_reasons.items())) or "-"),
           "", "== 메시지마다 (cmd별) =="]
    for cmd, e in sorted(r.messages.items()):
        out.append(f"cmd {cmd} x{e['count']}  대기(시작-도착) {_dist(e['wait'])}  머문 시간 {_dist(e['dwell'])}")
    if not r.messages:
        out.append("-")
    out.append(f"처리가 끝나지 않은 항목(판정 제외): {r.open_items}건")
    if messages:
        out.append("seq  cmd  도착  시작  끝  대기(초)")
        for seq, cmd, arrive, start, end, wait in r.rows:
            out.append(f"{seq} {cmd} {arrive:.3f} {start:.3f} {'-' if end is None else format(end, '.3f')} {wait:.3f}")

    out += ["", f"== 줄마다 (여유 {r.margin * 1000:.0f}ms) =="]
    out.append("처리 시작보다 이른 줄(show): "
               + (", ".join(f"{k} {len(v)}건 최대 {max(v):.2f}s" for k, v in sorted(r.early.items())) or "없음"))
    out.append("화면 신호 게이트 [시작-여유, 끝+여유] 안/밖/판정불가(보고만, 종료 코드에 넣지 않음): "
               + (", ".join(f"{k} {g['in']}/{g['out']}/{g['unknown']}" for k, g in sorted(r.gates.items())) or "없음"))
    out.append(f"결과 줄 (처리 끝 - show): {_dist(r.result_lag)}")
    out.append(f"queue-end 공개 결과 줄 (show - 처리 끝): {_dist(r.queue_end_result)}")
    for k, v in sorted(r.queue_end_other.items()):
        out.append(f"queue-end 공개 {k} 줄 (show - 처리 끝): {_dist(v)}")
    out.append("처리 끝보다 이른 결과 줄: "
               + (", ".join(f"cmd {c} {len(v)}건 최대 {max(v):.2f}s" for c, v in sorted(r.result_early.items())) or "없음"))
    out.append(f"끝 기준 미상(판정 제외): {r.unknown_end}건")

    if r.no_rule_period:
        out += ["", "경고: 규칙 적용 구간(state=live 또는 state=frozen)이 없어 --expect-queue를 판정할 수 없다."]
    elif r.expect_queue:
        out += ["", f"== 규칙 위반 {len(r.violations)}건 =="] + r.violations
    return "\n".join(out)


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("log")
    ap.add_argument("--expect-queue", action="store_true")
    ap.add_argument("--margin-ms", type=int, default=100)
    ap.add_argument("--messages", action="store_true")
    args = ap.parse_args(argv)

    with open(args.log, encoding="utf-8", errors="replace") as f:
        log = parse(f)
    if log.queue_lines == 0:
        print("[timing] queue 줄이 없다. Diagnostics.TraceTiming을 켜고 대기열 추적이 들어간 빌드로 진행한 로그가 필요하다.")
        return 2
    report = evaluate(log, windows(log), args.margin_ms, args.expect_queue)
    print(render(report, args.messages))
    if report.no_rule_period:
        return 2
    return 1 if args.expect_queue and report.violations else 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
