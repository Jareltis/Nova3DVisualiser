using Nova3DVisualiser.Network;

namespace SampleGame.NetworkPackets;

/// <summary>
/// Server → joining client, over reliable TCP: the per-session UDP token. The client stamps this token on
/// every UDP frame it sends (see NetworkManager.SetLocalUdpToken / BuildUdpFrame); the server drops any
/// incoming UDP datagram whose token it hasn't issued, so an off-path spoofer can't inject fake transforms
/// or physics or poison endpoint learning by forging a senderId. Authenticates the client→server direction
/// only (a client trusts the server it dialed). Not encryption — an on-path MITM is out of scope.
/// </summary>
public class SessionPacket : INetworkPacket
{
    public long Token;

    public SessionPacket() { }

    public void Serialize(BinaryWriter w) { w.Write(Token); }
    public void Deserialize(BinaryReader r) { Token = r.ReadInt64(); }
}
