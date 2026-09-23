"""ScreenProbe + TraceTiming 로그에서 라운드 전환 화면 신호 후보를 고른다.

사용법:
    python tools/probe_rounds.py <BepInEx/LogOutput.log> [--top 20]

라운드 패킷(RoundStart 1015 / GameRoundChange 1117) 사이 구간마다 정확히 한 번 켜지는
오브젝트 경로를 후보로 보고, 패킷 수신부터 켜지기까지의 지연을 보여 준다.
F10 표시(mark)가 있으면 각 후보의 켜짐과 가장 가까운 표시의 차이도 함께 낸다.
"""
import argparse
import re
from collections import defaultdict
from datetime import datetime

TIMING = re.compile(r"\[timing\] (\d\d:\d\d:\d\d\.\d{3}) (.*)")
PROBE = re.compile(r"\[probe\] (\d\d:\d\d:\d\d\.\d{3}) f=\d+ active=([01]) path=(.*)")
ROUND_CMDS = {"1015", "1117"}


def ms(stamp):
    t = datetime.strptime(stamp, "%H:%M:%S.%f")
    return ((t.hour * 60 + t.minute) * 60 + t.second) * 1000 + t.microsecond // 1000


def parse(path):
    rounds, marks, ons = [], [], defaultdict(list)
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            if m := PROBE.search(line):
                if m.group(2) == "1":
                    ons[m.group(3).strip()].append(ms(m.group(1)))
            elif m := TIMING.search(line):
                body = m.group(2)
                cmd = re.match(r"frame cmd=(\d+)", body)
                if cmd and cmd.group(1) in ROUND_CMDS:
                    # 한 전환에 두 패킷이 붙어 오면 하나로 본다.
                    at = ms(m.group(1))
                    if not rounds or at - rounds[-1] > 2000:
                        rounds.append(at)
                elif body.startswith("mark"):
                    marks.append(ms(m.group(1)))
    return rounds, marks, ons


def score(rounds, times):
    bounds = rounds + [float("inf")]
    delays, exact = [], 0
    for i, start in enumerate(rounds):
        inside = [t for t in times if start <= t < bounds[i + 1]]
        if len(inside) == 1:
            exact += 1
        delays.append(inside[0] - start if inside else None)
    return exact, delays


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("log")
    ap.add_argument("--top", type=int, default=20)
    args = ap.parse_args()

    rounds, marks, ons = parse(args.log)
    if len(rounds) < 2:
        print(f"라운드 패킷이 {len(rounds)}개뿐이다. TraceTiming을 켜고 2라운드 이상 진행한 로그가 필요하다.")
        return
    print(f"라운드 패킷 {len(rounds)}개, F10 표시 {len(marks)}개, 켜짐 기록 경로 {len(ons)}개\n")

    ranked = []
    for path, times in ons.items():
        before = sum(1 for t in times if t < rounds[0])
        exact, delays = score(rounds, times)
        # 판 전체에서 켜짐 수가 라운드 수와 비슷해야 한다. 자주 켜지는 UI는 뺀다.
        if exact == 0 or len(times) - before > 2 * len(rounds):
            continue
        ranked.append((-exact, len(times), path, delays))
    ranked.sort()

    for neg_exact, total, path, delays in ranked[: args.top]:
        shown = " ".join("-" if d is None else f"{d / 1000:.1f}s" for d in delays)
        line = f"[{-neg_exact}/{len(rounds)} 구간 1회, 총 {total}회] {path}\n    지연: {shown}"
        if marks:
            gaps = []
            for t in ons[path]:
                near = min(marks, key=lambda m: abs(m - t))
                if abs(near - t) < 10_000:
                    gaps.append(f"{(t - near) / 1000:+.1f}s")
            if gaps:
                line += f"\n    F10 대비: {' '.join(gaps)}"
        print(line)


if __name__ == "__main__":
    main()
