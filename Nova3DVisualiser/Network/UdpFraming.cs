using System.Collections.Concurrent;

namespace Nova3DVisualiser.Network;

// Pure, socket-free helpers for the additive UDP fast-path (plan E, stage E1). Kept separate from
// NetworkManager so the framing + sequence logic can be unit-tested (udptest) with no real sockets.
//
// A UDP datagram is message-oriented — one datagram == one packet — so there is NO length prefix
// (unlike the TCP BuildFrame). Header: TypeId(int32) + SenderId(int32) + Seq(int32) + Token(int64) = 20
// bytes, then the packet payload. The Token is a per-session secret the server issues over TCP and the
// client stamps on every UDP frame, so an off-path spoofer can't inject fake datagrams (client→server).
public static class UdpFraming
{
    public const int HeaderSize = 20;   // TypeId(4) + SenderId(4) + Seq(4) + Token(8)

    // Serialize the packet, then prepend the UDP header. Mirrors NetworkManager.BuildFrame's payload
    // pattern; the differences are Seq (instead of a length prefix) and the per-session Token.
    public static byte[] BuildUdpFrame<T>(T packet, int senderId, int seq, long token) where T : INetworkPacket
    {
        int typeId = PacketManager.GetId(packet.GetType());

        using var payloadMs = new MemoryStream();
        using var payloadWriter = new BinaryWriter(payloadMs);
        packet.Serialize(payloadWriter);
        byte[] payload = payloadMs.ToArray();

        using var ms = new MemoryStream(HeaderSize + payload.Length);
        using var writer = new BinaryWriter(ms);
        writer.Write(typeId);
        writer.Write(senderId);
        writer.Write(seq);
        writer.Write(token);
        writer.Write(payload);
        return ms.ToArray();
    }

    // Parse a received datagram. Returns false (packet == null) on ANY malformed/short buffer or unknown
    // typeId — it never throws out of this method, so a garbage datagram can't crash the receive loop.
    public static bool TryParseUdpFrame(byte[] buffer, int length, out int typeId, out int senderId, out int seq, out long token, out INetworkPacket? packet)
    {
        typeId = 0;
        senderId = 0;
        seq = 0;
        token = 0;
        packet = null;

        if (buffer == null || length < HeaderSize || length > buffer.Length) return false;

        try
        {
            using var ms = new MemoryStream(buffer, 0, length);
            using var reader = new BinaryReader(ms);

            typeId = reader.ReadInt32();
            senderId = reader.ReadInt32();
            seq = reader.ReadInt32();
            token = reader.ReadInt64();

            INetworkPacket instance = PacketManager.CreateInstance(typeId);   // throws on an unknown id
            instance.Deserialize(reader);                                     // throws if the payload is short/garbled
            packet = instance;
            return true;
        }
        catch
        {
            packet = null;
            return false;
        }
    }
}

// Latest-wins sequence filter for the receive side, keyed per (senderId, typeId). Accept iff Seq is
// monotonic NON-DECREASING (Seq >= the last accepted Seq for the key):
//  - a strictly-OLDER datagram (Seq < stored) is dropped, so an out-of-order older frame never overwrites
//    a newer one;
//  - a SAME-Seq datagram (Seq == stored) is accepted (no advance), so a chunked physics flush that shares
//    ONE Seq across its datagrams (see NetworkManager.SendPacketUnreliableGroup) is reorder-immune WITHIN
//    the flush — every chunk passes regardless of arrival order;
//  - duplicates / same-Seq re-sends are harmless for the idempotent latest-wins streams this path carries
//    (transforms overwrite a pose; physics-sync overwrites _physTargets[id] per id).
//
// Seq is a per-sender counter (Interlocked.Increment on the send side; a chunked flush takes ONE seq for
// all its datagrams). Wraparound is NOT handled: an int seq at streaming rates won't wrap within a session.
public class UdpSeqFilter
{
    // Thread-safe: the receive loop runs on a background task. Value = last accepted Seq for the key.
    private readonly ConcurrentDictionary<long, int> _lastSeq = new();

    public bool Accept(int senderId, int typeId, int seq)
    {
        long key = ((long)senderId << 32) | (uint)typeId;

        while (true)
        {
            if (_lastSeq.TryGetValue(key, out int stored))
            {
                if (seq < stored) return false;                       // strictly older → drop
                if (seq == stored) return true;                       // same frame (a chunked flush shares one seq) → accept, no advance
                if (_lastSeq.TryUpdate(key, seq, stored)) return true; // newer → advance
                // lost a race with another thread; re-read and retry
            }
            else
            {
                if (_lastSeq.TryAdd(key, seq)) return true;           // first datagram for this key
                // another thread added it first; re-read and retry
            }
        }
    }
}
