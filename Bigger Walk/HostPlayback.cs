using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Audio;

namespace Bigger_Walk;

// Extend cue capacity only. Native methods retain decoding, filters, spatial audio,
// radio caches and puzzle effects. Never mix two speakers through one channel.
internal static class HostPlayback
{
    sealed class Slot
    {
        internal Bank Bank;
        internal SoundCue Cue;
        internal AudioBus Bus;
        internal AudioMixerGroup Group;
        internal int Index;
        internal bool Used;
    }
    sealed class Bank
    {
        internal AudioMixer Mixer;
        internal readonly List<Slot> Slots = new();
        internal readonly Dictionary<string, float> Globals = new(StringComparer.Ordinal);
        internal void Dispose()
        {
            foreach (var slot in Slots)
            {
                if (slot.Cue != null) UnityEngine.Object.Destroy(slot.Cue);
                if (slot.Group != null) AudioBus.S_AllMixerGroups?.Remove(slot.Group);
                if (slot.Bus != null) UnityEngine.Object.Destroy(slot.Bus);
            }
            if (Mixer != null) UnityEngine.Object.Destroy(Mixer);
        }
    }
    // Shared exposed parameters set by GlobalAudioEffects on VoiceMixer. Never copy
    // per-channel dry/high/reverb levels: each speaker controls its own channel.
    static readonly string[] globals = { "VoicePitch", "Room", "RoomHF", "RoomLF", "DecayTime", "DecayHFRatio", "Reflections", "ReflectDelay", "Reverb", "ReverbDelay", "HFReference", "LFReference", "Diffusion", "Density" };
    static readonly List<Bank> banks = new();
    static readonly Dictionary<IntPtr, Slot> borrowed = new();
    static AudioMixer template;
    static bool failed, closing;
    static int lastFrame = -1, oldRealVoices, reservedRealVoices, oldVirtualVoices, reservedVirtualVoices;
    internal static int Count { get; private set; }
    internal static string Status => failed ? "host overflow unavailable (see log)" : "native host playback: " + Count + ", extra cue banks: " + banks.Count;

    internal static void ReserveVoices()
    {
        closing = false;
        if (borrowed.Count == 0 && banks.Count == 0) failed = false;
        if (reservedRealVoices != 0) return;
        var config = AudioSettings.GetConfiguration();
        oldRealVoices = config.numRealVoices; oldVirtualVoices = config.numVirtualVoices;
        int needed = Math.Clamp(MaxConnectionsPatch.ConfiguredMaxConnections, 1, 128) + 32;
        if (config.numRealVoices >= needed) return;
        config.numRealVoices = needed;
        config.numVirtualVoices = Math.Max(config.numVirtualVoices, needed);
        if (AudioSettings.Reset(config))
        {
            config = AudioSettings.GetConfiguration();
            reservedRealVoices = config.numRealVoices; reservedVirtualVoices = config.numVirtualVoices;
            Plugin.Log.LogInfo("[VOICE] Host Unity audio budget: " + reservedRealVoices + " real voices (includes 32 for world audio).");
            if (reservedRealVoices < needed) Plugin.Log.LogWarning("[VOICE] Unity clamped host audio budget; simultaneous playback capacity needs checking.");
        }
        else Plugin.Log.LogWarning("[VOICE] Unity rejected larger host audio budget.");
    }

