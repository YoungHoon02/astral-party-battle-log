using System;
using System.Collections.Generic;
using AstralPartyBattleLog.Net;

namespace AstralPartyBattleLog.UI;

// 게임 처리 대기열의 진행으로 줄을 공개한다. 규칙과 근거는 docs/QUEUE-SYNC.md "2단계: 공개 규칙".
internal static partial class OverlaySchedule
{
    // 추적이 이보다 오래 멈추면 멈춘 경계를 버리고 지금 방식(모델·화면 신호·상한)으로 돌아간다.
    private const long QueueFreezeUs = 60_000_000;
    // 끝 경계보다 이만큼 앞의 메시지를 기다리는 줄은 없다(대기 줄 상한과 같은 수).
    private const int MaxLinks = 4096;

    private enum Fit : byte { Take, Skip, Stop }

    // 재생기는 기록된 추적 상태를 넣으려고 바꿔 끼운다(tools/harness/ReplayAdapter.cs).
    private static Func<QueueSnapshot> _readQueue = QueueTracker.Snapshot;
    private static Func<List<(long Opener, long End)>, int> _drainLinks = QueueTracker.DrainLinks;

    private static QueueSnapshot _q;
    private static QueueSnapshot _qSignal;
    private static QueueSnapshot _recordedQ;
    private static readonly Dictionary<long, long> Links = new();
    private static readonly List<(long Opener, long End)> NewLinks = new();
    private static readonly List<long> StaleLinks = new();
    private static long _frozenSinceUs = long.MinValue;
    private static bool _queueActive;

    private static void ResetQueue()
    {
        _q = default;
        _qSignal = default;
        Links.Clear();
        _frozenSinceUs = long.MinValue;
        _queueActive = false;
    }

    // 펌프 첫머리에 한 번 읽어 그 펌프 내내 쓴다. 기록에는 바뀐 값과 새 끝 기준만 남긴다.
    private static void RefreshQueue(long now, bool rec)
    {
        _qSignal = _q;
        _q = _readQueue();
        NewLinks.Clear();
        _drainLinks(NewLinks);
        foreach ((long opener, long end) in NewLinks) Links[opener] = end;
        if (Links.Count > MaxLinks) PruneLinks();

        if (_q.Mode != QueueMode.Frozen) _frozenSinceUs = long.MinValue;
        else if (_frozenSinceUs == long.MinValue) _frozenSinceUs = now;
        _queueActive = _enabled && (_q.Mode == QueueMode.Live
                                    || _q.Mode == QueueMode.Frozen && now - _frozenSinceUs < QueueFreezeUs);

        if (!rec) return;
        if (_q.Mode != _recordedQ.Mode || _q.StartedThrough != _recordedQ.StartedThrough
            || _q.DoneThrough != _recordedQ.DoneThrough)
        {
            RecordQueue(_q);
            _recordedQ = _q;
        }
        foreach ((long opener, long end) in NewLinks) RecordQueueLink(opener, end);
    }

    private static void PruneLinks()
    {
        long floor = _q.DoneThrough - MaxLinks;
        foreach (long opener in Links.Keys)
            if (opener < floor) StaleLinks.Add(opener);
        foreach (long opener in StaleLinks) Links.Remove(opener);
        StaleLinks.Clear();
    }

    private static bool Queued(in Item item) => _queueActive && item.Ref >= 0;

    private static bool Started(long r) => r <= _q.StartedThrough;

    private static bool Ended(long r) => r <= _q.DoneThrough;

    // 걷기 끝 기준이 아직 정해지지 않은 열린 메시지는 끝나지 않은 것으로 본다.
    private static long EndOf(long r, bool opener) =>
        !opener ? r : Links.TryGetValue(r, out long end) ? end : long.MaxValue;

    private static bool EndedOf(in Item item) => Ended(EndOf(item.Ref, item.Opener));

    // 신호 짝짓기 후보(규칙 2). 이번 펌프가 처리하는 신호는 모두 지난 펌프 뒤에 보였으므로, 처리 구간은 지난 펌프의
    // 경계로 판단한다. 이번 펌프의 경계를 쓰면 같은 프레임에서 신호 뒤에 시작한 메시지(약한 창이 닫히자마자
    // 시작한 다음 PK 등)에 짝지어 그 결과를 연출보다 먼저 낸다.
    // 라운드·차례 줄(orCurrent)은 이번 펌프의 경계로 처리 중이어도 받는다. 팁·배너는 그 메시지 처리기가 띄워 처리
    // 시작과 같은 펌프 사이에 켜질 수 있고, 결과를 보여 주는 줄이 아니라 처리 시작에 내도 먼저 뜨지 않는다.
    private static Fit SignalFit(Entry e, bool orCurrent = false)
    {
        if (!_queueActive || e.Item.Ref < 0) return Fit.Take;
        long end = EndOf(e.Item.Ref, e.Item.Opener);
        if (end <= _qSignal.DoneThrough) return Fit.Skip;
        if (e.Item.Ref <= _qSignal.StartedThrough) return Fit.Take;
        return orCurrent && Started(e.Item.Ref) && !Ended(end) ? Fit.Take : Fit.Stop;
    }
}
