using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using Dissonance.Integrations.MirrorIgnorance;
using Il2CppInterop.Runtime;
using Mirror;
using Segment = Il2CppSystem.ArraySegment<byte>;

namespace Bigger_Walk;

// Windows x64 passes the eight-byte MirrorConn value directly as its connection pointer.
// This Il2CppInterop Harmony version boxes it as an indirect struct instead, copying
// the NetworkConnection object's class pointer into MirrorConn.Connection.
internal static class VoiceSendHook
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    delegate bool NativeSend(IntPtr server, IntPtr packet, IntPtr connection, byte channel, IntPtr method);

    static INativeDetour hook;
    static NativeSend original;
    static readonly NativeSend callback = Send;

    internal static void Install()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("Voice transport hook requires Windows x64.");
        RuntimeHelpers.RunClassConstructor(typeof(Segment).TypeHandle);
        uint alignment = 0;
        RuntimeHelpers.RunClassConstructor(typeof(MirrorConn).TypeHandle);
        if (IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore<MirrorConn>.NativeClassPtr, ref alignment) != 8 ||
            IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore<Segment>.NativeClassPtr, ref alignment) != 16)
            throw new InvalidOperationException("Voice transport struct layout changed.");
        var field = typeof(MirrorIgnoranceServer).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(f => f.Name.StartsWith("NativeMethodInfoPtr_Send_Private_", StringComparison.Ordinal));
        var method = (IntPtr)field.GetValue(null);
        hook = INativeDetour.CreateAndApply(Marshal.ReadIntPtr(method), callback, out original);
        Plugin.Log.LogInfo("[VOICE] Native send hook installed: direct eight-byte connection ABI.");
    }

    static bool Send(IntPtr server, IntPtr packet, IntPtr connection, byte channel, IntPtr method)
    {
        if (!VoiceRoster.Enabled || VoiceRoster.Sending)
            return original(server, packet, connection, channel, method);
        try
        {
            var data = new Segment(IL2CPP.il2cpp_value_box(Il2CppClassPointerStore<Segment>.NativeClassPtr, packet));
            var destination = new MirrorConn(connection == IntPtr.Zero ? null : new NetworkConnection(connection));
            if (!VoiceRoster.FilterOutgoing(new MirrorIgnoranceServer(server), ref data, destination)) return true;
            bool result = original(server, IL2CPP.il2cpp_object_unbox(data.Pointer), connection, channel, method);
            GC.KeepAlive(data);
            GC.KeepAlive(destination);
            return result;
        }
        catch (Exception error) { VoiceRoster.Report("native outgoing packet", error); return true; }
    }

    internal static void Dispose()
    {
        hook?.Dispose(); hook = null;
    }
}