    // False leaves vanilla allocation/failure handling intact. Cloned cues must never
    // enter vanilla's static stack and survive into an unmodded/non-hosted session.
    internal static bool TryTake(PlayerVoicePlaybackControl control)
    {
        if (!VoiceRoster.Enabled || control == null || failed || closing || control._cue != null) return false;
        var stack = PlayerVoicePlaybackControl.cueStack;
        if (stack == null || stack.Count > 0) return false;
        if (borrowed.ContainsKey(control.Pointer)) return false;
        try
        {
            Slot free = null;
            foreach (var bank in banks)
            {
                foreach (var slot in bank.Slots) if (!slot.Used) { free = slot; break; }
                if (free != null) break;
            }
            if (free == null)
            {
                int limit = (Math.Clamp(MaxConnectionsPatch.ConfiguredMaxConnections, 1, 128) + VoiceRoster.Slots - 1) / VoiceRoster.Slots;
                if (banks.Count >= limit) return false;
                var bank = CreateBank(); banks.Add(bank); free = bank.Slots[0];
                Plugin.Log.LogInfo("[VOICE] Independent native host cue bank " + banks.Count + " created: " + bank.Slots.Count + " slots. Multiplayer audio validation required.");
            }
            free.Used = true; borrowed.Add(control.Pointer, free);
            control._cue = free.Cue; control._index = free.Index; control._mixer = free.Bank.Mixer;
            control._cueUnusable = false; control._reportedNoCue = false;
            return true;
        }
        catch (Exception error)
        {
            failed = true;
            Plugin.Log.LogError("[VOICE] Cannot extend native host cue capacity; keeping vanilla playback instead of approximating audio: " + error);
            return false;
        }
    }

    static Bank CreateBank()
    {
        var cues = GlobalAudioEffects.Instance?.VoiceCues;
        if (cues == null || cues.Length == 0) throw new InvalidOperationException("Native voice cues missing.");
        var sourceMixer = cues[0]?.Bus?.MixerGroup?.audioMixer;
        if (sourceMixer == null) throw new InvalidOperationException("Native voice mixer missing.");
        if (template != null && template.Pointer != sourceMixer.Pointer) throw new InvalidOperationException("Voice mixer changed while host banks exist.");
        var bank = new Bank();
        try
        {
            bank.Mixer = UnityEngine.Object.Instantiate(sourceMixer);
            if (bank.Mixer == null || bank.Mixer.Pointer == sourceMixer.Pointer) throw new InvalidOperationException("Unity did not clone the voice mixer.");
            bank.Mixer.name = "bigger walk+talk native voice bank " + (banks.Count + 1);
            bank.Mixer.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            bank.Mixer.outputAudioMixerGroup = sourceMixer.outputAudioMixerGroup;
            var indices = new HashSet<int>();
            for (int i = 0; i < cues.Length; i++)
            {
                var cue = cues[i]; var originalBus = cue?.Bus; var originalGroup = originalBus?.MixerGroup;
                // Native TryTakeCue parses the complete numeric group name, not a suffix.
                if (originalGroup == null || originalGroup.audioMixer == null || originalGroup.audioMixer.Pointer != sourceMixer.Pointer ||
                    !int.TryParse(originalGroup.name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int channel))
                    throw new InvalidOperationException("Unexpected native voice group layout: cue=" + cue?.name + ", group=" + originalGroup?.name);
                int index = channel - 1;
                if (index < 0 || index >= PlayerVoicePlaybackControl.PARAM_DRY.Length || !indices.Add(index))
                    throw new InvalidOperationException("Invalid or duplicate native voice channel.");
                AudioMixerGroup group = null;
                foreach (var match in bank.Mixer.FindMatchingGroups(originalGroup.name))
                    if (match.name == originalGroup.name && match.audioMixer.Pointer == bank.Mixer.Pointer && match.Pointer != originalGroup.Pointer) { group = match; break; }
                if (group == null) throw new InvalidOperationException("Mixer clone shares/misses original channel " + originalGroup.name + ".");
                foreach (var parameter in new[] { PlayerVoicePlaybackControl.PARAM_DRY[index], PlayerVoicePlaybackControl.PARAM_HIGH[index], PlayerVoicePlaybackControl.PARAM_REVERB_FALL_WET[index], PlayerVoicePlaybackControl.PARAM_REVERB_BOOST_WET[index] })
                    if (!bank.Mixer.GetFloat(parameter, out _)) throw new InvalidOperationException("Mixer clone lacks parameter " + parameter + ".");
                var slot = new Slot { Bank = bank, Index = index, Group = group }; bank.Slots.Add(slot);
                slot.Bus = UnityEngine.Object.Instantiate(originalBus);
                slot.Bus.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                slot.Bus.MixerGroup = group;
                AudioBus.S_AllMixerGroups?.Add(group);
                slot.Cue = UnityEngine.Object.Instantiate(cue);
                slot.Cue.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                slot.Cue.Bus = slot.Bus;
            }
            template = sourceMixer;
            SyncBank(bank);
            return bank;
        }
        catch { bank.Dispose(); throw; }
    }

