using Dissonance.Networking;
using Segment = Il2CppSystem.ArraySegment<byte>;

namespace EventVoice;

// PacketReader.ReadRelay's generated out ArraySegment wrapper allocates pointer-sized
// storage for a 16-byte native value. Read its byte layout without that wrapper.
internal static class VoicePacket
{
    internal static bool TryReadType(Segment packet, out MessageTypes type)
    {
        type = default;
        if (!Valid(packet) || packet.Count < 3) return false;
        var bytes = packet.Array;
        int start = packet.Offset;
        if (bytes[start] != 0x8b || bytes[start + 1] != 0xc7) return false;
        type = (MessageTypes)bytes[start + 2];
        return true;
    }

    internal static bool TryRelayPayload(Segment packet, out Segment payload)
    {
        payload = null;
        if (!TryReadType(packet, out var type) ||
            (type != MessageTypes.ServerRelayReliable && type != MessageTypes.ServerRelayUnreliable) || packet.Count < 10) return false;
        var bytes = packet.Array;
        int start = packet.Offset;
        // Magic/type (3), session (4), byte destination count, UInt16 destinations,
        // UInt16 payload length, then the nested packet. Integers are big-endian.
        int lengthIndex = 8 + bytes[start + 7] * 2;
        if (lengthIndex > packet.Count - 2) return false;
        int length = (bytes[start + lengthIndex] << 8) | bytes[start + lengthIndex + 1];
        int dataIndex = lengthIndex + 2;
        if (length < 3 || length > packet.Count - dataIndex) return false;
        payload = new Segment(bytes, start + dataIndex, length);
        return true;
    }

    static bool Valid(Segment packet) => packet is not null && packet.Array is not null &&
        packet.Offset >= 0 && packet.Count >= 0 && packet.Offset <= packet.Array.Length - packet.Count;
}
