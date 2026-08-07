using Mirror;
using System;
using System.Reflection;

namespace Bigger_Walk;

public static class HouseNetworkManagerPatch
{
    public static void OnStartServerPrefix(HouseNetworkManager __instance)
    {
        Plugin.Log.LogInfo("HouseNetworkManager.OnStartServer");
        ApplyMaxConnections(__instance);
    }

    public static void OnStartHostPrefix(HouseNetworkManager __instance)
    {
        Plugin.Log.LogInfo("HouseNetworkManager.OnStartHost");
        ApplyMaxConnections(__instance);
    }

    public static void CreateHostEosLobbyAsyncPrefix(HouseNetworkManager __instance)
    {
        Plugin.Log.LogInfo("HouseNetworkManager.CreateHostEosLobbyAsync");
        ApplyMaxConnections(__instance);
    }

    public static void ApplyMaxConnections(NetworkManager networkManager)
    {
        if (networkManager == null)
        {
            Plugin.Log.LogInfo("Could not set max connections");
            return;
        }

        var previous = networkManager.maxConnections;
        var maxConnections = Plugin.MaxConnections.Value;
        networkManager.maxConnections = maxConnections;
        Plugin.Log.LogInfo("Set NetworkManager.maxConnections " + previous + " to " + networkManager.maxConnections);
    }

    public static void CreateEosServerPostfix(object __result)
    {
        ApplyEosServerMaxConnections(__result, Plugin.MaxConnections.Value);
    }

    private static void ApplyEosServerMaxConnections(object server, int maxConnections)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var maxConnectionsProperty = server?.GetType().GetProperty("maxConnections", flags);
            if (maxConnectionsProperty?.CanWrite == true && maxConnectionsProperty.PropertyType == typeof(int))
            {
                var previous = maxConnectionsProperty.GetValue(server);
                maxConnectionsProperty.SetValue(server, maxConnections);
                Plugin.Log.LogInfo("Set transport server maxConnections " + previous + " to " + maxConnections);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Failed to set EOS server maxConnections: " + ex);
        }
    }
}
