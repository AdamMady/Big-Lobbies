extern alias GameInterop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dissonance;
using Dissonance.Integrations.MirrorIgnorance;
using Dissonance.Networking;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Mirror;
using UnityEngine;
using Segment = Il2CppSystem.ArraySegment<byte>;
using VoicePeer = Dissonance.Networking.ClientInfo<Dissonance.Integrations.MirrorIgnorance.MirrorConn>;
using GameNetworkCode = GameInterop::Mirror.GeneratedNetworkCode;

namespace Bigger_Walk;

// Gameplay identities are deliberately not voice identities. Connection ownership joins the two.
internal static class VoiceRoster
{
    internal const int Slots = 12;
    internal static float Range => Plugin.HearingDistance.Value;
    internal static MirrorIgnoranceServer Server;
    internal static bool Sending;
    internal static bool Enabled => VoicePatches.Ready && NetworkServer.active;
    sealed class Peer
    {
        internal VoicePeer Info;
        internal ushort Id;
        internal string Name;
        internal MirrorConn Transport;
        internal NetworkConnection Connection;
        internal PlayerCharacter Player;
        internal Vector3 Position;
        internal bool Located, Host, Muted;
        internal float LastLoud = -100f;
        internal float Loudness;
        internal readonly Dictionary<ushort, float> Roster = new();
        internal readonly Queue<(float Time, byte[] Data)> Onset = new();
    }
    readonly struct Route
    {
        internal readonly ushort Source;
        internal readonly Vector3 Position;
        internal readonly float Radius;
        internal readonly bool Positional;
        internal Route(ushort source, Vector3 position, float radius, bool positional)
        { Source = source; Position = position; Radius = radius; Positional = positional; }
    }
    sealed class PendingUse
    {
        internal RadioVoiceAssigner Radio;
        internal PlayerCharacter Player;
        internal PeckContext Context;
        internal float Until;
    }
    sealed class RadioRefresh
    {
        internal RadioVoiceAssigner Radio;
        internal PeckContext Context;
        internal string Room;
        internal readonly HashSet<ushort> Listeners = new();
        internal float At, Deadline;
        internal bool Reset, Released;
    }
    static readonly Dictionary<ushort, Peer> peers = new();
    static readonly Dictionary<string, Peer> voiceNames = new(StringComparer.Ordinal);
    static readonly Dictionary<IntPtr, PendingUse> pendingUses = new();
    static readonly Dictionary<IntPtr, RadioRefresh> radioRefreshes = new();
    static readonly Dictionary<int, ushort> connections = new();
    static readonly HashSet<int> handshakes = new();
    static readonly HashSet<string> muted = new(StringComparer.Ordinal);
    static readonly HashSet<string> roomNames = new(StringComparer.Ordinal);
    static readonly Dictionary<string, RoomMembership> joinedRooms = new(StringComparer.Ordinal);
    static readonly List<Route> special = new();
    static readonly HashSet<ushort> pinnedSources = new();
    static readonly HashSet<string> mutedNames = new(StringComparer.Ordinal);
    static readonly Il2CppSystem.Collections.Generic.List<VoicePeer> clientBuffer = new();
    static readonly Il2CppSystem.Collections.Generic.List<VoicePeer> roomBuffer = new();
    static DissonanceComms comms;
    static ushort? hostId;
    static uint session;
    static float nextRefresh, nextError, nextDiagnostic;
    static bool refreshing;
    static int publishedRoomCount;
    static long receivedVoicePackets, sentVoicePackets;
    internal static int AllocatedSlots => peers.Values.Where(p => !p.Host).Sum(p => p.Roster.Count);
    internal static int PeerCount => peers.Count;

    internal static void Bind(MirrorIgnoranceServer server)
    {
        if (!Enabled || server == null) return;
        if (Server != null && Server.Pointer == server.Pointer && session == server.SessionId) return;
        ClearState(false);
        Server = server; session = server.SessionId;
        comms = UnityEngine.Object.FindObjectOfType<DissonanceComms>();
        HostPlayback.ReserveVoices();
        Plugin.Log.LogInfo("[VOICE] Session started: per-listener 12-slot rosters; host reserves one routing slot.");
    }

    internal static void Reset()
    {
        ClearState(true);
        Server = null; comms = null; session = 0;
        HostPlayback.Clear();
    }

