using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace Bigger_Walk;

// Only managed buffers and native Opus handles cross the worker boundary.
internal static class RelayQuality
{
    internal sealed class Job
    {
        internal ushort Source, Sequence;
        internal byte ChannelSession;
        internal string Name;
        internal IntPtr Connection;
        internal uint Session;
        internal byte[] Data;
        internal int AudioOffset, AudioLength, Rate, Samples;
        internal ushort[] Recipients;
        internal float At, Loss;
        internal bool Fec;
        internal long Submitted;
    }
    static Worker worker;
    static int applied, microphoneBitrate = 17000;
    static volatile bool hosting;
    static bool unavailable;
    static float nextWarning;
    internal static bool Recompress => hosting && applied < 17000 && !unavailable;

    internal static void Tick()
    {
        hosting = VoiceRoster.Enabled;
        Volatile.Write(ref microphoneBitrate, Plugin.VoiceBitrate);
        ApplyMicrophone();
        int desired = hosting ? Plugin.VoiceBitrate : 0;
        if (desired != applied)
        {
            worker?.Stop(); worker = null; unavailable = false;
            bool resetGuests = applied != 0 && desired != 0 && (applied < 17000 || desired < 17000);
            applied = desired;
            if (resetGuests) VoiceRoster.ResetGuestQuality();
            if (desired > 0 && desired < 17000)
            {
                try
                {
                    Native.Load(Path.Combine(Application.dataPath, "Plugins", "x86_64", "opus.dll"));
                    worker = new Worker(desired);
                }
                catch (Exception ex)
                {
                    unavailable = true;
                    Plugin.Log.LogWarning("[VOICE QUALITY] Guest recompression unavailable; preserving original packets: " + ex.Message);
                }
            }
            if (desired > 0) Plugin.Log.LogInfo("[VOICE QUALITY] " + Plugin.VoicePercentage + "%: host mic " + desired + "bps; guest relay " + (Recompress ? "background recompression" : "original packets") + ".");
        }
        if (worker == null) return;
        while (worker.Errors.TryDequeue(out var error))
            if (Time.unscaledTime >= nextWarning) { nextWarning = Time.unscaledTime + 10f; Plugin.Log.LogWarning("[VOICE QUALITY] " + error); }
        while (worker.Completed.TryDequeue(out var job)) VoiceRoster.FinishQuality(job);
    }

    internal static bool Submit(Job job)
    {
        if (worker == null) return false;
        job.Submitted = Stopwatch.GetTimestamp();
        if (!worker.Input.TryAdd(job) && Time.unscaledTime >= nextWarning)
        {
            nextWarning = Time.unscaledTime + 10f;
            Plugin.Log.LogWarning("[VOICE QUALITY] Encoder queue full; dropping voice instead of building latency. Set quality to 100% to disable guest recompression.");
        }
        return true;
    }

    internal static void Reset()
    {
        worker?.Stop(); worker = null; applied = 0; hosting = false; unavailable = false;
    }

    static readonly object microphoneGate = new();
    static Dissonance.Audio.Codecs.Opus.OpusNative.OpusEncoder microphone;
    static IntPtr appliedMicrophone;
    static int originalMicBitrate, lastMicBitrate;
    static int microphoneWarning;
    internal static void RegisterMicrophone(Dissonance.Audio.Codecs.Opus.OpusNative.OpusEncoder encoder, ref int bitrate)
    {
        lock (microphoneGate)
        {
            if (microphone?.Pointer != encoder.Pointer) { microphone = encoder; originalMicBitrate = bitrate; }
            if (hosting) bitrate = Volatile.Read(ref microphoneBitrate);
        }
    }
    static void ApplyMicrophone()
    {
        Dissonance.Audio.Codecs.Opus.OpusNative.OpusEncoder encoder;
        int original;
        lock (microphoneGate) { encoder = microphone; original = originalMicBitrate; }
        if (encoder == null) return;
        int desired = hosting ? Volatile.Read(ref microphoneBitrate) : original;
        if (appliedMicrophone == encoder.Pointer && lastMicBitrate == desired) return;
        try
        {
            if (encoder._disposed) return;
            if (encoder.Bitrate != desired) encoder.Bitrate = desired;
            appliedMicrophone = encoder.Pointer; lastMicBitrate = desired;
        }
        catch (Exception ex)
        {
            if (hosting && Interlocked.Exchange(ref microphoneWarning, 1) == 0)
                Plugin.Log.LogWarning("[VOICE QUALITY] Cannot apply microphone bitrate; native encoding continues: " + ex.Message);
        }
    }

