namespace Bigger_Walk;

public static class EpicTransportServerPatch
{
    public static void CreateServerPrefix(ref int maxConnections)
    {
        maxConnections = MaxConnectionsPatch.ConfiguredMaxConnections;
    }

    public static void CreateServerPostfix(object __result)
    {
        MaxConnectionsPatch.ActiveTransportServer = __result;
        MaxConnectionsPatch.ApplyToEosServer(__result);
        if (__result is EpicTransport.Server server)
            Plugin.Log.LogInfo("[CAPACITY] Transport player limit=" + server.maxConnections);
    }
}
