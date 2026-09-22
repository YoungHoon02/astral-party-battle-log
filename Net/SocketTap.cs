using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSocket = Il2CppSystem.Net.Sockets.Socket;

namespace AstralPartyBattleLog.Net;

// USocket.asycRead가 항상 true라 수신 경로는 BeginReceive → EndReceive뿐이다.
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

    // IL2CPP가 dispose된 소켓 주소를 재사용하므로, 남겨두면 재접속이 반쪽 프레임을 물려받는다.
    internal static void DropSocket(IntPtr socket)
    {
        lock (Gate)
        {
            Outstanding.Remove(socket);
            Streams.Remove(socket);
        }
    }

    internal static void NoteEndReceive(IntPtr socket, int received)
    {
        Pending pending;
        FrameReassembler stream;

        lock (Gate)
        {
            if (!Outstanding.TryGetValue(socket, out pending)) return;
            Outstanding.Remove(socket);

            // 0바이트(FIN)는 예외가 없어 Finalizer를 타지 않으므로 여기서 버린다.
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
                catch { }
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

    // 닫힌 소켓은 EndReceive가 던져 Postfix가 불리지 않는다. 예외는 삼키지 않는다.
    private static void Finalizer(Il2CppSocket __instance, Exception? __exception)
    {
        if (__exception is null) return;
        try { SocketTap.DropSocket(__instance.Pointer); }
        catch { }
    }
}
