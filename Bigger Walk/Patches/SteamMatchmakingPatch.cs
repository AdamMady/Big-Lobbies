namespace Bigger_Walk;

public static class SteamMatchmakingPatch
{
    public static void CreateLobbyPrefix(ref int cMaxMembers)
    {
        cMaxMembers = MaxConnectionsPatch.ConfiguredMaxConnections;
    }

    public static void SetLobbyMemberLimitPrefix(ref int cMaxMembers)
    {
        cMaxMembers = MaxConnectionsPatch.ConfiguredMaxConnections;
    }
}
