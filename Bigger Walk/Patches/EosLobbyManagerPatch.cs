namespace Bigger_Walk;

public static class EosLobbyManagerPatch
{
    public static void FeedUpdateAttributesPrefix(object update)
    {
        MaxConnectionsPatch.ApplyToLobbyModification(update);
    }
}
