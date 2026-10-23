using System;
using Dissonance.Integrations.MirrorIgnorance;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Mirror;
using UnityEngine;
using Segment = Il2CppSystem.ArraySegment<byte>;

namespace Bigger_Walk;

internal static class VoicePatches
{
    internal static bool Ready;
    static Harmony harmony;
    internal static void Install()
    {
        harmony = new Harmony("AdamMady.BiggerWalkAndTalk.Voice");
        try
        {
            VoiceSendHook.Install();
            Patch(typeof(MirrorIgnoranceServer), "OnMessageReceived", nameof(Receive));
            Patch(typeof(MirrorIgnoranceServer), "Connect", null, nameof(Connected));
            Patch(typeof(MirrorIgnoranceServer), "Update", null, nameof(ServerUpdated));
            Patch(typeof(MirrorIgnoranceServer), "Disconnect", nameof(Disconnect));
            Patch(typeof(MirrorIgnoranceCommsNetwork), "PreprocessPacketToServer", nameof(HostSend));
            Patch(typeof(PlayerVoicePlaybackControl), "TryTakeCue", nameof(TakeCue));
            Patch(typeof(PlayerVoicePlaybackControl), "OnDisable", nameof(PlaybackDisabling), nameof(PlaybackDisabled));
            Patch(typeof(GlobalAudioEffects), "Update", null, nameof(AudioEffectsUpdated));
            Patch(typeof(RadioVoiceAssigner), "Peck", nameof(RadioUse), nameof(RadioUsed));
            Patch(typeof(Dissonance.Audio.Codecs.Opus.OpusNative.OpusEncoder), "set_Bitrate", nameof(MicrophoneBitrate));
            ClassInjector.RegisterTypeInIl2Cpp<VoicePump>();
            Ready = true;
        }
        catch (Exception error)
        {
            harmony.UnpatchSelf(); VoiceSendHook.Dispose(); Ready = false;
            Plugin.Log.LogError("[VOICE] Initialization failed; voice patches rolled back. Lobby cap still active. " + error);
            throw; // Do not report this build as voice-ready with missing required hooks.
        }
    }
    static void Patch(Type type, string name, string prefix = null, string postfix = null)
    {
        var target = AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
        HarmonyMethod Hook(string method) => method == null ? null : new HarmonyMethod(typeof(VoicePatches), method);
        harmony.Patch(target, Hook(prefix), Hook(postfix));
    }
    [HarmonyPriority(Priority.Last)]
    static bool Receive(MirrorIgnoranceServer __instance, NetworkConnection source, DissonanceNetworkMessage msg, bool __runOriginal)
    {
        if (!__runOriginal) return true; // An earlier limiter already owns/disposed a rejected message.
        try
        {
            if (!VoiceRoster.Incoming(__instance, source, msg.Data, false)) return true;
            msg.Dispose(); return false;
        }
        catch (Exception error) { VoiceRoster.Report("incoming packet", error); msg.Dispose(); return false; }
    }
    static bool HostSend(Segment packet, ref bool __result)
    {
        if (!VoiceRoster.Enabled || VoiceRoster.Server == null) return true;
        try
        {
            if (!VoiceRoster.Incoming(VoiceRoster.Server, null, packet, true)) return true;
            __result = true; return false;
        }
        catch (Exception error) { VoiceRoster.Report("host packet", error); __result = true; return false; }
    }
    static void Connected(MirrorIgnoranceServer __instance) => VoiceRoster.Bind(__instance);
    static void ServerUpdated(MirrorIgnoranceServer __instance)
    {
        if (!VoiceRoster.Enabled) return;
        try { VoiceRoster.Bind(__instance); VoiceRoster.Tick(); }
        catch (Exception error) { VoiceRoster.Report("server update", error); }
    }
    static void Disconnect() => VoiceRoster.Reset();
    static void MicrophoneBitrate(Dissonance.Audio.Codecs.Opus.OpusNative.OpusEncoder __instance, ref int __0) => RelayQuality.RegisterMicrophone(__instance, ref __0);
    static bool TakeCue(PlayerVoicePlaybackControl __instance, ref bool __result)
    {
        if (!HostPlayback.TryTake(__instance)) return true;
        __result = true; return false;
    }
    static void AudioEffectsUpdated()
    {
        if (!VoiceRoster.Enabled) return;
        try { HostPlayback.SyncEffects(); }
        catch (Exception error) { VoiceRoster.Report("native voice effects", error); }
    }
    static void PlaybackDisabling(PlayerVoicePlaybackControl __instance) => HostPlayback.BeforeDisable(__instance);
    static void PlaybackDisabled(PlayerVoicePlaybackControl __instance) => HostPlayback.AfterDisable(__instance);
    static bool RadioUse(RadioVoiceAssigner __instance, PeckContext context)
    {
        if (replayingRadio) return true;
        try { return VoiceRoster.BeforeUse(__instance, context); }
        catch (Exception error) { VoiceRoster.Report("radio use", error); return true; }
    }
    static bool replayingRadio;
    internal static void ApplyRadioLocally(RadioVoiceAssigner radio, PeckContext context)
    {
        // This is synchronous. Remote retries use a targeted native RPC and never
        // feed back into the host's activation hooks after this guard has expired.
        bool previous = replayingRadio;
        replayingRadio = true;
        try
        {
            if (context.state != 0) radio.isWaitingForMessageEnd = false;
            radio.Peck(context);
        }
        finally { replayingRadio = previous; }
    }
    static void RadioUsed(RadioVoiceAssigner __instance, PeckContext context)
    {
        if (replayingRadio) return;
        if (context != null && __instance.trackedStateSystem != null &&
            VoiceRoster.Enabled)
        {
            try { VoiceRoster.AfterUse(__instance, context); }
            catch (Exception error) { VoiceRoster.Report("radio activation", error); }
        }
    }
}

public sealed class VoicePump : MonoBehaviour
{
    public VoicePump(IntPtr pointer) : base(pointer) { }
    public void Update()
    {
        try { RelayQuality.Tick(); Plugin.ApplyPendingSettings(); MaxConnectionsPatch.ApplyLive(); EventLoad.Tick(); VoiceRoster.Tick(); VoiceRoster.Flush(); }
        catch (Exception error) { VoiceRoster.Report("voice pump", error); }
    }
}
