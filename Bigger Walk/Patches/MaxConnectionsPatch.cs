using System;
using System.Reflection;
using Mirror;
using Epic.OnlineServices;
using Epic.OnlineServices.Lobby;
using PlayEveryWare.EpicOnlineServices;
using UnityEngine;
using System.Runtime.InteropServices;

namespace Bigger_Walk;

public static class MaxConnectionsPatch
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type lobbyModificationSetMaxMembersOptionsType;
    private static MethodInfo setMaxMembersMethod;
    internal static object ActiveTransportServer;
    private static bool updatingLobby;
    private static string appliedLobby;
    private static int appliedLimit;
    private static float nextLobbyAttempt;
    private static object configuredTransport;
    private static int localLimit;
    private static string updatingLobbyId;
    private static int updatingLimit;
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeUpdateOptions { public int ApiVersion; public IntPtr ModificationHandle; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UpdateCallback(IntPtr info);
    private static readonly UpdateCallback LobbyUpdated = OnLobbyUpdated;
    [DllImport("EOSSDK-Win64-Shipping", CallingConvention = CallingConvention.Cdecl)]
    private static extern void EOS_Lobby_UpdateLobby(IntPtr handle, ref NativeUpdateOptions options, IntPtr clientData, UpdateCallback callback);

    private static void OnLobbyUpdated(IntPtr info)
    {
        try
        {
            var result = (Result)Marshal.ReadInt32(info);
            if (result == Result.Success)
            {
                appliedLobby = updatingLobbyId; appliedLimit = updatingLimit;
                Plugin.Log.LogInfo("[CAPACITY] Active lobby limit=" + appliedLimit);
            }
            else
            {
                nextLobbyAttempt = Time.unscaledTime + 5f;
                Plugin.Log.LogWarning("[CAPACITY] Active lobby update failed: " + result);
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning("[CAPACITY] Lobby update callback failed: " + ex.Message); }
        finally { updatingLobby = false; }
    }

    internal static void ApplyLive()
    {
        if (!NetworkServer.active) { appliedLobby = null; appliedLimit = 0; return; }
        int limit = ConfiguredMaxConnections;
        if (localLimit != limit || !ReferenceEquals(configuredTransport, ActiveTransportServer) || NetworkServer.maxConnections != limit)
        {
            ApplyToNetworkManager(NetworkManager.singleton);
            NetworkServer.maxConnections = limit;
            ApplyToEosServer(ActiveTransportServer);
            configuredTransport = ActiveTransportServer; localLimit = limit;
        }
        var manager = EOSPrivateLobbyManager.Instance;
        string lobbyId = manager?.currentLobbyID;
        if (manager == null || !manager.IsHostingLobby || string.IsNullOrEmpty(lobbyId) || updatingLobby ||
            (appliedLobby == lobbyId && appliedLimit == limit) || Time.unscaledTime < nextLobbyAttempt) return;
        var eos = EOSManager.Instance;
        var api = eos?.GetEOSLobbyInterface();
        var user = eos?.GetProductUserId();
        if (api == null || user == null) return;
        var options = new UpdateLobbyModificationOptions { LobbyId = lobbyId, LocalUserId = user };
        var result = api.UpdateLobbyModification(ref options, out var modification);
        if (result != Result.Success || modification == null)
        {
            nextLobbyAttempt = Time.unscaledTime + 5f;
            Plugin.Log.LogWarning("[CAPACITY] Cannot update active lobby: " + result);
            return;
        }
        try
        {
            var members = new LobbyModificationSetMaxMembersOptions { MaxMembers = (uint)limit };
            result = modification.SetMaxMembers(ref members);
            if (result != Result.Success)
            {
                nextLobbyAttempt = Time.unscaledTime + 5f;
                Plugin.Log.LogWarning("[CAPACITY] Cannot set active lobby limit: " + result);
                return;
            }
            var update = new NativeUpdateOptions { ApiVersion = 1, ModificationHandle = modification.InnerHandle };
            updatingLobbyId = lobbyId; updatingLimit = limit;
            updatingLobby = true;
            EOS_Lobby_UpdateLobby(api.InnerHandle, ref update, IntPtr.Zero, LobbyUpdated);
        }
        catch { updatingLobby = false; nextLobbyAttempt = Time.unscaledTime + 5f; throw; }
        finally { modification.Release(); }
    }

    public static int ConfiguredMaxConnections => Math.Max(1, Plugin.MaxConnections.Value);

    public static uint ConfiguredMaxConnectionsUInt => (uint)ConfiguredMaxConnections;

    public static void ApplyToNetworkManager(NetworkManager networkManager)
    {
        if (networkManager != null)
        {
            networkManager.maxConnections = ConfiguredMaxConnections;
        }
    }

    public static void ApplyToEosServer(object server)
    {
        try
        {
            var maxConnectionsProperty = server?.GetType().GetProperty("maxConnections", InstanceFlags);
            if (maxConnectionsProperty?.CanWrite == true && maxConnectionsProperty.PropertyType == typeof(int))
            {
                maxConnectionsProperty.SetValue(server, ConfiguredMaxConnections);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Failed to set EOS server maxConnections " + ex);
        }
    }

    public static void ApplyToLobbyModification(object lobbyModification)
    {
        if (lobbyModification == null)
        {
            return;
        }

        try
        {
            var optionsType = lobbyModificationSetMaxMembersOptionsType ??= Plugin.ResolveKnownType(
                "Epic.OnlineServices.Lobby.LobbyModificationSetMaxMembersOptions"
            );
            if (optionsType == null)
            {
                Plugin.Log.LogWarning("Failed to set EOS lobby MaxMembers: options type not found");
                return;
            }

            var options = Activator.CreateInstance(optionsType);
            SetNumericMember(options, "MaxMembers", ConfiguredMaxConnections);

            var method = setMaxMembersMethod ??= lobbyModification.GetType().GetMethod("SetMaxMembers", InstanceFlags);
            if (method == null)
            {
                Plugin.Log.LogWarning("Failed to set EOS lobby MaxMembers SetMaxMembers method not found");
                return;
            }

            var result = method.Invoke(lobbyModification, new[] { options });
            if (result == null || result.ToString() != "Success")
                Plugin.Log.LogError("[CAPACITY] EOS SetMaxMembers(" + ConfiguredMaxConnections + ") failed: " + result);
        }
        catch (TargetInvocationException ex)
        {
            Plugin.Log.LogWarning(
                "Failed to set EOS lobby MaxMembers " +
                (ex.InnerException == null ? ex.ToString() : ex.InnerException.ToString())
            );
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Failed to set EOS lobby MaxMembers " + ex);
        }
    }

    private static void SetNumericMember(object target, string memberName, int value)
    {
        var type = target.GetType();
        var field = type.GetField("_" + memberName + "_k__BackingField", InstanceFlags) ??
                    type.GetField(memberName, InstanceFlags);
        if (field != null)
        {
            field.SetValue(target, ConvertNumber(value, field.FieldType));
            return;
        }

        var property = type.GetProperty(memberName, InstanceFlags);
        if (property?.CanWrite == true && property.GetIndexParameters().Length == 0)
        {
            property.SetValue(target, ConvertNumber(value, property.PropertyType));
        }
    }

    private static object ConvertNumber(int value, Type targetType)
    {
        if (targetType == typeof(uint))
        {
            return (uint)Math.Max(0, value);
        }

        if (targetType == typeof(ushort))
        {
            return (ushort)Math.Max(0, value);
        }

        if (targetType == typeof(byte))
        {
            return (byte)Math.Max(0, value);
        }

        if (targetType == typeof(long))
        {
            return (long)value;
        }

        if (targetType == typeof(ulong))
        {
            return (ulong)Math.Max(0, value);
        }

        return value;
    }
}
