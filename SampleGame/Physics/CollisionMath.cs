using Nova3DVisualiser;

namespace SampleGame.Physics;

// Pure collision geometry extracted verbatim from PriviewNetworkScene (zero behaviour change): the
// sphere-vs-AABB/OBB/sphere camera-bubble resolvers, the closest-point-on-triangle, and the full 3D
// box-vs-box SAT. Covered by collisiontest/physicstest; reused by Contact (mesh manifolds) and the scene's
// camera-bubble collision.
public static class CollisionMath
{
    // Push a sphere (c,r) out of an AABB. Returns c unchanged if not penetrating.
    public static Vector3 ResolveSphereVsAabb(Vector3 c, float r, Vector3 min, Vector3 max)
    {
        Vector3 cl = new(Math.Clamp(c.X, min.X, max.X), Math.Clamp(c.Y, min.Y, max.Y), Math.Clamp(c.Z, min.Z, max.Z));
        Vector3 d = c - cl; float d2 = d * d;
        if (d2 >= r * r) return c;
        if (d2 > 1e-8f) { float dist = MathF.Sqrt(d2); return cl + d * (r / dist); }   // outside-ish: push to the surface
        // centre inside the box: eject along the least-penetrating face
        float px1 = c.X - min.X, px2 = max.X - c.X, py1 = c.Y - min.Y, py2 = max.Y - c.Y, pz1 = c.Z - min.Z, pz2 = max.Z - c.Z;
        float m = MathF.Min(MathF.Min(MathF.Min(px1, px2), MathF.Min(py1, py2)), MathF.Min(pz1, pz2));
        if (m == px1) c.X = min.X - r; else if (m == px2) c.X = max.X + r;
        else if (m == py1) c.Y = min.Y - r; else if (m == py2) c.Y = max.Y + r;
        else if (m == pz1) c.Z = min.Z - r; else c.Z = max.Z + r;
        return c;
    }

    // Push a sphere (c,r) out of an ORIENTED box: center + 3 orthonormal axes (ax/ay/az) + per-axis
    // half-extents (half). Same math as ResolveSphereVsAabb but in the box's local frame, so a rotated
    // mesh blocks at its true silhouette instead of an inflated world AABB. Returns c if not penetrating.
    public static Vector3 ResolveSphereVsObb(Vector3 c, float r, Vector3 center, Vector3 ax, Vector3 ay, Vector3 az, Vector3 half)
    {
        Vector3 d = c - center;
        float ex = d * ax, ey = d * ay, ez = d * az;                 // sphere center in the box's local coords
        float qx = Math.Clamp(ex, -half.X, half.X);
        float qy = Math.Clamp(ey, -half.Y, half.Y);
        float qz = Math.Clamp(ez, -half.Z, half.Z);
        Vector3 q = center + ax * qx + ay * qy + az * qz;            // closest point on/in the box
        Vector3 diff = c - q; float d2 = diff * diff;
        if (d2 >= r * r) return c;
        if (d2 > 1e-8f) { float dist = MathF.Sqrt(d2); return q + diff * (r / dist); }   // outside-ish: push to the surface
        // centre inside the box: eject along the least-penetrating local face, keeping the other two axes.
        float dxp = half.X - ex, dxn = ex + half.X, dyp = half.Y - ey, dyn = ey + half.Y, dzp = half.Z - ez, dzn = ez + half.Z;
        float m = MathF.Min(MathF.Min(MathF.Min(dxp, dxn), MathF.Min(dyp, dyn)), MathF.Min(dzp, dzn));
        float nx = ex, ny = ey, nz = ez;
        if (m == dxp) nx = half.X + r; else if (m == dxn) nx = -half.X - r;
        else if (m == dyp) ny = half.Y + r; else if (m == dyn) ny = -half.Y - r;
        else if (m == dzp) nz = half.Z + r; else nz = -half.Z - r;
        return center + ax * nx + ay * ny + az * nz;
    }

