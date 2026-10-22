namespace Bigger_Walk;

public static class EosLobbyManagerPatch
{
    public static void FeedUpdateAttributesPostfix(object update)
    {
        MaxConnectionsPatch.ApplyToLobbyModification(update);
    }
}
