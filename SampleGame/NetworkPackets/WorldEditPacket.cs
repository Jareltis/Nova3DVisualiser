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
    // Object ops: 0 = Modify, 1 = Spawn, 2 = Delete. Joint ops (C1-5, same wire fields, no format change):
    // 3 = JointModify, 4 = JointSpawn (both carry a JointConfig in ObjectJson), 5 = JointDelete (Id only).
    public byte Op;
    public int Id;                     // target object's WorldObject.Id, or a joint's JointConfig.Id (3/4/5)
    public string ObjectJson = "";     // a WorldObject (0/1) or a JointConfig (3/4), System.Text.Json; empty for Delete (2/5)
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