    static void ClearState(bool clearMute)
    {
        RelayQuality.Reset();
        // Only release memberships created here, never the game's original room memberships.
        if (comms != null)
            foreach (var membership in joinedRooms.Values)
                try { comms.Rooms.Leave(membership); } catch { }
        joinedRooms.Clear(); roomNames.Clear(); peers.Clear(); voiceNames.Clear(); connections.Clear();
        pendingUses.Clear(); radioRefreshes.Clear(); handshakes.Clear(); special.Clear(); pinnedSources.Clear(); mutedNames.Clear(); hostId = null; nextRefresh = nextDiagnostic = 0;
        delayed.Clear();
        publishedRoomCount = 0;
        receivedVoicePackets = sentVoicePackets = 0;
        if (clearMute) muted.Clear();
    }

    static string MuteKey(PlayerNetworking player)
    {
        if (player == null || player.isLocalPlayer) return null;
        return MuteKey(player.connectionToClient);
    }
    static string MuteKey(NetworkConnection connection)
    {
        if (connection == null || connection.connectionId == 0) return null;
        var address = connection.TryCast<NetworkConnectionToClient>()?.address;
        return string.IsNullOrEmpty(address) ? "session:" + connection.connectionId : "eos:" + address;
    }

    internal static bool IsMuted(PlayerNetworking player)
    {
        var key = MuteKey(player);
        return key != null && muted.Contains(key);
    }

    internal static bool SetMuted(PlayerNetworking player, bool value)
    {
        if (!Enabled || player == null) return false;
        var key = MuteKey(player); if (key == null) return false;
        if (value) muted.Add(key); else muted.Remove(key);
        if (value) { StopMutedBroadcasts(); HostPlayback.Discard(player); }
        Refresh(true);
        Plugin.Log.LogInfo("[VOICE] " + (value ? "Muted" : "Unmuted") + " connection " + player.connectionToClient.connectionId + ".");
        return true;
    }

    static bool IsMuted(Peer peer) => peer.Muted;
    internal static bool NameMuted(string name) => name != null && mutedNames.Contains(name);
    internal static void ObserveVoice(string name, float amplitude)
    {
        if (name == null || !voiceNames.TryGetValue(name, out var peer) || peer.Muted || !float.IsFinite(amplitude)) return;
        peer.Loudness = Mathf.Lerp(peer.Loudness, Mathf.Max(0f, amplitude), 1f - Mathf.Exp(-Time.unscaledDeltaTime * 8f));
        if (amplitude < Plugin.QuietThreshold.Value) return;
        float now = Time.unscaledTime;
        if (now - peer.LastLoud >= 1f) nextRefresh = 0; // Speech onset gets a roster scan without waiting for the 100ms timer.
        peer.LastLoud = now;
    }

    internal static void Tick()
    {
        if (!Enabled) { if (Server != null) Reset(); return; }
        if (Server == null) return;
        try
        {
            if (Server.SessionId != session) Bind(Server);
            HostPlayback.Tick();
            if (Time.unscaledTime >= nextRefresh) Refresh(false);
            RefreshRadioProviders();
        }
        catch (Exception error) { Report("update", error); }
    }

    internal static void Report(string operation, Exception error)
    {
        if (Time.unscaledTime < nextError) return;
        nextError = Time.unscaledTime + 5f;
        Plugin.Log.LogError("[VOICE] " + operation + ": " + error);
    }

