using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSocket = Il2CppSystem.Net.Sockets.Socket;

namespace AstralPartyBattleLog.Net;

/// <summary>
/// 게임의 TCP 수신 경로를 읽기 전용으로 엿듣는다.
///
/// <c>Core.Net.USocket</c>은 <c>System.Net.Sockets.Socket</c>을 직접 쓰고, 이 타입은
/// AOT(<c>Il2CppSystem.dll</c>)라 HybridCLR 제약과 무관하게 Harmony가 붙는다.
/// <c>USocket.asycRead</c>가 항상 true라 실제 경로는 BeginReceive → EndReceive.
///
/// 패치는 전부 Postfix이고 게임 상태를 일절 건드리지 않는다.
/// </summary>
internal static class SocketTap
{
    private readonly struct Pending
    {
        public readonly Il2CppStructArray<byte> Buffer;
        public readonly int Offset;
        public Pending(Il2CppStructArray<byte> buffer, int offset)
        {
            Buffer = buffer;
            Offset = offset;
        }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, Pending> Outstanding = new();
    private static readonly Dictionary<IntPtr, FrameReassembler> Streams = new();

    /// <summary>소켓 IO 스레드에서 불린다.</summary>
    public static Action<FrameHeader, byte[]>? OnFrame;

    internal static MethodBase FindBeginReceive() =>
        typeof(Il2CppSocket)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == "BeginReceive"
                         && m.GetParameters().Length == 6
                         && m.GetParameters()[0].ParameterType == typeof(Il2CppStructArray<byte>));

    internal static MethodBase FindEndReceive() =>
        typeof(Il2CppSocket)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == "EndReceive" && m.GetParameters().Length == 1);

    internal static void NoteBeginReceive(IntPtr socket, Il2CppStructArray<byte> buffer, int offset)
    {
        lock (Gate) Outstanding[socket] = new Pending(buffer, offset);
    }

    /// <summary>
    /// 끊긴 소켓의 상태를 버린다. <c>EndReceive</c>가 <c>ObjectDisposedException</c>을 던지면
    /// Postfix가 아예 불리지 않아 재조립기가 남는데, IL2CPP는 dispose된 객체의 주소를
    /// 재사용하므로 <b>재접속이 만든 새 소켓이 죽은 소켓의 반쪽 프레임을 물려받는다.</b>
    /// 그러면 프레임 경계가 어긋나 큰 메시지(방 명단 등)가 통째로 유실된다.
    /// </summary>
    internal static void DropSocket(IntPtr socket)
    {
        lock (Gate)
        {
            Outstanding.Remove(socket);
            Streams.Remove(socket);
        }
    }

    /// <summary>EndReceive가 알려준 바이트 수만큼 위 버퍼에서 꺼내 재조립기에 넣는다.</summary>
    internal static void NoteEndReceive(IntPtr socket, int received)
    {
        Pending pending;
        FrameReassembler stream;

        lock (Gate)
        {
            if (!Outstanding.TryGetValue(socket, out pending)) return;
            Outstanding.Remove(socket);

            // 0바이트는 정상 종료(FIN)다. 예외가 안 나므로 Finalizer도 안 불린다 —
            // 여기서 버리지 않으면 같은 주소를 받은 새 소켓이 이 재조립기를 물려받는다.
            if (received <= 0)
            {
                Streams.Remove(socket);
                return;
            }

            if (!Streams.TryGetValue(socket, out stream!))
            {
                stream = new FrameReassembler();
                Streams[socket] = stream;
            }
        }

        if (stream.Rejected) return;

        var buffer = pending.Buffer;
        if (buffer is null) return;

        // 게임 프로토콜이 아닌 소켓(HTTP 등)도 같은 타입을 쓰므로, 헤더가 말이 안 되면
        // FrameReassembler가 스스로 Rejected로 넘어가 이후 입력을 전부 버린다.
        var managed = new byte[received];
        int max = buffer.Length - pending.Offset;
        if (received > max) return;
        for (int i = 0; i < received; i++) managed[i] = buffer[pending.Offset + i];

        lock (Gate)
        {
            stream.Append(managed, 0, received);
            while (stream.TryDequeue(out FrameHeader header, out byte[] body))
            {
                try { OnFrame?.Invoke(header, body); }
                catch { /* 로깅 실패가 게임을 막으면 안 된다 */ }
            }
        }
    }
}

[HarmonyPatch]
internal static class BeginReceivePatch
{
    private static MethodBase TargetMethod() => SocketTap.FindBeginReceive();

    private static void Postfix(Il2CppSocket __instance, Il2CppStructArray<byte> __0, int __1)
    {
        try { SocketTap.NoteBeginReceive(__instance.Pointer, __0, __1); }
        catch { }
    }
}

[HarmonyPatch]
internal static class EndReceivePatch
{
    private static MethodBase TargetMethod() => SocketTap.FindEndReceive();

    private static void Postfix(Il2CppSocket __instance, int __result)
    {
        try { SocketTap.NoteEndReceive(__instance.Pointer, __result); }
        catch { }
    }

    // 소켓이 닫히면 EndReceive가 던지고 Postfix는 불리지 않는다. 그 소켓의 상태를
    // 여기서 버려야 재접속이 깨끗한 재조립기로 시작한다. 예외는 삼키지 않는다.
    private static void Finalizer(Il2CppSocket __instance, Exception? __exception)
    {
        if (__exception is null) return;
        try { SocketTap.DropSocket(__instance.Pointer); }
        catch { }
    }
}
