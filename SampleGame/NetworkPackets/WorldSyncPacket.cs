using Nova3DVisualiser.Network;

namespace SampleGame.NetworkPackets;

/// <summary>
/// Carries a whole world to a joining client: the WorldConfig as JSON, the raw .obj text of every
/// distinct mesh the world references, and the raw PNG BYTES of every small texture it references
/// (large textures stream separately via <see cref="TextureChunkPacket"/>). Follows the same
/// (de)serialization scheme as the other app packets (length-prefixed strings via BinaryWriter/Reader,
/// plus an int length + raw bytes for each binary texture). The packet type id is derived from the
/// type name by PacketManager, like the others.
/// </summary>
public class WorldSyncPacket : INetworkPacket
{
    public string ConfigJson = "";
    public Dictionary<string, string> MeshTexts = new();
    public Dictionary<string, byte[]> TextureData = new();   // texture file name -> raw PNG bytes (inlined; small only)

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
        w.Write(TextureData.Count);
        foreach (var kv in TextureData)
        {
            w.Write(kv.Key);
            w.Write(kv.Value.Length);
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
        TextureData = new Dictionary<string, byte[]>();
        int texCount = r.ReadInt32();
        for (int i = 0; i < texCount; i++)
        {
            string name = r.ReadString();
            int len = r.ReadInt32();
            byte[] bytes = r.ReadBytes(len);
            TextureData[name] = bytes;
        }
    }
}