    internal static void Refresh(bool immediate)
    {
        if (Server == null || refreshing || (!immediate && Time.unscaledTime < nextRefresh)) return;
        refreshing = true;
        try
        {
            float now = Time.unscaledTime;
            nextRefresh = now + .1f;
            if (comms == null) comms = UnityEngine.Object.FindObjectOfType<DissonanceComms>();
            clientBuffer.Clear(); Server._clients.GetClients(clientBuffer);
            var alive = new HashSet<ushort>();
            connections.Clear(); voiceNames.Clear(); mutedNames.Clear(); hostId = null;
            for (int i = 0; i < clientBuffer.Count; i++)
            {
                var info = clientBuffer[i]; if (info == null || !info.IsConnected) continue;
                alive.Add(info.PlayerId);
                if (!peers.TryGetValue(info.PlayerId, out var peer)) peers[info.PlayerId] = peer = new Peer();
                var name = info.PlayerName;
                var transport = info.Connection;
                var connection = transport.Connection;
                if (peer.Info != null && (peer.Name != name || peer.Connection?.Pointer != connection?.Pointer))
                {
                    peer.Onset.Clear(); peer.LastLoud = -100f; peer.Loudness = 0f;
                }
                peer.Info = info; peer.Located = false; peer.Player = null;
                peer.Id = info.PlayerId; peer.Name = name;
                voiceNames[peer.Name] = peer;
                peer.Transport = transport; peer.Connection = connection;
                peer.Host = comms != null && peer.Name == comms.LocalPlayerName;
                if (peer.Host) hostId = info.PlayerId;
                var muteKey = MuteKey(connection);
                peer.Muted = muteKey != null && muted.Contains(muteKey);
                if (peer.Muted) mutedNames.Add(peer.Name);
                if (connection != null) connections[connection.connectionId] = info.PlayerId;
                for (int r = 0; r < info.Rooms.Count; r++) roomNames.Add(info.Rooms[r]);
            }
            foreach (var id in peers.Keys.Where(id => !alive.Contains(id)).ToArray()) peers.Remove(id);
            var players = PlayerCharacter.allPlayerCharacters;
            if (players != null)
                for (int i = 0; i < players.Count; i++)
                {
                    var player = players[i]; var pn = player?.playerNetworking;
                    if (pn == null) continue;
                    Peer peer = null;
                    if (pn.isLocalPlayer && hostId.HasValue) peers.TryGetValue(hostId.Value, out peer);
                    else if (pn.connectionToClient != null && connections.TryGetValue(pn.connectionToClient.connectionId, out var id)) peers.TryGetValue(id, out peer);
                    if (peer == null) continue;
                    peer.Player = player; peer.Position = player.transform.position; peer.Located = true;
                    peer.Muted |= pn.isMuted;
                    if (peer.Muted) mutedNames.Add(peer.Name);
                    while (peer.Onset.Count > 0 && now - peer.Onset.Peek().Time > .25f) peer.Onset.Dequeue();
                }
            StopMutedBroadcasts();
            BuildSpecialRoutes(now);
            if (comms != null)
                foreach (var name in roomNames)
                    if (!comms.Rooms.Contains(name) && !joinedRooms.ContainsKey(name)) joinedRooms[name] = comms.Rooms.Join(name);
            bool newRooms = publishedRoomCount != roomNames.Count;
            foreach (var listener in peers.Values)
                if (!listener.Host && listener.Connection != null && handshakes.Contains(listener.Connection.connectionId))
                {
                    if (newRooms && hostId.HasValue && listener.Roster.ContainsKey(hostId.Value) && peers.TryGetValue(hostId.Value, out var anchor)) SendState(listener, anchor);
                    UpdateRoster(listener, now);
                }
            publishedRoomCount = roomNames.Count;
            if (now >= nextDiagnostic)
            {
                nextDiagnostic = now + 15f;
                Plugin.Log.LogInfo("[VOICE] Routing: peers=" + peers.Count + ", host=" + (hostId?.ToString() ?? "missing") +
                    ", located=" + peers.Values.Count(p => p.Located) + ", guest cues=" + AllocatedSlots +
                    ", audio packets in/sent=" + receivedVoicePackets + "/" + sentVoicePackets +
                    ", host playbacks=" + HostPlayback.Count + ".");
            }
        }
        finally { refreshing = false; }
    }

    static void BuildSpecialRoutes(float now)
    {
        special.Clear(); pinnedSources.Clear();
        foreach (var key in pendingUses.Where(p => p.Value.Until < now || p.Value.Radio == null).Select(p => p.Key).ToArray()) pendingUses.Remove(key);
        var radios = RadioVoiceAssigner.allRadios; if (radios == null) return;
        // Match vanilla's one broadcaster per room rule. A held but unused item is absent.
        var broadcasters = new Dictionary<string, List<(RadioVoiceAssigner Radio, PlayerCharacter Player)>>(StringComparer.Ordinal);
        for (int i = 0; i < radios.Count; i++)
        {
            var radio = radios[i]; if (radio == null || !radio.isActiveAndEnabled || radio.isInDeadZone || string.IsNullOrEmpty(radio.roomName)) continue;
            var player = radio.latestBroadcastPlayer;
            bool active = radio.isBroadcasting;
            if (pendingUses.TryGetValue(radio.Pointer, out var pending)) { active = true; player = pending.Player; }
            if (!active || player == null || IsMuted(player.playerNetworking)) continue;
            if (!broadcasters.TryGetValue(radio.roomName, out var group)) broadcasters[radio.roomName] = group = new();
            group.Add((radio, player)); roomNames.Add(radio.roomName);
        }
        foreach (var pair in broadcasters)
        {
            if (pair.Value.Count != 1) continue;
            var broadcaster = pair.Value[0];
            var source = peers.Values.FirstOrDefault(p => p.Player == broadcaster.Player);
            if (source == null) continue;
            for (int i = 0; i < radios.Count; i++)
            {
                var receiver = radios[i];
                if (receiver == null || !receiver.isActiveAndEnabled || receiver.isInDeadZone || receiver.roomName != pair.Key || receiver.voicePlayer == null) continue;
                if (receiver == broadcaster.Radio && !receiver.fullDuplex) continue;
                var voice = receiver.voicePlayer;
                var type = receiver._cachedVoiceType;
                float range = type == VoicePlayer.VoicePlayerType.Megaphone ? 800f : type == VoicePlayer.VoicePlayerType.WalkieTalkie ? 30f : 20f;
                // Spatial RTPC is evaluated by the game's own voice source controller.
                bool positional = true;
                if (voice.GetX(AudioRTPC.XAxisType.Spatial, out var spatial)) positional = spatial > .001f;
                special.Add(new Route(source.Id, voice.transform.position, range, positional));
                pinnedSources.Add(source.Id);
            }
        }
    }

