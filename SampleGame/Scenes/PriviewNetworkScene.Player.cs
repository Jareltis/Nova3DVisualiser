using Nova3DVisualiser;
using Nova3DVisualiser.AbstractClass;
using Nova3DVisualiser.Implementation;
using Nova3DVisualiser.Interfaces;
using Nova3DVisualiser.Interfaces.modifier;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Network;
using Nova3DVisualiser.Shape;
using Nova3DVisualiser.StaticClass;
using SampleGame.NetworkPackets;
using SampleGame.Physics;
using SampleGame.Textures;
using SampleGame.Worlds;
using System.Globalization;
using System.Text.Json;

namespace SampleGame.Scenes;

public partial class PriviewNetworkScene
{
    // Player controller + camera rig: ray-pick, sphere/OBB/triangle collision resolvers, camera-bubble body collision, walk/jump physics, and the body<->camera offset rig.

    /// <summary>
    /// Returns the index of the editable entry whose instance the ray hits nearest, or -1 for no hit.
    /// Reuses the renderer's own ray-object intersection (IDisplays.GetRenderData) — no new math.
    /// </summary>
    public static int PickNearest(Ray ray, IReadOnlyList<EditEntry> editables)
    {
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < editables.Count; i++)
        {
            if (editables[i].Instance is not IDisplays disp) continue;
            var rd = disp.GetRenderData(ray);
            if (rd.Intersection > -1f && rd.Intersection < bestDist)
            {
                bestDist = rd.Intersection;
                best = i;
            }
        }
        return best;
    }

    // ---- Collision (camera bubble vs scene colliders) ----
    private const float CameraRadius = 0.35f;

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

    // Build the OBB params for a mesh (center / orthonormal axes / world half-extents) from its local
    // bbox + transform, then resolve the sphere against it. Mirrors Object3d's local→world transform.
    private static Vector3 ResolveSphereVsObb(Vector3 c, float r, Object3d o)
    {
        Vector3 rot = o.TotalRotation;
        Vector3 ax = new Vector3(1f, 0f, 0f).Rotate(rot);
        Vector3 ay = new Vector3(0f, 1f, 0f).Rotate(rot);
        Vector3 az = new Vector3(0f, 0f, 1f).Rotate(rot);
        Vector3 center = (o.LocalCenter * o.Scale).Rotate(rot) + o.Position;
        Vector3 half = o.Size * (0.5f * o.Scale);
        return ResolveSphereVsObb(c, r, center, ax, ay, az, half);
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

    // Eject the player BODY's bubble out of every collidable scene object (a couple of passes catch
    // multiple simultaneous contacts). A sphere collider for Sphere, the world AABB for Object3d. The
    // bubble is the body's physical presence (was the camera's, pre-B-camera); the camera derives from it.
    private void ResolveBodyCollision()
    {
        for (int pass = 0; pass < 2; pass++)
            foreach (var e in _editables)
            {
                if (e.Instance is not { Collides: true }) continue;
                if (e.Instance is Sphere s)        _localBody.Position = ResolveSphereVsSphere(_localBody.Position, CameraRadius, s.Position, s.R);
                else if (e.Instance is Object3d o) _localBody.Position = o.Collider == ColliderShape.Obb
                    ? ResolveSphereVsObb(_localBody.Position, CameraRadius, o)
                    : ResolveSphereVsAabb(_localBody.Position, CameraRadius, o.WorldMin, o.WorldMax);
            }
    }

    // ---- Player character controller (only when the world has gravity) ----
    // When gravity is on and fly mode is off, the camera is a walking character: gravity pulls it
    // down, collision rests it on the floor/objects, Space jumps. F1 toggles a free-fly (noclip)
    // mode for building/inspecting, which restores the old Space/C vertical flight. Player physics
    // runs locally for every peer (it only moves _myCamera, never world objects), so it never desyncs.
    private float _playerVelY = 0f;     // camera vertical velocity (gravity + jump)
    private bool _onGround = false;     // camera resting on a surface this frame (gates jumping)
    private bool _flyMode = false;      // F1: free-fly/noclip instead of walking
    private const float JumpSpeed = 6f; // initial upward speed of a jump
    private const float GroundEps = 1e-3f;

    // The player walks (gravity + ground + jump) only in a gravity world with fly mode off.
    private bool PlayerWalking => _world.Physics.GravityEnabled && !_flyMode;

    // One player step: jump on Space (from the ground), apply gravity, then let the camera-bubble
    // collision rest the camera on whatever is beneath it. Landing/headbonk are read from how the
    // collision nudged Y relative to the pre-collision position.
    private void StepPlayerPhysics(float dt)
    {
        if (_onGround && Pressed(ConsoleKey.Spacebar)) _playerVelY = JumpSpeed;   // jump

        _playerVelY -= _world.Physics.GravityStrength * dt;
        _localBody.Position.Y += _playerVelY * dt;

        float yBefore = _localBody.Position.Y;
        ResolveBodyCollision();                // pushes the bubble out of floor + objects (all axes)
        float yAfter = _localBody.Position.Y;

        // Landed: collision lifted us while we were falling -> rest on the surface.
        if (_playerVelY <= 0f && yAfter > yBefore + GroundEps) { _onGround = true; _playerVelY = 0f; }
        else _onGround = false;
        // Bonked a ceiling while rising -> stop the upward velocity.
        if (_playerVelY > 0f && yAfter < yBefore - GroundEps) _playerVelY = 0f;
    }

    // ---- Camera RIG: the camera derives from the body via a per-mode offset (pure math so it is unit-
    // testable). FIRSTPERSON: the camera sits AT the body (its head/eye anchor — the body is at eye level,
    // matching today's feel and the remote-avatar convention). THIRDPERSON: behind + above the body along
    // the yaw look direction. Reserved modes fall back to first-person for now. ----
    public static Vector3 CameraOffsetFor(Vector3 lookRotate, CameraMode mode)
    {
        if (mode == CameraMode.ThirdPerson)
        {
            Vector3 forward = new Vector3(1, 0, 0).Rotate(new Vector3(0, lookRotate.Y, 0));   // yaw-only look
            return forward * (-ThirdPersonBack) + new Vector3(0, ThirdPersonUp, 0);           // behind + above
        }
        return Vector3.Zero;   // first-person (and reserved modes) — camera at the body's eye
    }

    // The camera position for a given body position + look rotation + mode. body + the mode offset.
    public static Vector3 CameraPositionFor(Vector3 bodyPos, Vector3 lookRotate, CameraMode mode)
        => bodyPos + CameraOffsetFor(lookRotate, mode);

    // Places the camera at the body + mode offset, and keeps the avatar facing the look direction (for the
    // 3rd-person view + the streamed transform). Called every frame after movement/physics settle the body.
    private void SyncCameraToBody()
    {
        _localBody.LocalRotate = _myCamera.LocalRotate;                 // avatar faces where we look (matches remote avatars)
        if (_bodyDisplayed) _localBody.UpdateGeometry();               // refresh its world AABB for the GPU cull when drawn
        _myCamera.Position = CameraPositionFor(_localBody.Position, _myCamera.LocalRotate, _cameraMode);
    }

    // Adds/removes the local body avatar from the display so it is drawn ONLY in 3rd person (you never see
    // inside your own body in 1st person). Idempotent.
    private void ApplyCameraMode()
    {
        bool show = _cameraMode == CameraMode.ThirdPerson;
        if (show && !_bodyDisplayed) { AddDisplaysObject(_localBody); _bodyDisplayed = true; }
        else if (!show && _bodyDisplayed) { RemoveDisplaysObject(_localBody); _bodyDisplayed = false; }
    }

    // A player-avatar cube coloured by network id (shared by the local body + every remote peer's avatar),
    // so you and everyone else render the same kind of body. Never collides / never locally simulated.
    private static Object3d CreatePlayerAvatar(int netId)
    {
        Object3d cube = CreateCube();
        Rgba32[] palette = { new Rgba32(255, 0, 0), new Rgba32(0, 255, 0), new Rgba32(0, 0, 255), new Rgba32(255, 255, 0), new Rgba32(0, 255, 255), new Rgba32(255, 0, 255) };
        cube.Color = palette[((netId % palette.Length) + palette.Length) % palette.Length];
        cube.Collides = false;   // an avatar never blocks the local camera (the controller drives the body)
        cube.Gravity = false;    // avatars are driven by the controller / network, never local object gravity
        return cube;
    }

}
