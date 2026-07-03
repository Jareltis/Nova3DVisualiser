using Nova3DVisualiser.Network;

namespace SampleGame.NetworkPackets;

/// <summary>
/// One slice of a large texture's raw PNG bytes, streamed to a joining peer so a client that never
/// had the file can render it (PNGs are usually &gt; the inline threshold, so this is the common path).
/// Mirrors <see cref="MeshChunkPacket"/> but with a BINARY payload. The peer reassembles all
/// <see cref="Total"/> parts by <see cref="Index"/>, writes the .png, then loads/decodes it exactly
/// like a locally-present texture. Small textures skip this and inline in the WorldSyncPacket.
/// </summary>
public class TextureChunkPacket : INetworkPacket
{
    public string TextureName = "";
    public int Index;
    public int Total;
    public byte[] Data = Array.Empty<byte>();

    public TextureChunkPacket() { }

    public void Serialize(BinaryWriter w) { w.Write(TextureName); w.Write(Index); w.Write(Total); w.Write(Data.Length); w.Write(Data); }
    public void Deserialize(BinaryReader r) { TextureName = r.ReadString(); Index = r.ReadInt32(); Total = r.ReadInt32(); int len = r.ReadInt32(); Data = r.ReadBytes(len); }
}
