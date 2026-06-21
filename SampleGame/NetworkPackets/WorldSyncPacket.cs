using Nova3DVisualiser.Network;

namespace SampleGame.NetworkPackets;

/// <summary>
/// Carries a whole world to a joining client: the WorldConfig as JSON plus the raw .obj
/// text of every distinct mesh the world references. Follows the same (de)serialization
/// scheme as the other app packets (length-prefixed UTF-8 strings via BinaryWriter/Reader).
/// The packet type id is derived from the type name by PacketManager, like the others.
/// </summary>
public class WorldSyncPacket : INetworkPacket
{
    public string ConfigJson = "";
    public Dictionary<string, string> MeshTexts = new();

    public WorldSyncPacket() { }

    public void Serialize(BinaryWriter w)
    {
        w.Write(ConfigJson);
        w.Write(MeshTexts.Count);
        foreach (var kv in MeshTexts)
        {
            w.Write(kv.Key);
            w.Write(kv.Value);
        }
    }

    public void Deserialize(BinaryReader r)
    {
        ConfigJson = r.ReadString();
        MeshTexts = new Dictionary<string, string>();
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string name = r.ReadString();
            string text = r.ReadString();
            MeshTexts[name] = text;
        }
    }
}
