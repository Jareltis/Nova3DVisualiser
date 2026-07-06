using Nova3DVisualiser.Network;

namespace SampleGame.NetworkPackets;

/// <summary>
/// One slice of a large spawned mesh's .obj text, streamed a few per frame so a multi-MB spawn
/// doesn't block real-time packets (avatars/edits) behind it. The client reassembles all
/// <see cref="Total"/> parts by <see cref="Index"/>, writes the mesh, then the trailing spawn
/// WorldEditPacket (with empty MeshObjText) builds it from disk. Small meshes skip this and inline.
/// </summary>
public class MeshChunkPacket : INetworkPacket
{
    public string MeshName = "";
    public int Index;
    public int Total;
    public string Data = "";

    public MeshChunkPacket() { }

    public void Serialize(BinaryWriter w) { w.Write(MeshName); w.Write(Index); w.Write(Total); w.Write(Data); }
    // NOTE (S1): the fields are ReadString (a BinaryReader 7-bit length prefix) — already bounded by the
    // ReadLoop frame cap (NetLimits.MaxFrameBytes), since the reader can never read past the framed payload.
    // So no explicit count/len guard is needed here (unlike the ReadBytes(len)/allocate-by-count packets).
    public void Deserialize(BinaryReader r) { MeshName = r.ReadString(); Index = r.ReadInt32(); Total = r.ReadInt32(); Data = r.ReadString(); }
}
