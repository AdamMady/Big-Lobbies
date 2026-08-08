namespace Bigger_Walk;

public static class EpicTransportServerPatch
{
    public static void CreateServerPrefix(ref int maxConnections)
    {
        maxConnections = MaxConnectionsPatch.ConfiguredMaxConnections;
    }

    public static void CreateServerPostfix(object __result)
    {
        MaxConnectionsPatch.ApplyToEosServer(__result);
    }
}