    sealed class Worker
    {
        internal readonly BlockingCollection<Job> Input = new(128);
        internal readonly ConcurrentQueue<Job> Completed = new();
        internal readonly ConcurrentQueue<string> Errors = new();
        readonly Dictionary<(string, IntPtr), Stream> streams = new();
        readonly HashSet<(string, IntPtr)> unsupported = new();
        readonly int bitrate;
        volatile bool stopped;
        internal Worker(int bitrate)
        {
            this.bitrate = bitrate;
            new Thread(Run) { IsBackground = true, Name = "Big Lobbies voice quality" }.Start();
        }
        internal void Stop()
        {
            stopped = true;
            try { Input.CompleteAdding(); } catch (ObjectDisposedException) { }
        }
        void Run()
        {
            try
            {
                foreach (var job in Input.GetConsumingEnumerable())
                {
                    if (stopped) break;
                    if ((Stopwatch.GetTimestamp() - job.Submitted) / (double)Stopwatch.Frequency > .2) continue;
                    var key = (job.Name, job.Connection);
                    if (unsupported.Contains(key)) { Completed.Enqueue(job); continue; }
                    try
                    {
                        if (!streams.TryGetValue(key, out var stream)) streams[key] = stream = new Stream(job, bitrate);
                        if (stream.Encode(job)) Completed.Enqueue(job);
                    }
                    catch (Exception ex)
                    {
                        if (streams.Remove(key, out var stream)) stream.Dispose();
                        unsupported.Add(key);
                        if (Errors.IsEmpty) Errors.Enqueue("Recompression failed for a voice stream; forwarding original frame: " + ex.Message);
                        Completed.Enqueue(job);
                    }
                    if (streams.Count > 0)
                    {
                        List<(string, IntPtr)> expired = null;
                        foreach (var pair in streams)
                            if (job.Submitted - pair.Value.LastUsed > Stopwatch.Frequency * 15L) (expired ??= new()).Add(pair.Key);
                        if (expired != null) foreach (var old in expired) { streams[old].Dispose(); streams.Remove(old); }
                    }
                }
            }
            finally
            {
                foreach (var stream in streams.Values) stream.Dispose();
                streams.Clear();
                while (Input.TryTake(out _)) { }
                Input.Dispose();
            }
        }
    }

