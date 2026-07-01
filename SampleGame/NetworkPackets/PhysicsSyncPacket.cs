using Nova3DVisualiser;
using Nova3DVisualiser.Network;

namespace SampleGame.NetworkPackets;

/// <summary>
/// A compact server→client batch of dynamic RigidBody state, sent periodically (a few times a second)
/// for objects that physics MOVED since the last batch. Each entry is an id + full pose (world position +
/// Euler orientation) + full velocity (linear + angular), so clients — which never simulate — can see
/// falling, rolling and tumbling objects. The client DEAD-RECKONS each entry by its velocity (advance the
/// pose by LinVel·dt / AngVel·dt) and eases toward the target, so motion looks smooth between sparse
/// batches (see PriviewNetworkScene.StepInterpolate / StepNetworkPhysics).
///
/// This deliberately does NOT reuse WorldEditPacket (which carries a full WorldObject JSON per object):
/// physics streams state every few frames, so it stays lean — fixed 40 bytes per entry.
/// </summary>
public class PhysicsSyncPacket : INetworkPacket
{
    public int[] Ids = System.Array.Empty<int>();           // stable WorldObject.Id per entry
    public Vector3[] Positions = System.Array.Empty<Vector3>();
    public Vector3[] LinVel = System.Array.Empty<Vector3>();     // FULL linear velocity (X/Y/Z) — the impulse solver rolls/tumbles in every direction, so the client dead-reckons all 3 axes
    public Vector3[] Rotations = System.Array.Empty<Vector3>();  // Euler LocalRotate orientation (so peers see physics spin)
    public Vector3[] AngVel = System.Array.Empty<Vector3>();     // angular velocity (rad/s about world X/Y/Z) so the client dead-reckons spin between batches

    public PhysicsSyncPacket() { }

    public void Serialize(BinaryWriter w)
    {
        w.Write(Ids.Length);
        for (int i = 0; i < Ids.Length; i++)
        {
            w.Write(Ids[i]);
            w.Write(Positions[i].X); w.Write(Positions[i].Y); w.Write(Positions[i].Z);
            w.Write(LinVel[i].X); w.Write(LinVel[i].Y); w.Write(LinVel[i].Z);
            w.Write(Rotations[i].X); w.Write(Rotations[i].Y); w.Write(Rotations[i].Z);
            w.Write(AngVel[i].X); w.Write(AngVel[i].Y); w.Write(AngVel[i].Z);
        }
    }

    public void Deserialize(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0) n = 0;
        Ids = new int[n];
        Positions = new Vector3[n];
        LinVel = new Vector3[n];
        Rotations = new Vector3[n];
        AngVel = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            Ids[i] = r.ReadInt32();
            Positions[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            LinVel[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            Rotations[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            AngVel[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }
    }
}
