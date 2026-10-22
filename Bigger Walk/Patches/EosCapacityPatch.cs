using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using Epic.OnlineServices;
using Epic.OnlineServices.Lobby;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Mirror;

namespace Bigger_Walk;

internal static class EosCapacityPatch
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeCreate(IntPtr lobby, IntPtr options, IntPtr clientData, IntPtr completion, IntPtr method);

    private static INativeDetour createHook;
    private static NativeCreate originalCreate;
    private static readonly NativeCreate createCallback = Create;

    internal static void Install(Harmony harmony)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("EOS capacity hook requires Windows x64.");
        RuntimeHelpers.RunClassConstructor(typeof(CreateLobbyOptions).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(LobbyInterface).TypeHandle);
        var capacityField = IL2CPP.GetIl2CppField(Il2CppClassPointerStore<CreateLobbyOptions>.NativeClassPtr, "<MaxLobbyMembers>k__BackingField");
        if (IL2CPP.il2cpp_field_get_offset(capacityField) - 2 * IntPtr.Size != 8)
            throw new InvalidOperationException("EOS lobby creation options layout changed.");
        var field = typeof(LobbyInterface).GetField("NativeMethodInfoPtr_CreateLobby_Public_Void_byref_CreateLobbyOptions_Object_OnCreateLobbyCallback_0", BindingFlags.NonPublic | BindingFlags.Static);
        if (field == null) throw new MissingFieldException("EOS CreateLobby native method info not found.");
        var method = (IntPtr)field.GetValue(null);
        createHook = INativeDetour.CreateAndApply(Marshal.ReadIntPtr(method), createCallback, out originalCreate);
        harmony.Patch(AccessTools.Method(typeof(LobbyModification), nameof(LobbyModification.SetMaxMembers)),
            prefix: new HarmonyMethod(typeof(EosCapacityPatch), nameof(MaxMembersPrefix)),
            postfix: new HarmonyMethod(typeof(EosCapacityPatch), nameof(MaxMembersPostfix)));
        Plugin.Log.LogInfo("[CAPACITY] EOS SDK creation/update hooks installed; configured player limit=" + MaxConnectionsPatch.ConfiguredMaxConnections);
    }

    // CreateLobbyOptions is a non-blittable native struct passed by address. Harmony's
    // ref wrapper treats its first field as an object pointer, so edit the native field.
    private static void Create(IntPtr lobby, IntPtr options, IntPtr clientData, IntPtr completion, IntPtr method)
    {
        try
        {
            if (NetworkServer.active && options != IntPtr.Zero)
            {
                int previous = Marshal.ReadInt32(options, 8);
                int capacity = MaxConnectionsPatch.ConfiguredMaxConnections;
                Marshal.WriteInt32(options, 8, capacity);
                Plugin.Log.LogInfo("[CAPACITY] EOS CreateLobby MaxLobbyMembers " + previous + " -> " + capacity);
            }
        }
        catch (Exception ex) { Plugin.Log.LogError("[CAPACITY] Cannot update lobby creation options: " + ex); }
        originalCreate(lobby, options, clientData, completion, method);
    }

    private static void MaxMembersPrefix(ref LobbyModificationSetMaxMembersOptions options)
    {
        if (NetworkServer.active)
            options.MaxMembers = MaxConnectionsPatch.ConfiguredMaxConnectionsUInt;
    }

    private static void MaxMembersPostfix(Result __result)
    {
        if (NetworkServer.active && __result != Result.Success)
            Plugin.Log.LogError("[CAPACITY] EOS SetMaxMembers rejected: " + __result);
    }
}