    static bool SpecialAudible(ushort source, Peer listener)
    {
        foreach (var route in special)
            if (route.Source == source && (!route.Positional || DistanceSquared(route.Position, listener.Position) < route.Radius * route.Radius)) return true;
        return false;
    }
    static bool SpecialPinned(ushort source) => pinnedSources.Contains(source);
    static bool Audible(Peer source, Peer listener) => listener.Located && source.Located &&
        (DistanceSquared(source.Position, listener.Position) < Range * Range || SpecialAudible(source.Id, listener));
    static float DistanceSquared(Vector3 first, Vector3 second)
    {
        float x = first.x - second.x, y = first.y - second.y, z = first.z - second.z;
        return x * x + y * y + z * z;
    }

    static void UpdateRoster(Peer listener, float now)
    {
        var desired = new HashSet<ushort>();
        if (hostId.HasValue && hostId.Value != listener.Id) desired.Add(hostId.Value);
        var candidates = peers.Values.Where(p => p != listener && !p.Host && p.Located && listener.Located && !IsMuted(p))
            .Select(p => (Peer: p, Special: SpecialAudible(p.Id, listener), Distance: DistanceSquared(p.Position, listener.Position)))
            .Where(p => p.Special || p.Distance < Range * Range).ToList();
        int free = Slots - desired.Count;
        bool congested = candidates.Count > free;
        var chosen = candidates
            .Where(p => !congested || p.Special || now - p.Peer.LastLoud < 1f)
            .OrderByDescending(p => p.Special)
            .ThenByDescending(p => congested ? Mathf.Round(p.Peer.Loudness * 1000f) : 0f)
            .ThenBy(p => p.Distance)
            .ThenByDescending(p => listener.Roster.ContainsKey(p.Peer.Id)).ThenBy(p => p.Peer.Id)
            .Take(free);
        foreach (var item in chosen) desired.Add(item.Peer.Id);
        // Remove before add: vanilla must return the old cue before allocating its replacement.
        foreach (var id in listener.Roster.Keys.Where(id => !desired.Contains(id)).ToArray())
        {
            SendRemove(listener, id); listener.Roster.Remove(id);
        }
        foreach (var id in desired)
        {
            if (listener.Roster.ContainsKey(id) || !peers.TryGetValue(id, out var source)) continue;
            SendState(listener, source); listener.Roster[id] = now + .15f;
            if (SpecialPinned(id)) ScheduleRadioRefresh(source, listener, now);
            foreach (var frame in source.Onset)
                if (now - frame.Time <= .2f) QueueOnset(listener, source, frame.Data, now + .15f);
        }
    }

    static PeckContext CopyContext(PeckContext context, int state) => new PeckContext(state)
    {
        playerIdentity = context.playerIdentity, propIdentity = context.propIdentity, actionNumber = context.actionNumber
    };
    static bool SameUse(PeckContext first, PeckContext second) => first != null && second != null &&
        first.state == second.state && first.actionNumber == second.actionNumber &&
        first.playerIdentity?.Pointer == second.playerIdentity?.Pointer && first.propIdentity?.Pointer == second.propIdentity?.Pointer;

