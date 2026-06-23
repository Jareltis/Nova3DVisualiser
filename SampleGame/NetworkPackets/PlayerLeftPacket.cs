using Nova3DVisualiser.Network;

namespace SampleGame.NetworkPackets;

/// <summary>
/// Server -> clients: a peer with this net id has disconnected, so drop its avatar. Same
/// length-prefixed scheme as the other app packets.
/// </summary>
public class PlayerLeftPacket : INetworkPacket
{
    public int NetId;

    public PlayerLeftPacket() { }

    public void Serialize(BinaryWriter w) { w.Write(NetId); }
    public void Deserialize(BinaryReader r) { NetId = r.ReadInt32(); }
}
