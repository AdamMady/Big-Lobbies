namespace Bigger_Walk;

public static class EosLobbyPatch
{
    public static void CreateLobbyPrefix(ref uint maxConnections)
    {
        maxConnections = MaxConnectionsPatch.ConfiguredMaxConnectionsUInt;
    }
}
