using Mirror;

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
        networkManager.maxConnections = Plugin.MAX_CONNECTIONS;
        Plugin.Log.LogInfo("Set maxConnections " + previous + " to " + networkManager.maxConnections
        );
    }
}