    sealed class Stream : IDisposable
    {
        IntPtr decoder, encoder;
        readonly int rate, samples;
        readonly float[] pcm;
        readonly byte[] encoded = new byte[1276];
        ushort sequence;
        byte channelSession;
        bool started;
        internal long LastUsed;
        internal Stream(Job job, int bitrate)
        {
            rate = job.Rate; samples = job.Samples;
            if (rate != 48000 || samples <= 0 || samples > 5760) throw new InvalidOperationException("Unsupported Opus frame format.");
            pcm = new float[5760];
            try
            {
                decoder = Native.CreateDecoder(rate, 1, out var error);
                if (decoder == IntPtr.Zero || error != 0) throw new InvalidOperationException("Opus decoder: " + error);
                encoder = Native.CreateEncoder(rate, 1, 2048, out error);
                if (encoder == IntPtr.Zero || error != 0) throw new InvalidOperationException("Opus encoder: " + error);
                Check(Native.EncoderCtl(encoder, 4002, bitrate));
                Check(Native.EncoderCtl(encoder, 4012, job.Fec ? 1 : 0));
            }
            catch { Dispose(); throw; }
        }
        internal unsafe bool Encode(Job job)
        {
            if (job.Rate != rate || job.Samples != samples || job.AudioOffset < 2 || job.AudioLength < 1 ||
                job.AudioOffset > job.Data.Length - job.AudioLength || job.AudioOffset + job.AudioLength != job.Data.Length)
                throw new InvalidOperationException("Voice payload layout changed.");
            int lengthOffset = job.AudioOffset - 2;
            if (((job.Data[lengthOffset] << 8) | job.Data[lengthOffset + 1]) != job.AudioLength)
                throw new InvalidOperationException("Voice payload length field mismatch.");
            fixed (byte* input = job.Data)
            fixed (float* output = pcm)
            fixed (byte* compressed = encoded)
            {
                if (Native.PacketChannels((IntPtr)(input + job.AudioOffset)) != 1) throw new InvalidOperationException("Stereo voice is not recompressed.");
                if (Native.PacketSamples((IntPtr)(input + job.AudioOffset), job.AudioLength, rate) != samples)
                    throw new InvalidOperationException("Voice frame duration mismatch.");
                int gap = started ? (ushort)(job.Sequence - sequence) : 1;
                if (started && job.ChannelSession == channelSession && job.Submitted - LastUsed <= Stopwatch.Frequency && (gap == 0 || gap >= 32768)) return false;
                if (!started || job.ChannelSession != channelSession || gap > 4 || job.Submitted - LastUsed > Stopwatch.Frequency)
                {
                    Check(Native.DecoderCtl(decoder, 4028, 0));
                    if (!started || job.ChannelSession != channelSession) Check(Native.EncoderCtl(encoder, 4028, 0));
                }
                else
                    for (int i = 1; i < gap; i++) Check(Native.Decode(decoder, IntPtr.Zero, 0, (IntPtr)output, samples, 0));
                int count = Native.Decode(decoder, (IntPtr)(input + job.AudioOffset), job.AudioLength, (IntPtr)output, pcm.Length, 0);
                Check(count);
                if (count != samples) throw new InvalidOperationException("Unexpected decoded frame length.");
                Check(Native.EncoderCtl(encoder, 4012, job.Fec ? 1 : 0));
                Check(Native.EncoderCtl(encoder, 4014, Math.Clamp((int)(job.Loss * 100f), 0, 100)));
                count = Native.Encode(encoder, (IntPtr)output, samples, (IntPtr)compressed, encoded.Length);
                Check(count);
                var packet = new byte[job.AudioOffset + count];
                Buffer.BlockCopy(job.Data, 0, packet, 0, job.AudioOffset);
                packet[lengthOffset] = (byte)(count >> 8); packet[lengthOffset + 1] = (byte)count;
                Buffer.BlockCopy(encoded, 0, packet, job.AudioOffset, count);
                job.Data = packet; sequence = job.Sequence; channelSession = job.ChannelSession; started = true; LastUsed = job.Submitted;
                return true;
            }
        }
        static void Check(int result) { if (result < 0) throw new InvalidOperationException("Opus error " + result); }
        public void Dispose()
        {
            if (decoder != IntPtr.Zero) { Native.DestroyDecoder(decoder); decoder = IntPtr.Zero; }
            if (encoder != IntPtr.Zero) { Native.DestroyEncoder(encoder); encoder = IntPtr.Zero; }
        }
    }

    static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr NewEncoder(int rate, int channels, int application, out int error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr NewDecoder(int rate, int channels, out int error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Destroy(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int EncodeFrame(IntPtr handle, IntPtr pcm, int samples, IntPtr output, int capacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int DecodeFrame(IntPtr handle, IntPtr input, int length, IntPtr pcm, int capacity, int fec);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Control(IntPtr handle, int request, int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Channels(IntPtr packet);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Samples(IntPtr packet, int length, int rate);
        static IntPtr library;
        internal static NewEncoder CreateEncoder;
        internal static NewDecoder CreateDecoder;
        internal static Destroy DestroyEncoder, DestroyDecoder;
        internal static EncodeFrame Encode;
        internal static DecodeFrame Decode;
        internal static Control EncoderCtl, DecoderCtl;
        internal static Channels PacketChannels;
        internal static Samples PacketSamples;
        internal static void Load(string path)
        {
            if (library != IntPtr.Zero) return;
            var handle = NativeLibrary.Load(path);
            try
            {
                T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));
                CreateEncoder = Get<NewEncoder>("opus_encoder_create"); CreateDecoder = Get<NewDecoder>("opus_decoder_create");
                DestroyEncoder = Get<Destroy>("opus_encoder_destroy"); DestroyDecoder = Get<Destroy>("opus_decoder_destroy");
                Encode = Get<EncodeFrame>("opus_encode_float"); Decode = Get<DecodeFrame>("opus_decode_float");
                EncoderCtl = Get<Control>("dissonance_opus_encoder_ctl_in"); DecoderCtl = Get<Control>("dissonance_opus_decoder_ctl_in");
                PacketChannels = Get<Channels>("opus_packet_get_nb_channels"); PacketSamples = Get<Samples>("opus_packet_get_nb_samples");
                library = handle; // Kept for process lifetime, like the game's own Opus import.
            }
            catch { NativeLibrary.Free(handle); throw; }
        }
    }
}