    static RadioRefresh QueueRadioRefresh(RadioVoiceAssigner radio, PeckContext context, float at)
    {
        if (!radioRefreshes.TryGetValue(radio.Pointer, out var retry) || retry.Room != radio.roomName || !SameUse(retry.Context, context))
            radioRefreshes[radio.Pointer] = retry = new RadioRefresh { Radio = radio, Context = CopyContext(context, context.state), Room = radio.roomName, At = at };
        return retry;
    }
    static void ScheduleRadioRefresh(Peer source, Peer listener, float now)
    {
        var radios = RadioVoiceAssigner.allRadios; if (radios == null) return;
        for (int i = 0; i < radios.Count; i++)
        {
            var radio = radios[i];
            if (radio == null || radio.trackedStateSystem == null) continue;
            pendingUses.TryGetValue(radio.Pointer, out var pending);
            var player = pending?.Player ?? radio.latestBroadcastPlayer;
            var context = pending?.Context ?? radio.trackedStateSystem.currentPeckContext;
            if (player != source.Player || (!radio.isBroadcasting && pending == null) || context == null || context.state == 0) continue;
            // Retry only on the client which just acquired this source, never the whole lobby.
            QueueRadioRefresh(radio, context, now + .25f).Listeners.Add(listener.Id);
        }
    }
    static void RefreshRadioProviders()
    {
        float now = Time.unscaledTime;
        foreach (var key in radioRefreshes.Where(p => p.Value.At <= now).Select(p => p.Key).ToArray())
        {
            var retry = radioRefreshes[key]; var radio = retry.Radio;
            var tracked = radio?.trackedStateSystem;
            var player = retry.Context.GetPlayerCharacter();
            if (radio == null || !radio.isActiveAndEnabled || radio.roomName != retry.Room || tracked == null || !SameUse(tracked.currentPeckContext, retry.Context) || player == null || IsMuted(player.playerNetworking))
            { radioRefreshes.Remove(key); continue; }
            if (retry.Reset && !retry.Released)
            {
                // Finish the previous press before restarting. Native clients do not clear
                // their old message-drain flag when a fresh press arrives in the same frame.
                retry.Released = true; retry.At = now + .25f; retry.Deadline = now + 2f;
                var release = CopyContext(retry.Context, 0);
                // -1 is native's authoritative effect action. A normal prediction action
                // can be skipped by DoPeck on the player who owns the radio.
                release.actionNumber = -1;
                VoicePatches.ApplyRadioLocally(radio, release);
                foreach (var listener in peers.Values)
                    if (!listener.Host) SendRadioRefresh(listener, tracked, release);
                continue;
            }
            if (retry.Reset && radio.isWaitingForMessageEnd && now < retry.Deadline)
            { retry.At = now + .05f; continue; }
            radioRefreshes.Remove(key);
            if (retry.Reset)
            {
                if (radio.isWaitingForMessageEnd)
                    Plugin.Log.LogWarning("[VOICE] Radio re-press drain timed out: " + radio.roomName + "; restarting current press once.");
                VoicePatches.ApplyRadioLocally(radio, retry.Context);
                var restart = CopyContext(retry.Context, retry.Context.state);
                restart.actionNumber = -1;
                foreach (var listener in peers.Values)
                    if (!listener.Host) SendRadioRefresh(listener, tracked, restart);
                Plugin.Log.LogInfo("[VOICE] Radio re-press correction applied: " + retry.Room + ", action=" + retry.Context.actionNumber + ".");
            }
            else if (radio.isBroadcasting)
                foreach (var id in retry.Listeners)
                    if (peers.TryGetValue(id, out var listener) && player.playerNetworking != null &&
                        connections.TryGetValue(player.playerNetworking.connectionToClient?.connectionId ?? 0, out var source) && listener.Roster.ContainsKey(source))
                        SendRadioRefresh(listener, tracked, retry.Context);
        }
    }

    static void SendRadioRefresh(Peer listener, TrackedPeckState tracked, PeckContext context)
    {
        var manager = PeckManager.Instance;
        if (listener.Host || listener.Connection == null || !listener.Connection.isReady || manager == null) return;
        var writer = NetworkWriterPool.Get();
        try
        {
            GameNetworkCode.Method_Public_Static_Void_NetworkWriter_ShellReference_PDM_0(writer, tracked.shellReference);
            GameNetworkCode._Write_PeckContext(writer, context);
            // Same reliable RPC and function hash as native PeckManager.ServerRetrigger (1.6.0).
            manager.SendTargetRPCInternal(listener.Connection, "System.Void PeckManager::Retrigger(SeaShell/ShellReference,PeckContext)", 0x43875769, writer, 0);
        }
        finally { NetworkWriterPool.Return(writer); }
    }

    internal static void AfterUse(RadioVoiceAssigner radio, PeckContext context)
    {
        if (!Enabled || radio == null) return;
        if (context.state == 0) { pendingUses.Remove(radio.Pointer); radioRefreshes.Remove(radio.Pointer); }
        Refresh(true);
    }

