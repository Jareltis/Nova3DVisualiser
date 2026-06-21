using Nova3DVisualiser.Network;

namespace SampleGame.NetworkPackets;

/// <summary>
/// A server-authored delta to one editable world object, streamed to viewing clients.
/// Designed for the full live-edit set; THIS pass wires only <see cref="Op"/> == 0 (Modify).
/// Same (de)serialization scheme as the other app packets (length-prefixed strings via
/// BinaryWriter/Reader); the packet type id is derived from the type name by PacketManager.
/// </summary>
public class WorldEditPacket : INetworkPacket
{
    public byte Op;                    // 0 = Modify, 1 = Spawn, 2 = Delete (1/2 reserved for 5b)
    public int Id;                     // target object's stable WorldObject.Id
    public string ObjectJson = "";     // the WorldObject (System.Text.Json) for Modify/Spawn; empty for Delete
    public string MeshName = "";       // 5b: name of a streamed brand-new mesh; empty in this pass
    public string MeshObjText = "";    // 5b: the streamed mesh's .obj text; empty in this pass

    public WorldEditPacket() { }

    public void Serialize(BinaryWriter w)
    {
        w.Write(Op);
        w.Write(Id);
        w.Write(ObjectJson);
        w.Write(MeshName);
        w.Write(MeshObjText);
    }

    public void Deserialize(BinaryReader r)
    {
        Op = r.ReadByte();
        Id = r.ReadInt32();
        ObjectJson = r.ReadString();
        MeshName = r.ReadString();
        MeshObjText = r.ReadString();
    }
}
