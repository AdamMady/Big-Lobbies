using Mirror;

namespace Bigger_Walk;

public static class NetworkManagerPatch
{
    public static void StartServerPrefix(NetworkManager __instance)
    {
        MaxConnectionsPatch.ApplyToNetworkManager(__instance);
    }

    public static void StartHostPrefix(NetworkManager __instance)
    {
        MaxConnectionsPatch.ApplyToNetworkManager(__instance);
    }
}