    sealed class Delayed
    {
        internal readonly ushort Listener, Source;
        internal readonly float At;
        internal readonly byte[] Data;
        internal readonly string ListenerName, SourceName;
        internal readonly IntPtr ListenerConnection, SourceConnection;
        internal Delayed(Peer listener, Peer source, float at, byte[] data)
        {
            Listener = listener.Id; Source = source.Id; At = at; Data = data;
            ListenerName = listener.Name; SourceName = source.Name;
            ListenerConnection = listener.Connection?.Pointer ?? IntPtr.Zero;
            SourceConnection = source.Connection?.Pointer ?? IntPtr.Zero;
        }
    }
    static readonly Queue<Delayed> delayed = new();
    static void QueueOnset(Peer listener, Peer source, byte[] data, float at)
    {
        // Bound by the event cap and 200ms of Opus frames. Never retain unbounded packet history.
        if (delayed.Count < Math.Max(512, peers.Count * Slots * 12)) delayed.Enqueue(new Delayed(listener, source, at, data));
    }
    internal static void Flush()
    {
        float now = Time.unscaledTime;
        while (delayed.Count > 0 && delayed.Peek().At <= now)
        {
            var frame = delayed.Dequeue();
            if (now - frame.At > .2f) continue;
            if (Server == null || !peers.TryGetValue(frame.Listener, out var listener) || !peers.TryGetValue(frame.Source, out var source) || IsMuted(source) || !listener.Roster.ContainsKey(frame.Source) || !Audible(source, listener)) continue;
            if (listener.Name != frame.ListenerName || source.Name != frame.SourceName ||
                (listener.Connection?.Pointer ?? IntPtr.Zero) != frame.ListenerConnection ||
                (source.Connection?.Pointer ?? IntPtr.Zero) != frame.SourceConnection) continue;
            var array = new Il2CppStructArray<byte>(frame.Data);
            Send(listener, new Segment(array), false);
        }
    }

    static void Send(Peer listener, Segment data, bool reliable)
    {
        bool previous = Sending; Sending = true;
        try
        {
            Server.Send(data, listener.Transport, reliable ? MirrorIgnoranceCommsNetwork.ReliableSequencedChannel : MirrorIgnoranceCommsNetwork.UnreliableChannel);
            if (!reliable) sentVoicePackets++;
        }
        finally { Sending = previous; }
    }
    static PacketWriter Writer() => new(new Il2CppStructArray<byte>(8192));
    static Il2CppSystem.Collections.ObjectModel.ReadOnlyCollection<string> HostRooms()
    {
        var names = new Il2CppSystem.Collections.Generic.List<string>();
        foreach (var room in roomNames) names.Add(room);
        return new Il2CppSystem.Collections.ObjectModel.ReadOnlyCollection<string>(names.Cast<Il2CppSystem.Collections.Generic.IList<string>>());
    }
    static void SendState(Peer listener, Peer source)
    {
        var info = source.Info;
        var writer = Writer().WriteClientState(session, info.PlayerName, info.PlayerId, info.CodecSettings, source.Host ? HostRooms() : info.Rooms);
        Send(listener, writer.Written, true);
    }
    static void SendRemove(Peer listener, ushort id) => Send(listener, Writer().WriteRemoveClient(session, id).Written, true);

    internal static bool Header(Segment data, out MessageTypes type)
    {
        return EventVoice.VoicePacket.TryReadType(data, out type);
    }

