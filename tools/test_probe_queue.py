import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(__file__))
import probe_queue as pq

PREFIX = "[Info   :Astral Party Battle Log] "


def stamp(sec):
    whole = int(sec) % 86400
    milli = round((sec - int(sec)) * 1000)
    return f"{whole // 3600:02d}:{whole // 60 % 60:02d}:{whole % 60:02d}.{milli:03d}"


def L(sec, text, prefix=PREFIX):
    return f"{prefix}[timing] {stamp(sec)} {text}"


def frame(sec, seq, cmd, grp=None):
    return L(sec, f"frame cmd={cmd} Msg{cmd} up=0 down={seq + 100} err=0 len=10 seq={seq} grp={grp or seq}")


def head(sec, seq, cmd=0):
    return L(sec, f"queue head seq={seq} cmd={cmd} down={seq + 100} size=3 f=1")


def empty(sec, done):
    return L(sec, f"queue empty done={done} f=1")


def state(sec, name, why="x"):
    return L(sec, f"queue state={name} why={why} f=1")


def show(sec, ref, kind="dice", why="model"):
    r = "-" if ref is None else ref
    return L(sec, f"show kind={kind} planned=0ms waited=0ms speed=1 grp=1 ref={r} why={why}")


def gate(sec, kind, by, ref):
    return L(sec, f"gate release kind={kind} by={by} waited=0ms grp=1 ref={ref} why=own-signal")


def run(lines, margin_ms=100, expect=False):
    log = pq.parse(lines)
    wins = pq.windows(log)
    return log, wins, pq.evaluate(log, wins, margin_ms, expect)


BASE = [frame(9, 1, 1040), frame(9, 2, 5028), frame(9, 3, 1040),
        head(10.0, 1), head(12.0, 3), empty(15.0, 3)]


def test_pq1_prefix_and_midnight():
    bare = "[timing] 23:59:59.500 queue head seq=1 cmd=1 down=1 size=1 f=1"
    prefixed = PREFIX + "[timing] 00:00:01.000 queue head seq=2 cmd=1 down=2 size=1 f=1"
    log = pq.parse([bare, "noise", prefixed])
    assert [t for t, _, _ in log.heads] == [pytest.approx(86399.5), pytest.approx(86401.0)]


def test_window_spanning_midnight():
    lines = [frame(86398, 1, 1040), head(86399.0, 1), head(86402.0, 2), empty(86403.0, 2)]
    log, wins, r = run(lines)
    assert wins[1].end - wins[1].start == pytest.approx(3.0)
    assert wins[2].end - wins[2].start == pytest.approx(1.0)
    assert r.messages[1040]["wait"] == [pytest.approx(1.0)]


def test_pq2_windows():
    _, wins, _ = run(BASE)
    assert wins[1] == (10.0, 12.0)
    assert wins[2] == (12.0, 12.0)
    assert wins[3] == (12.0, 15.0)


def test_pq3_early_lines():
    _, _, r = run(BASE + [show(11.5, 3)])
    assert len(r.early["dice"]) == 1
    assert r.early["dice"][0] == pytest.approx(0.5)
    _, _, r = run(BASE + [show(11.95, 3)])
    assert r.early == {}


def test_pq4_gate_signal_and_opener_link():
    _, _, r = run(BASE + [gate(11.0, "pk", "signal", 1)])
    assert r.gates["pk"] == {"in": 1, "out": 0, "unknown": 0}

    opener = [frame(9, 1, 5022), frame(9, 2, 5028), frame(9, 3, 1040),
              head(10.0, 1), head(12.0, 3), empty(15.0, 3)]
    link = L(9.5, "queue link seq=1 end=2 f=1")
    _, _, r = run(opener + [link, gate(12.05, "dice", "signal", 1)])
    assert r.gates["dice"]["in"] == 1
    _, _, r = run(opener + [link, gate(12.5, "dice", "signal", 1)])
    assert r.gates["dice"]["out"] == 1
    _, _, r = run(opener + [gate(11.0, "dice", "signal", 1)])
    assert r.gates["dice"]["unknown"] == 1


def test_pq5_queue_end_delay():
    _, _, r = run(BASE + [show(15.3, 3, why="queue-end"), show(14.0, 3, why="model")])
    assert r.queue_end_result == [pytest.approx(0.3)]
    assert r.queue_end_other == {}
    assert sorted(r.result_lag) == [pytest.approx(-0.3), pytest.approx(1.0)]


def test_pq6_ratios():
    lines = BASE + [state(10.0, "live"), state(20.0, "frozen"), state(30.0, "live"),
                          L(40.0, "queue unmatched cmd=1 down=1 size=1 f=1"), L(90.0, "mark")]
    _, _, r = run(lines)
    assert r.live_ratio == pytest.approx(20 / 30)
    assert r.frozen_ratio == pytest.approx(10 / 30)
    assert r.span == pytest.approx(30.0)
    assert "분모" in pq.render(r)


