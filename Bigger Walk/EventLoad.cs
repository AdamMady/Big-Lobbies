using Mirror;

namespace Bigger_Walk;

internal static class EventLoad
{
    private static bool hosting;
    private static int previousRate, appliedRate;

    internal static void Tick()
    {
        if (!NetworkServer.active)
        {
            if (hosting && NetworkServer.tickRate == appliedRate) NetworkServer.tickRate = previousRate;
            hosting = false;
            return;
        }
        if (!hosting)
        {
            previousRate = NetworkServer.tickRate;
            appliedRate = previousRate;
            hosting = true;
        }
        int cap = Plugin.UpdateCapValue;
        int target = cap == 0 ? previousRate : previousRate > 0 ? System.Math.Min(previousRate, cap) : cap;
        if (appliedRate != target)
        {
            NetworkServer.tickRate = appliedRate = target;
            Plugin.Log.LogInfo("[LOAD] World update rate=" + target + "Hz; game default=" + previousRate + "Hz.");
        }
    }
}