    internal static bool FilterOutgoing(MirrorIgnoranceServer server, ref Segment data, MirrorConn destination)
    {
        if (!Enabled || Sending) return true;
        Bind(server);
        if (!Header(data, out var type)) return true;
        var connection = destination.Connection;
        bool host = connection == null || connection.connectionId == 0 ||
            (NetworkServer.localConnection != null && connection.Pointer == NetworkServer.localConnection.Pointer) ||
            (hostId.HasValue && peers.TryGetValue(hostId.Value, out var hostPeer) && hostPeer.Connection?.Pointer == connection?.Pointer);
        var reader = new PacketReader(data); reader.ReadPacketHeader(out _);
        if (type == MessageTypes.HandshakeResponse)
        {
            // Native already sends an empty handshake, then individual ClientState messages.
            reader.ReadHandshakeResponseHeader(out var sid, out var id);
            // Registration has happened inside native ProcessHandshakeRequest. Populate mappings
            // before its immediately following ClientState sends, rather than waiting for Update.
            Refresh(true);
            if (connection != null && handshakes.Add(connection.connectionId))
                Plugin.Log.LogInfo("[VOICE] Native handshake: connection " + connection.connectionId + ", voice participant " + id + ".");
            nextRefresh = 0; return true;
        }
        if (type == MessageTypes.HandshakeRequest) return true;
        if (reader.ReadUInt32() != session) return true;
        ushort sourceId;
        switch (type)
        {
            case MessageTypes.ClientState: sourceId = reader.ReadClientStateHeader().PlayerId; break;
            case MessageTypes.DeltaChannelState: reader.ReadDeltaChannelState(out _, out sourceId, out _); break;
            case MessageTypes.RemoveClient: reader.ReadRemoveClient(out sourceId); break;
            case MessageTypes.VoiceData: reader.ReadVoicePacketHeader1(out sourceId); break;
            default: return true;
        }
        // Keep host metadata even for muted reconnects, so unmuting needs no new handshake.
        if (host) return type != MessageTypes.VoiceData || !peers.TryGetValue(sourceId, out var speaker) || !IsMuted(speaker);
        if (!connections.TryGetValue(connection.connectionId, out var listenerId) || !peers.TryGetValue(listenerId, out var target)) return false;
        if (sourceId == listenerId) return type != MessageTypes.VoiceData;
        if (type == MessageTypes.RemoveClient) return target.Roster.Remove(sourceId);
        if (!target.Roster.ContainsKey(sourceId)) return false;
        if (type == MessageTypes.VoiceData && peers.TryGetValue(sourceId, out var voiceSource) && (IsMuted(voiceSource) || !Audible(voiceSource, target))) return false;
        if (type == MessageTypes.ClientState && hostId == sourceId && peers.TryGetValue(sourceId, out var anchor))
            data = Writer().WriteClientState(session, anchor.Info.PlayerName, sourceId, anchor.Info.CodecSettings, HostRooms()).Written;
        if (type == MessageTypes.DeltaChannelState && hostId == sourceId) return false; // Routing subscriptions cannot be removed by the host's normal room transitions.
        return true;
    }

    // Returns true only when handled; non-voice protocol traffic stays with Dissonance.
    internal static bool Incoming(MirrorIgnoranceServer server, NetworkConnection connection, Segment packet, bool local)
    {
        if (!Enabled || !Header(packet, out var type)) return false;
        Bind(server);
        if (type != MessageTypes.VoiceData && type != MessageTypes.ServerRelayReliable && type != MessageTypes.ServerRelayUnreliable) return false;
        var reader = new PacketReader(packet); reader.ReadPacketHeader(out _);
        if (reader.ReadUInt32() != session) return false;
        if (type != MessageTypes.VoiceData)
        {
            if (!EventVoice.VoicePacket.TryRelayPayload(packet, out packet)) return true;
            if (!Header(packet, out type) || type != MessageTypes.VoiceData) return false;
            reader = new PacketReader(packet); reader.ReadPacketHeader(out _);
            if (reader.ReadUInt32() != session) return true;
        }
        reader.ReadVoicePacketHeader1(out var sender);
        if (!peers.TryGetValue(sender, out var source)) { Refresh(true); peers.TryGetValue(sender, out source); }
        if (source == null) return true;
        if (local ? !source.Host : connection == null || source.Connection == null || source.Connection.Pointer != connection.Pointer) return true;
        if (IsMuted(source)) return true;
        receivedVoicePackets++;
        reader.ReadVoicePacketHeader2(out var options, out var sequence, out var channelCount);
        if (channelCount > 128) return true;
        var recipients = new HashSet<ushort>();
        for (int i = 0; i < channelCount; i++)
        {
            reader.ReadVoicePacketChannel(out var channel, out var recipient);
            if (channel.Type == ChannelType.Player) recipients.Add(recipient);
            else
            {
                roomBuffer.Clear(); Server._clients.TryGetClientsInRoom(recipient, roomBuffer);
                for (int r = 0; r < roomBuffer.Count; r++) recipients.Add(roomBuffer[r].PlayerId);
            }
        }
        // Validate the encoded payload too before replacing the native relay path.
        var audio = reader.ReadByteSegment();
        float now = Time.unscaledTime;
        var copy = new byte[packet.Count];
        var packetArray = packet.Array;
        Marshal.Copy(IntPtr.Add(packetArray.Pointer, 4 * IntPtr.Size + packet.Offset), copy, 0, copy.Length);
        GC.KeepAlive(packetArray);
        // Decode all sources for activity and cached radio samples, not global hearing.
        // Native playback applies its own full audio processing and distance attenuation.
        if (hostId.HasValue && sender != hostId.Value && peers.TryGetValue(hostId.Value, out var hostPeer)) Send(hostPeer, packet, false);
        if (!local && RelayQuality.Recompress && source.Info.CodecSettings.Codec == Dissonance.Audio.Codecs.Codec.Opus)
        {
            var job = new RelayQuality.Job { Source = sender, Name = source.Name, Connection = source.Connection?.Pointer ?? IntPtr.Zero,
                Session = session, Data = copy, AudioOffset = audio.Offset - packet.Offset, AudioLength = audio.Count,
                Sequence = sequence, ChannelSession = options.ChannelSession, Rate = source.Info.CodecSettings.SampleRate,
                Samples = checked((int)source.Info.CodecSettings.FrameSize), Recipients = recipients.ToArray(), At = now,
                Fec = Dissonance.Config.VoiceSettings.Instance.ForwardErrorCorrection, Loss = comms?.PacketLoss ?? 0f };
            if (RelayQuality.Submit(job)) return true;
        }
        RelayToGuests(source, recipients, copy, now, packet);
        return true;
    }

