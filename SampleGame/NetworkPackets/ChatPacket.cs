using Nova3DVisualiser.Network;

namespace SampleGame.NetworkPackets;

public class ChatPacket : INetworkPacket
{
    public string Message = "";

    public ChatPacket() { }
    public ChatPacket(string msg) { Message = msg; }

    public void Serialize(BinaryWriter w) => w.Write(Message);
    public void Deserialize(BinaryReader r) => Message = r.ReadString();
}