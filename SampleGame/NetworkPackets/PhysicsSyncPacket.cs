using Nova3DVisualiser;
using Nova3DVisualiser.Network;

namespace SampleGame.NetworkPackets;

/// <summary>
/// A compact server→client batch of dynamic-object positions, sent periodically (a few times a
/// second) for objects that physics MOVED since the last batch. Each entry is just an id + world
/// position + vertical velocity, so clients — which never simulate gravity — can see falling and
/// resting objects. The client dead-reckons each entry by its velocity and eases toward the target,
/// so the motion looks smooth between sparse batches (see PriviewNetworkScene.StepInterpolate).
///
/// This deliberately does NOT reuse WorldEditPacket (which carries a full WorldObject JSON per
/// object): physics streams positions every few frames, so it stays lean — fixed 16 bytes per entry.
/// </summary>
public class PhysicsSyncPacket : INetworkPacket
{
    public int[] Ids = System.Array.Empty<int>();           // stable WorldObject.Id per entry
    public Vector3[] Positions = System.Array.Empty<Vector3>();
    public float[] VelY = System.Array.Empty<float>();       // vertical velocity (for client dead-reckoning)
    public Vector3[] Rotations = System.Array.Empty<Vector3>(); // Euler LocalRotate (so peers see physics spin)
    public Vector3[] AngVel = System.Array.Empty<Vector3>();    // angular velocity (rad/s about world X/Y/Z) so the client dead-reckons spin between batches

    public PhysicsSyncPacket() { }

    public void Serialize(BinaryWriter w)
    {
        w.Write(Ids.Length);
        for (int i = 0; i < Ids.Length; i++)
        {
            w.Write(Ids[i]);
            w.Write(Positions[i].X); w.Write(Positions[i].Y); w.Write(Positions[i].Z);
            w.Write(VelY[i]);
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
        VelY = new float[n];
        Rotations = new Vector3[n];
        AngVel = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            Ids[i] = r.ReadInt32();
            Positions[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            VelY[i] = r.ReadSingle();
            Rotations[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            AngVel[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }
    }
}
