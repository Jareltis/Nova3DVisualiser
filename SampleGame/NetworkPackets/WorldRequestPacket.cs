using Nova3DVisualiser.Network;

namespace SampleGame.NetworkPackets;

/// <summary>
/// Sent by a joining client (repeatedly until answered) to ask the server for its world.
/// Has no payload — its arrival is the whole signal; the server replies with a WorldSyncPacket.
/// </summary>
public class WorldRequestPacket : INetworkPacket
{
    public WorldRequestPacket() { }

    public void Serialize(BinaryWriter w) { }
    public void Deserialize(BinaryReader r) { }
}
