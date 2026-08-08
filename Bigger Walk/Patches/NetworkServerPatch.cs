using System;
using Mirror;

namespace Bigger_Walk;

public static class NetworkServerPatch
{
    public static void ListenPrefix(ref int maxConns)
    {
        var maxConnections = MaxConnectionsPatch.ConfiguredMaxConnections;
        maxConns = maxConnections;

        try
        {
            NetworkServer.maxConnections = maxConnections;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Failed to set NetworkServer.maxConnections " + ex);
        }
    }
}