def test_pq7_expect_queue_violations():
    live = [state(9.0, "live")]
    early = run(BASE + live + [show(11.0, 2)], expect=True)[2].violations
    assert len(early) == 1 and "ref=2" in early[0]
    assert not run(BASE + live + [show(11.95, 2)], expect=True)[2].violations
    late = run(BASE + live + [show(14.0, 3, kind="result")], expect=True)[2].violations
    assert len(late) == 1 and "ref=3" in late[0] and "cmd=1040" in late[0]
    assert not run(BASE + live + [show(14.95, 3, kind="result")], expect=True)[2].violations

    outside = [state(9.0, "frozen")]
    assert run(BASE + outside + [show(11.0, 2)], expect=True)[2].violations
    assert not run(BASE + [state(9.0, "off"), show(11.0, 2)], expect=True)[2].violations
    stale = [frame(9, 1, 1040), head(100.0, 1), state(10.0, "frozen")]
    assert not run(stale + [show(95.0, 1)], expect=True)[2].violations


def test_pq8_exit_code_without_queue_lines(tmp_path):
    path = tmp_path / "LogOutput.log"
    path.write_text("\n".join([frame(9, 1, 1040), show(10.0, 1)]), encoding="utf-8")
    assert pq.main([str(path)]) == 2


def test_main_exit_codes(tmp_path):
    path = tmp_path / "LogOutput.log"
    path.write_text("\n".join(BASE + [state(9.0, "live"), show(11.0, 3)]), encoding="utf-8")
    assert pq.main([str(path)]) == 0
    assert pq.main([str(path), "--expect-queue"]) == 1


def test_pq9_early_results_by_cmd():
    lines = [frame(9, 1, 1040), frame(9, 2, 1013), frame(9, 3, 1096), frame(9, 4, 1040),
             head(10.0, 1), head(11.0, 2), head(12.0, 3), head(13.0, 4), empty(14.0, 4),
             L(9.5, "queue link seq=3 end=4 f=1"),
             show(10.5, 1), show(10.0, 1), show(10.9, 2), show(12.5, 3), show(14.5, 4)]
    _, _, r = run(lines)
    assert {c: len(v) for c, v in r.result_early.items()} == {1040: 2, 1013: 1, 1096: 1}
    assert max(r.result_early[1040]) == pytest.approx(1.0)
    text = pq.render(r)
    assert "cmd 1040 2건" in text


def test_queue_end_gate_lines_measured_separately():
    gate_line = L(15.2, "gate release kind=pk by=signal waited=0ms grp=1 ref=3 why=queue-end")
    _, _, r = run(BASE + [show(15.1, 2, kind="dice", why="queue-end"), gate_line])
    assert r.queue_end_result == []
    assert r.queue_end_other["gate pk"] == [pytest.approx(0.2)]
    assert r.queue_end_other["show dice"] == [pytest.approx(3.1)]
    text = pq.render(r)
    assert "공개 gate pk" in text and "공개 show dice" in text


def test_signal_gate_without_window_counts_unknown():
    _, _, r = run(BASE + [gate(11.0, "turn", "signal", 99)])
    assert r.gates["turn"] == {"in": 0, "out": 0, "unknown": 1}


def test_expect_queue_without_rule_period_exits_2(tmp_path):
    path = tmp_path / "LogOutput.log"
    path.write_text(chr(10).join(BASE + [show(11.0, 2)]), encoding="utf-8")
    assert pq.main([str(path)]) == 0
    assert pq.main([str(path), "--expect-queue"]) == 2


def test_never_ended_item_reported_not_judged():
    lines = [frame(9, 1, 1040), head(10.0, 1), state(9.0, "live"), show(9.9, 1)]
    _, wins, r = run(lines, expect=True)
    assert wins[1].end is None
    assert r.open_items == 1 and r.unknown_end == 1
    assert [v for v in r.violations if "cmd=" in v] == []


def test_immediate_frame_and_dash_ref_ignored():
    lines = [frame(9, 1, 1040), L(9, "frame cmd=5004 HeartBeat up=0 down=9 err=0 len=1 seq=- grp=2"),
             head(10.0, 1), empty(11.0, 1), state(9.0, "live"), show(5.0, None)]
    log, wins, r = run(lines, expect=True)
    assert list(log.frames) == [1] and list(wins) == [1]
    assert r.violations == [] and r.early == {}


def test_truncated_lines_skipped():
    lines = [L(1, "queue head seq=1"), L(1, "queue head cmd=1"), L(1, "queue unmatched size=1"),
             L(1, "queue link seq=1 end=-"), L(1, "queue empty done="), L(1, "frame cmd= seq=1"),
             head(2.0, 1, 7)]
    log = pq.parse(lines)
    assert len(log.heads) == 1 and not log.links and not log.unmatched and not log.empties
    assert not log.frames


def test_messages_table_flag(tmp_path):
    log, wins, r = run(BASE)
    assert "seq  cmd" not in pq.render(r)
    text = pq.render(r, messages=True)
    assert "seq  cmd" in text and "12.000" in text
