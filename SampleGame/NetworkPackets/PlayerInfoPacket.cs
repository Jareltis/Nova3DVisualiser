using Nova3DVisualiser.Network;

namespace SampleGame.NetworkPackets;

/// <summary>
/// Bidirectional (N2): announces a peer's nickname for a given net id, so every peer can build a
/// roster (netId -> nick). A joining client sends its own (NetId = its net id, Nick = its nickname);
/// the server relays it and back-fills the newcomer with everyone already present (incl. its own).
/// DUMB by design — no sanitization here (the frame-size cap bounds the string; the policy lives in
/// the scene handler). Same length-prefixed scheme as the other app packets.
/// </summary>
public class PlayerInfoPacket : INetworkPacket
{
    public int NetId;
    public string Nick = "";

    public PlayerInfoPacket() { }

    public void Serialize(BinaryWriter w) { w.Write(NetId); w.Write(Nick); }
    public void Deserialize(BinaryReader r) { NetId = r.ReadInt32(); Nick = r.ReadString(); }
}
