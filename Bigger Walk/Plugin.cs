using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using BepInEx.Configuration;
using Mirror;
using Steamworks;

namespace Bigger_Walk;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log;
    private Harmony harmony;
    internal static ConfigEntry<int> MaxConnections;
    public override void Load()
    {
        Log = base.Log;
        
        MaxConnections = Config.Bind(
            "General",
            "MaxPlayerCount",
            40,
            "Maximum number of players allowed in the lobby."
        );

        harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        HarmonyPatches();

        Log.LogInfo("Bigger Walk Loaded");
    }

    private void HarmonyPatches()
    {
        TryPatch(
            AccessTools.Method(typeof(NetworkManager), nameof(NetworkManager.StartServer), Type.EmptyTypes),
            AccessTools.Method(typeof(NetworkManagerPatch), nameof(NetworkManagerPatch.StartServerPrefix)),
            null,
            "NetworkManager.StartServer"
        );

        TryPatch(
            AccessTools.Method(typeof(NetworkManager), nameof(NetworkManager.StartHost), Type.EmptyTypes),
            AccessTools.Method(typeof(NetworkManagerPatch), nameof(NetworkManagerPatch.StartHostPrefix)),
            null,
            "NetworkManager.StartHost"
        );

        TryPatch(
            AccessTools.Method(typeof(NetworkServer), nameof(NetworkServer.Listen), new[] { typeof(int) }),
            AccessTools.Method(typeof(NetworkServerPatch), nameof(NetworkServerPatch.ListenPrefix)),
            null,
            "NetworkServer.Listen"
        );

        TryPatch(
            AccessTools.Method(typeof(HouseNetworkManager), nameof(HouseNetworkManager.OnStartServer), Type.EmptyTypes),
            AccessTools.Method(typeof(HouseNetworkManagerPatch), nameof(HouseNetworkManagerPatch.OnStartServerPrefix)),
            null,
            "HouseNetworkManager.OnStartServer"
        );

        TryPatch(
            AccessTools.Method(typeof(HouseNetworkManager), nameof(HouseNetworkManager.OnStartHost), Type.EmptyTypes),
            AccessTools.Method(typeof(HouseNetworkManagerPatch), nameof(HouseNetworkManagerPatch.OnStartHostPrefix)),
            null,
            "HouseNetworkManager.OnStartHost"
        );

        TryPatch(
            AccessTools.Method(typeof(HouseNetworkManager), nameof(HouseNetworkManager.CreateHostEosLobbyAsync), Type.EmptyTypes),
            AccessTools.Method(typeof(HouseNetworkManagerPatch), nameof(HouseNetworkManagerPatch.CreateHostEosLobbyAsyncPrefix)),
            null,
            "HouseNetworkManager.CreateHostEosLobbyAsync"
        );

        TryPatch(
            AccessTools.Method(typeof(EOSLobby), nameof(EOSLobby.CreateLobby)),
            AccessTools.Method(typeof(EosLobbyPatch), nameof(EosLobbyPatch.CreateLobbyPrefix)),
            null,
            "EOSLobby.CreateLobby"
        );

        TryPatch(
            MethodByTypeName("EOSLobbyManager", "FeedUpdateAttributes"),
            AccessTools.Method(typeof(EosLobbyManagerPatch), nameof(EosLobbyManagerPatch.FeedUpdateAttributesPrefix)),
            null,
            "EOSLobbyManager.FeedUpdateAttributes"
        );

        TryPatch(
            AccessTools.Method(typeof(SteamMatchmaking), nameof(SteamMatchmaking.CreateLobby)),
            AccessTools.Method(typeof(SteamMatchmakingPatch), nameof(SteamMatchmakingPatch.CreateLobbyPrefix)),
            null,
            "SteamMatchmaking.CreateLobby"
        );

        TryPatch(
            AccessTools.Method(typeof(SteamMatchmaking), nameof(SteamMatchmaking.SetLobbyMemberLimit)),
            AccessTools.Method(typeof(SteamMatchmakingPatch), nameof(SteamMatchmakingPatch.SetLobbyMemberLimitPrefix)),
            null,
            "SteamMatchmaking.SetLobbyMemberLimit"
        );

        TryPatch(
            AccessTools.Method(typeof(EpicTransport.Server), nameof(EpicTransport.Server.CreateServer)),
            AccessTools.Method(typeof(EpicTransportServerPatch), nameof(EpicTransportServerPatch.CreateServerPrefix)),
            AccessTools.Method(typeof(EpicTransportServerPatch), nameof(EpicTransportServerPatch.CreateServerPostfix)),
            "EpicTransport.Server.CreateServer"
        );
    }

    private static MethodInfo MethodByTypeName(string typeName, string methodName)
    {
        var type = ResolveKnownType(typeName);
        return type == null ? null : AccessTools.Method(type, methodName);
    }

    internal static Type ResolveKnownType(string typeName)
    {
        var type = typeof(EpicTransport.Server).Assembly.GetType(typeName, false, false) ??
                   typeof(HouseNetworkManager).Assembly.GetType(typeName, false, false) ??
                   typeof(NetworkManager).Assembly.GetType(typeName, false, false) ??
                   Type.GetType(typeName, false, false);
        if (type != null)
        {
            return type;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(typeName, false, false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private void TryPatch(MethodInfo original, MethodInfo prefix, MethodInfo postfix, string patchName)
    {
        if (original == null)
        {
            Log.LogError("Failed to find patch target: " + patchName);
            return;
        }

        try
        {
            harmony.Patch(original, 
                prefix == null ? null : new HarmonyMethod(prefix), 
                postfix == null ? null : new HarmonyMethod(postfix)
            );
        }
        catch (Exception ex)
        {
            Log.LogError("Failed to apply the patch for " + patchName + ": " + ex);
        }
    }
}
