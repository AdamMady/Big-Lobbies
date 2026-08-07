using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using BepInEx.Configuration;

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
            AccessTools.Method(typeof(EpicTransport.Server), "CreateServer"),
            null,
            AccessTools.Method(typeof(HouseNetworkManagerPatch), nameof(HouseNetworkManagerPatch.CreateEosServerPostfix)),
            "EpicTransport.Server.CreateServer"
        );
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

            var patchInfo = Harmony.GetPatchInfo(original);
            var prefixCount = patchInfo?.Prefixes?.Count ?? 0;
            var postfixCount = patchInfo?.Postfixes?.Count ?? 0;

            Log.LogInfo(
                "Installed patch for " + patchName + " prefixes=" + prefixCount + " postfixes=" + postfixCount
            );
        }
        catch (Exception ex)
        {
            Log.LogError("Failed to apply the patch for " + patchName + ": " + ex);
        }
    }
}
