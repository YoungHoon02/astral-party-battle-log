namespace AstralPartyBattleLog.Net;

// UpSn은 우리 요청에 대한 응답일 때만 0이 아니다 (RPCMsgManager.ReceiveRPCCallStatic).
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
