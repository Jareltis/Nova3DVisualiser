using Nova3DVisualiser.Network;

namespace SampleGame.NetworkPackets;

/// <summary>
/// A server-authored SETTINGS delta to an already-connected client: the live PlatformConfig (so
/// platform move/shape/size/color/delete/spawn reach a connected client, not just a newly-joining
/// one) plus the GraphicsConfig (carried so future runtime graphics edits ride the same packet).
/// Same length-prefixed-string scheme as the other app packets.
/// </summary>
public class WorldSettingsPacket : INetworkPacket
{
    public string PlatformJson = "";   // PlatformConfig (System.Text.Json)
    public string GraphicsJson = "";   // GraphicsConfig — carried for future runtime graphics edits

    public WorldSettingsPacket() { }

    public void Serialize(BinaryWriter w) { w.Write(PlatformJson); w.Write(GraphicsJson); }
    public void Deserialize(BinaryReader r) { PlatformJson = r.ReadString(); GraphicsJson = r.ReadString(); }
}