    static void SyncBank(Bank bank)
    {
        foreach (var parameter in globals)
        {
            if (!template.GetFloat(parameter, out float value)) continue;
            if (bank.Globals.TryGetValue(parameter, out var previous) && previous == value) continue;
            if (!bank.Mixer.SetFloat(parameter, value)) throw new InvalidOperationException("Cannot sync native voice effect " + parameter + ".");
            bank.Globals[parameter] = value;
        }
    }
    internal static void SyncEffects()
    {
        if (!VoiceRoster.Enabled || template == null) return;
        foreach (var parameter in globals)
        {
            if (!template.GetFloat(parameter, out float value)) continue;
            foreach (var bank in banks)
            {
                if (bank.Globals.TryGetValue(parameter, out var previous) && previous == value) continue;
                if (!bank.Mixer.SetFloat(parameter, value)) throw new InvalidOperationException("Cannot sync native voice effect " + parameter + ".");
                bank.Globals[parameter] = value;
            }
        }
    }

    // Detach custom cues BEFORE native OnDisable pushes its cue back. Original method
    // still owns source/filter/provider cleanup; release the slot only afterwards.
    internal static void BeforeDisable(PlayerVoicePlaybackControl control)
    {
        if (control != null && borrowed.ContainsKey(control.Pointer)) { control._cue = null; control._mixer = null; }
    }
    internal static void AfterDisable(PlayerVoicePlaybackControl control)
    {
        if (control != null && borrowed.Remove(control.Pointer, out var slot)) slot.Used = false;
        if (closing && borrowed.Count == 0) DisposeBanks();
    }
    static void DisposeBanks()
    {
        foreach (var bank in banks) bank.Dispose();
        banks.Clear(); template = null; failed = false;
    }
    internal static void Discard(PlayerNetworking player)
    {
        var controls = PlayerVoicePlaybackControl.controls;
        if (controls == null) return;
        for (int i = 0; i < controls.Count; i++)
        {
            var control = controls[i];
            if (control?.playerCharacter?.playerNetworking?.Pointer == player.Pointer)
                control.dissonanceSampleProvider?.DiscardSession();
        }
    }
    internal static void Tick()
    {
        if (lastFrame == Time.frameCount) return;
        lastFrame = Time.frameCount; Count = 0;
        var controls = PlayerVoicePlaybackControl.controls;
        if (controls == null) return;
        for (int i = 0; i < controls.Count; i++)
        {
            var control = controls[i];
            if (control == null || !control.isActiveAndEnabled) continue;
            if (control._setUp && control._sourceController != null) Count++;
            var provider = control.dissonanceSampleProvider;
            if (provider != null) VoiceRoster.ObserveVoice(control.voicePlayback?.PlayerName, provider.HasActiveSession ? provider.ARV : 0f);
        }
    }
    internal static void Clear()
    {
        closing = true; Count = 0; lastFrame = -1;
        if (borrowed.Count == 0) DisposeBanks();
        if (reservedRealVoices != 0)
        {
            var config = AudioSettings.GetConfiguration();
            if (config.numRealVoices == reservedRealVoices && config.numVirtualVoices == reservedVirtualVoices)
            {
                config.numRealVoices = oldRealVoices; config.numVirtualVoices = oldVirtualVoices;
                AudioSettings.Reset(config);
            }
            reservedRealVoices = reservedVirtualVoices = 0;
        }
    }
}
