namespace Bigger_Walk;

public static class HouseNetworkManagerPatch
{
    public static void OnStartServerPrefix(HouseNetworkManager __instance)
    {
        MaxConnectionsPatch.ApplyToNetworkManager(__instance);
    }

    public static void OnStartHostPrefix(HouseNetworkManager __instance)
    {
        MaxConnectionsPatch.ApplyToNetworkManager(__instance);
    }

    public static void CreateHostEosLobbyAsyncPrefix(HouseNetworkManager __instance)
    {
        MaxConnectionsPatch.ApplyToNetworkManager(__instance);
    }
}