    static void RelayToGuests(Peer source, IEnumerable<ushort> recipients, byte[] copy, float now, Segment packet = null)
    {
        while (source.Onset.Count >= 12) source.Onset.Dequeue();
        source.Onset.Enqueue((now, copy));
        packet ??= new Segment(new Il2CppStructArray<byte>(copy));
        foreach (var id in recipients)
        {
            if (id == source.Id || hostId == id || !peers.TryGetValue(id, out var listener) || !listener.Roster.TryGetValue(source.Id, out var ready) || !Audible(source, listener)) continue;
            if (now < ready) QueueOnset(listener, source, copy, ready);
            else Send(listener, packet, false);
        }
    }

    internal static void FinishQuality(RelayQuality.Job job)
    {
        if (!Enabled || session != job.Session || Time.unscaledTime - job.At > .2f ||
            !peers.TryGetValue(job.Source, out var source) || source.Name != job.Name ||
            (source.Connection?.Pointer ?? IntPtr.Zero) != job.Connection || IsMuted(source)) return;
        RelayToGuests(source, job.Recipients, job.Data, Time.unscaledTime);
    }

    internal static void ResetGuestQuality()
    {
        delayed.Clear();
        foreach (var source in peers.Values) source.Onset.Clear();
        foreach (var listener in peers.Values)
        {
            if (listener.Host) continue;
            foreach (var id in listener.Roster.Keys.ToArray())
                if (id != hostId) { SendRemove(listener, id); listener.Roster.Remove(id); }
        }
        nextRefresh = 0;
    }

    internal static bool BeforeUse(RadioVoiceAssigner radio, PeckContext context)
    {
        if (!Enabled || radio == null || context == null) return true;
        if (context.state == 0)
        { pendingUses.Remove(radio.Pointer); radioRefreshes.Remove(radio.Pointer); return true; }
        var player = context.GetPlayerCharacter(); if (player == null) return true;
        if (IsMuted(player.playerNetworking))
        {
            ReleaseMutedRadio(radio, player);
            return false;
        }
        bool repress = radio.isWaitingForMessageEnd;
        // Protect the host's native Update from an old release stopping this new press.
        radio.isWaitingForMessageEnd = false;
        pendingUses[radio.Pointer] = new PendingUse { Radio = radio, Player = player, Context = CopyContext(context, context.state), Until = Time.unscaledTime + .3f };
        if (repress)
        {
            var retry = QueueRadioRefresh(radio, context, Time.unscaledTime + .05f);
            retry.Reset = true;
        }
        Refresh(true); return true;
    }

    static void ReleaseMutedRadio(RadioVoiceAssigner radio, PlayerCharacter player)
    {
        pendingUses.Remove(radio.Pointer); radioRefreshes.Remove(radio.Pointer);
        var tracked = radio.trackedStateSystem;
        if (tracked == null) return;
        var current = tracked.currentPeckContext;
        if (current != null && current.state != 0)
        {
            var release = CopyContext(current, 0);
            release.playerIdentity = player.playerNetworking.netIdentity;
            release.actionNumber = -1;
            tracked.SetState(release);
        }
        tracked.ServerRejectPrediction(player);
    }

    static void StopMutedBroadcasts()
    {
        var radios = RadioVoiceAssigner.allRadios; if (radios == null) return;
        for (int i = 0; i < radios.Count; i++)
        {
            var radio = radios[i];
            if (radio == null || !radio.isBroadcasting || radio.latestBroadcastPlayer == null || !IsMuted(radio.latestBroadcastPlayer.playerNetworking)) continue;
            var tracked = radio.trackedStateSystem;
            if (tracked == null) continue;
            if (tracked.currentPeckContext.state != 0)
            {
                ReleaseMutedRadio(radio, radio.latestBroadcastPlayer);
            }
            pendingUses.Remove(radio.Pointer); radioRefreshes.Remove(radio.Pointer);
        }
    }
}