    // Push a sphere (c,r) out of another sphere (center,sr).
    public static Vector3 ResolveSphereVsSphere(Vector3 c, float r, Vector3 center, float sr)
    {
        Vector3 d = c - center; float d2 = d * d; float rr = r + sr;
        if (d2 >= rr * rr) return c;
        if (d2 > 1e-8f) { float dist = MathF.Sqrt(d2); return center + d * (rr / dist); }
        return c + new Vector3(0f, rr, 0f);   // coincident: pop up
    }

    // Closest point on triangle (a,b,c) to point p — the classic Voronoi-region method (Ericson, Real-Time
    // Collision Detection). Used so a sphere collides with a mesh's REAL faces, not its bounding box. Pure + tested.
    public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = ab * ap, d2 = ac * ap;                                  // Vector3*Vector3 = dot
        if (d1 <= 0f && d2 <= 0f) return a;                                // vertex region A
        Vector3 bp = p - b;
        float d3 = ab * bp, d4 = ac * bp;
        if (d3 >= 0f && d4 <= d3) return b;                                // vertex region B
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));   // edge AB
        Vector3 cp = p - c;
        float d5 = ab * cp, d6 = ac * cp;
        if (d6 >= 0f && d5 <= d6) return c;                                // vertex region C
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));   // edge AC
        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));   // edge BC
        float denom = 1f / (va + vb + vc);                                 // interior (barycentric)
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    // Full 3D Separating-Axis-Theorem between two oriented boxes A and B (center, 3 orthonormal axes,
    // half-extents). Tests 15 axes — the 3 faces of each box + the 9 edge×edge cross products — and
    // returns overlap + the minimum-penetration UNIT normal (pointing A->B) + the penetration depth, or
    // hit=false at the first separating axis. Near-degenerate cross axes (parallel edges) are skipped;
    // the face axes already cover those. Pure + tested. The impulse box-box manifold uses this normal.
    public static (bool hit, Vector3 normal, float depth) SatBox3D(
        Vector3 cA, Vector3 ax0, Vector3 ax1, Vector3 ax2, Vector3 hA,
        Vector3 cB, Vector3 bx0, Vector3 bx1, Vector3 bx2, Vector3 hB)
    {
        Span<Vector3> axes = stackalloc Vector3[15];
        axes[0] = ax0; axes[1] = ax1; axes[2] = ax2;
        axes[3] = bx0; axes[4] = bx1; axes[5] = bx2;
        Span<Vector3> ea = stackalloc Vector3[3]; ea[0] = ax0; ea[1] = ax1; ea[2] = ax2;
        Span<Vector3> eb = stackalloc Vector3[3]; eb[0] = bx0; eb[1] = bx1; eb[2] = bx2;
        int k = 6;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                axes[k++] = Vector3.Cross(ea[i], eb[j]);

        Vector3 dC = cB - cA;
        float minOv = float.MaxValue; Vector3 best = new Vector3(0f, 1f, 0f);
        for (int a = 0; a < 15; a++)
        {
            Vector3 L = axes[a];
            float len2 = L * L;                                  // Vector3*Vector3 = dot
            if (len2 < 1e-9f) continue;                          // degenerate axis (parallel edges) — faces cover it
            Vector3 Ln = L * (1f / MathF.Sqrt(len2));
            float rA = MathF.Abs(ax0 * Ln) * hA.X + MathF.Abs(ax1 * Ln) * hA.Y + MathF.Abs(ax2 * Ln) * hA.Z;
            float rB = MathF.Abs(bx0 * Ln) * hB.X + MathF.Abs(bx1 * Ln) * hB.Y + MathF.Abs(bx2 * Ln) * hB.Z;
            float dist = dC * Ln;
            float ov = rA + rB - MathF.Abs(dist);
            if (ov <= 0f) return (false, new Vector3(0f, 1f, 0f), 0f);          // separating axis -> no contact
            if (ov < minOv) { minOv = ov; best = dist < 0f ? Ln * -1f : Ln; }   // normal points A->B
        }
        return (true, best, minOv);
    }
}
