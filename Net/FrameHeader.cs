namespace AstralPartyBattleLog.Net;

/// <summary>
/// 프레임 헤더에서 쓰는 값. 본문을 해석하지 않고도 알 수 있는 것들이다.
///
/// <see cref="UpSn"/>은 <b>우리 클라이언트가 보낸 요청에 대한 응답</b>일 때만 0이 아니다 —
/// 게임도 이 값으로 응답을 찾는다(<c>RPCMsgManager.ReceiveRPCCallStatic</c>). 사용자가 입력할 수
/// 있는 건 화면이 그 단계까지 왔을 때뿐이라, 응답 시각은 화면이 패킷을 따라잡은 시점의 단서가 된다.
/// </summary>
internal readonly struct FrameHeader
{
    public readonly int CmdId;
    public readonly int ErrId;
    public readonly long UpSn;
    public readonly long DownSn;

    public FrameHeader(int cmdId, int errId, long upSn, long downSn)
    {
        CmdId = cmdId;
        ErrId = errId;
        UpSn = upSn;
        DownSn = downSn;
    }
}
