using System;
using System.Collections.Generic;
using System.Threading;

namespace AstralPartyBattleLog.UI;

// 라운드 페이지와 차례 시작 줄을 라운드 팁·차례 배너에 맞춰 공개한다. 근거는 docs/SIGNAL-GATING.md
// "라운드 전환 신호 후보 수집"·"차례 배너 게이트".
internal static partial class OverlaySchedule
{
    // 배너는 게임이 2초 띄운다. 같은 이름의 다른 팁이 함께 켜져 1.90~1.95초 뒤 꺼지므로 오차를 좁게 둔다.
    private const long BannerUs = 2_000_000;
    private const long BannerToleranceUs = 35_000;
    private const int BannerVotes = 2;

    private static readonly Dictionary<IntPtr, long> TopOnUs = new();
    private static readonly Dictionary<IntPtr, int> TopVotes = new();
    private static readonly Dictionary<IntPtr, int> TopOnCount = new();
    private static IntPtr _banner;

    // 오브젝트 포인터는 씬이 바뀌면 재사용될 수 있어 판 세대가 아니라 씬에 묶는다.
    public static void ForgetBanner()
    {
        _touched = true;
        if (_recording) EmitEnv(Env.ForgetBanner);
        TopOnUs.Clear();
        TopVotes.Clear();
        TopOnCount.Clear();
        _banner = IntPtr.Zero;
    }

    // 학습 전 차례 줄은 추정으로 내지만 장부에 올린다. 화면이 밀려 있으면 그 배너가 학습 뒤에 떠서,
    // 장부가 없으면 뒤 차례 줄에 짝지어져 사이의 결과를 먼저 푼다.
    private static Gate TurnGate(Item item, long seq)
    {
        if (_banner != IntPtr.Zero) return Gate.Turn;
        Unconsumed.Add(new Ledger { Gate = Gate.Turn, Seq = seq, ReceivedUs = item.ReceivedUs, ItemId = item.Id });
        return Gate.None;
    }

    // 라운드 팁은 공용 안내 팁이라 다른 안내에도 켜진다. 대기 중인 라운드 페이지가 있을 때만 짝짓는다.
    private static void MatchRound(long atUs)
    {
        Entry? page = null;
        foreach (Entry e in Queue)
            if (e.Gate == Gate.Round && e.Res == Resolution.None && e.Item.ReceivedUs < atUs) page = e;
        if (page is null)
        {
            Stray(atUs);
            GateTrace("stray kind=round");
            return;
        }
        ResolveScreenReached(page, atUs);
    }

    private static void TopTip(PendingSignal s)
    {
        if (s.On)
        {
            TopOnUs[s.Instance] = s.AtUs;
            if (s.Instance == _banner) MatchTurn(s.AtUs);
            else if (_banner == IntPtr.Zero) TopOnCount[s.Instance] = TopOnCount.GetValueOrDefault(s.Instance) + 1;
            return;
        }
        if (!TopOnUs.Remove(s.Instance, out long onUs) || _banner != IntPtr.Zero) return;
        if (!LooksLikeBanner(s.AtUs - onUs)) return;
        int votes = TopVotes[s.Instance] = TopVotes.GetValueOrDefault(s.Instance) + 1;
        if (votes < BannerVotes) return;
        foreach (KeyValuePair<IntPtr, int> other in TopVotes)
            if (other.Key != s.Instance && other.Value >= votes) return;
        LearnBanner(s.Instance);
    }

    // 게임 대기가 배속을 따르는지 확인하지 못해 실시간·배속 보정 길이 둘 다 받는다.
    private static bool LooksLikeBanner(long shownUs)
    {
        float speed = Volatile.Read(ref _speed);
        return Math.Abs(shownUs - BannerUs) <= BannerToleranceUs
               || Math.Abs((long)(shownUs * speed) - BannerUs) <= BannerToleranceUs;
    }

    // 학습 전에 이미 켜진 배너 수만큼 장부 앞쪽의 차례 줄은 화면을 지났다.
    private static void LearnBanner(IntPtr instance)
    {
        _banner = instance;
        int seen = TopOnCount.GetValueOrDefault(instance);
        for (int i = 0; i < Unconsumed.Count && seen > 0;)
        {
            if (Unconsumed[i].Gate != Gate.Turn)
            {
                i++;
                continue;
            }
            Unconsumed.RemoveAt(i);
            seen--;
        }
        GateTrace($"banner learned owed={Unconsumed.FindAll(l => l.Gate == Gate.Turn).Count}");
    }

    private static void MatchTurn(long atUs)
    {
        Entry? turn = null;
        foreach (Entry e in Queue)
            if (e.Gate == Gate.Turn && e.Res == Resolution.None)
            {
                if (e.Item.ReceivedUs < atUs) turn = e;
                break;
            }
        Ledger? owed = Unconsumed.Find(l => l.Gate == Gate.Turn && l.ReceivedUs < atUs);
        if (owed is not null && (turn is null || owed.Seq < turn.Seq))
        {
            Unconsumed.Remove(owed);
            Note(Diag.Consume, owed.ItemId);
            GateTrace("consume kind=turn");
            return;
        }
        if (turn is null)
        {
            Stray(atUs);
            GateTrace("stray kind=turn");
            return;
        }
        ResolveScreenReached(turn, atUs);
    }

    // 게임은 앞 연출이 모두 끝난 뒤 라운드 팁·차례 배너를 띄운다. 앞 대기 줄(이동만 한 몬스터 주사위 등)은
    // 늦은 경로로 함께 풀고, PK 창 꺼짐을 놓쳤어도 장벽을 붙잡지 않는다.
    private static void ResolveScreenReached(Entry e, long atUs)
    {
        _barrier = null;
        ResolveOlder(e.Seq, atUs);
        Resolve(e, Resolution.Signal, atUs, Why.OwnSignal);
    }
}
