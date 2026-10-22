using System;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using BepInEx.Configuration;
using Mirror;
using Steamworks;
using UnityEngine;

namespace Bigger_Walk;

[BepInPlugin("AdamMady.BiggerWalkAndTalk", "Big Lobbies", MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("IceBoxStudio.BigWalk.ModSettingsMenu", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log;
    private Harmony harmony;
    internal static ConfigEntry<int> MaxConnections;
    internal static ConfigEntry<float> QuietThreshold;
    internal static ConfigEntry<string> UpdateCapHz;
    internal static int UpdateCapValue => int.TryParse(UpdateCapHz.Value.Replace(" Hz", ""), out var value) ? value : 0;
    internal static ConfigEntry<float> HearingDistance;
    private static int settingsPending;
    public override void Load()
    {
        Log = base.Log;
        
        MaxConnections = Config.Bind(
            "General",
            "MaxPlayerCount",
            50,
            new ConfigDescription("Maximum players. Applies to the current hosted lobby; lowering this does not kick existing players.", new AcceptableValueRange<int>(1, 100), SettingTags("General", 10, 10, 1d))
        );

        harmony = new Harmony("AdamMady.BiggerWalkAndTalk");
        HarmonyPatches();
        try { EosCapacityPatch.Install(harmony); }
        catch (Exception ex) { Log.LogError("[CAPACITY] EOS SDK hook installation failed: " + ex); }
        QuietThreshold = Config.Bind("Voice", "QuietThreshold", .004f,
            new ConfigDescription("Decoded amplitude below which an ordinary speaker loses priority ONLY when a listener's voice slots are full.", new AcceptableValueRange<float>(.0001f, .05f), SettingTags("Voice", 20, 10, .0001d)));
        UpdateCapHz = Config.Bind("Wi-Fi Optimization", "Update Cap Hz", "Normal",
            new ConfigDescription("Normal uses the game's original rate. Lower caps reduce upload traffic but make movement less smooth. Never exceeds the game's original rate. Voice packet rate is unchanged. Applies live.",
                new AcceptableValueList<string>("5 Hz", "10 Hz", "15 Hz", "20 Hz", "25 Hz", "30 Hz", "Normal"), SettingTags("Wi-Fi Optimization", 30, 10, 5d)));
        HearingDistance = Config.Bind("Wi-Fi Optimization", "Hearing Distance", 30f,
            new ConfigDescription("Ordinary guest voice relay radius in metres. Does not amplify voices or change native host hearing. Active megaphones, walkie-talkies and radio stations keep their special routes. Applies live.", new AcceptableValueRange<float>(1f, 30f), SettingTags("Wi-Fi Optimization", 30, 20, 1d)));
        Config.SettingChanged += OnSettingChanged;
        RegisterSettingsMetadata();
        VoicePatches.Install();
        AddComponent<VoicePump>();
        Log.LogInfo("Big Lobbies loaded. Live settings and native host hearing.");
    }

    private static void RegisterSettingsMetadata()
    {
        var registry = ResolveKnownType("ModSettingsMenu.Api.ModSettingsRegistry");
        var optionsType = ResolveKnownType("ModSettingsMenu.Api.ModSettingsModOptions");
        if (registry == null || optionsType == null) return;
        try
        {
            var options = Activator.CreateInstance(optionsType);
            optionsType.GetProperty("Name").SetValue(options, "Big Lobbies");
            optionsType.GetProperty("Author").SetValue(options, "AdamMady");
            optionsType.GetProperty("Version").SetValue(options, MyPluginInfo.PLUGIN_VERSION);
            registry.GetMethod("Register", new[] { typeof(string), optionsType }).Invoke(null, new[] { (object)"AdamMady.BiggerWalkAndTalk", options });
        }
        catch (Exception ex) { Log.LogWarning("Cannot register Mod Settings metadata: " + ex.GetBaseException().Message); }
    }

    private static void OnSettingChanged(object sender, SettingChangedEventArgs args)
    {
        Interlocked.Exchange(ref settingsPending, 1);
    }

    internal static void ApplyPendingSettings()
    {
        if (Interlocked.Exchange(ref settingsPending, 0) == 0) return;
        VoiceRoster.Refresh(true);
    }

    private static object[] SettingTags(string section, int sectionOrder, int entryOrder, double step)
    {
        // Use the optional menu's real tag type without requiring its DLL to load this plugin.
        var type = ResolveKnownType("ModSettingsMenu.Api.ModSettingsEntryTag");
        if (type == null) return Array.Empty<object>();
        var tag = Activator.CreateInstance(type);
        type.GetProperty("SliderStep").SetValue(tag, step);
        type.GetProperty("Order").SetValue(tag, entryOrder);
        var sectionType = ResolveKnownType("ModSettingsMenu.Api.ModSettingsSectionTag");
        if (sectionType == null) return new[] { tag };
        var sectionTag = Activator.CreateInstance(sectionType);
        sectionType.GetProperty("Section").SetValue(sectionTag, section);
        sectionType.GetProperty("Order").SetValue(sectionTag, sectionOrder);
        return new[] { tag, sectionTag };
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
            MethodByTypeName("EOSPrivateLobbyManager", "FeedUpdateAttributes") ?? MethodByTypeName("EOSLobbyManager", "FeedUpdateAttributes"),
            null,
            AccessTools.Method(typeof(EosLobbyManagerPatch), nameof(EosLobbyManagerPatch.FeedUpdateAttributesPostfix)),
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
