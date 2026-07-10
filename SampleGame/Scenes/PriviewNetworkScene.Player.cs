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

    // ---- Collision (player CAPSULE vs scene colliders) ----
    // The player is a VERTICAL capsule centred at _localBody.Position: a central segment of half-length
    // CapsuleHalf plus radius CameraRadius, so the total height ≈ 2·CapsuleHalf + 2·CameraRadius ≈ 1.8 (human
    // scale). Position stays the capsule CENTRE — the camera rig derives the eye from it (first-person eye sits
    // AT the centre, i.e. floorTop + CapsuleHalf + CameraRadius ≈ 0.9 when standing, a natural height), so the
    // rig is untouched. Collision reuses the sphere resolvers at the segment point nearest each collider.
    private const float CameraRadius = 0.35f;   // capsule (and legacy bubble) radius
    private const float CapsuleHalf = 0.55f;    // half-length of the capsule's central segment (0 => the old single sphere)

    // Pure sphere-vs-AABB/OBB/sphere resolvers + ClosestPointOnTriangle moved to CollisionMath
    // (SampleGame.Physics). The Object3d-overload below builds this mesh's OBB params then delegates.

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
        return CollisionMath.ResolveSphereVsObb(c, r, center, ax, ay, az, half);
    }

    // Eject the player CAPSULE out of every collidable scene object (a couple of passes catch multiple
    // simultaneous contacts). The capsule is a vertical segment [Position ± (0,CapsuleHalf,0)] of radius
    // CameraRadius. For a CONVEX collider the capsule's closest point lies on that segment, so the exact
    // capsule resolution is: clamp the collider's height into the segment -> run the EXISTING sphere resolver
    // (unchanged) at that point -> translate the whole body by the delta. This gives the player real body
    // height (blocked by head- and foot-height obstacles, can't slip under low overhangs, slides along walls)
    // with no new collision math. With CapsuleHalf == 0 it reduces EXACTLY to the old single-sphere bubble.
    private void ResolveBodyCollision()
    {
        for (int pass = 0; pass < 2; pass++)
            foreach (var e in _editables)
            {
                if (e.Instance is not { Collides: true }) continue;
                Vector3 p = _localBody.Position;
                float segY = Math.Clamp(ColliderCenterY(e.Instance), p.Y - CapsuleHalf, p.Y + CapsuleHalf);
                Vector3 q = new Vector3(p.X, segY, p.Z);   // point on the capsule's central segment nearest this collider's height
                Vector3 qPrime;
                if (e.Instance is Sphere s) qPrime = CollisionMath.ResolveSphereVsSphere(q, CameraRadius, s.Position, s.R);
                else if (e.Instance is Object3d o) qPrime = o.Collider == ColliderShape.Obb
                    ? ResolveSphereVsObb(q, CameraRadius, o)
                    : CollisionMath.ResolveSphereVsAabb(q, CameraRadius, o.WorldMin, o.WorldMax);
                else continue;
                _localBody.Position += qPrime - q;         // translate the body by the ejection delta
            }
    }

    // The representative height of a collider used to clamp the capsule segment: a Sphere's centre, an OBB's
    // centre, else the AABB's mid-height. The sphere resolver at the clamped point still accounts for the
    // collider's FULL extent, so the horizontal push is correct at any clamped Y (a tall wall pushes sideways,
    // a floor clamps to the foot cap and pushes up, a ceiling clamps to the head cap and pushes down).
    private static float ColliderCenterY(GameObject inst)
    {
        if (inst is Sphere s) return s.Position.Y;
        if (inst is Object3d o)
            return o.Collider == ColliderShape.Obb
                ? ((o.LocalCenter * o.Scale).Rotate(o.TotalRotation) + o.Position).Y   // OBB centre (matches ResolveSphereVsObb)
                : (o.WorldMin.Y + o.WorldMax.Y) * 0.5f;
        return inst.Position.Y;
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

    // Camera-rig offset/position math (CameraOffsetFor/CameraPositionFor) + the rig distances moved to
    // CameraMath (SampleGame.Scenes).

    // Places the camera at the body + mode offset, and keeps the avatar facing the look direction (for the
    // 3rd-person view + the streamed transform). Called every frame after movement/physics settle the body.
    private void SyncCameraToBody()
    {
        _localBody.LocalRotate = _myCamera.LocalRotate;                 // avatar faces where we look (matches remote avatars)
        if (_bodyDisplayed) _localBody.UpdateGeometry();               // refresh its world AABB for the GPU cull when drawn
        _myCamera.Position = CameraMath.CameraPositionFor(_localBody.Position, _myCamera.LocalRotate, _cameraMode);
    }

    // Adds/removes the local body avatar from the display so it is drawn in the external body views (3rd +
    // 2nd person) but hidden in 1st person (you never see inside your own body). Idempotent.
    private void ApplyCameraMode()
    {
        bool show = _cameraMode == CameraMode.ThirdPerson || _cameraMode == CameraMode.SecondPerson;
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

    // ---- Spawnable cameras + active-view switching (Plan B) -----------------------------------------
    // A placed "camera" object is a VIEWPOINT you can switch _myCamera to render from. FIXED uses the
    // object's placed position + orientation; FOLLOW uses its placed position but aims at the player body.
    // Which view is active is a LOCAL, per-peer choice (like F7), never world state.

    private static bool IsCamera(EditEntry e) => string.Equals(e.Descriptor.Type, "camera", StringComparison.OrdinalIgnoreCase);

    private static CameraMode ParseCameraKind(string? s) => s?.Trim().ToLowerInvariant() == "follow" ? CameraMode.Follow : CameraMode.Fixed;
    private static string CameraKindToString(CameraMode m) => m == CameraMode.Follow ? "follow" : "fixed";

    // The camera LocalRotate (roll = 0, yaw, pitch) whose CENTER ray points along `dir`. Inverts the
    // engine's actual mapping — Camera.GetRayForUv applies roll(X) then pitch(Z) then yaw(Y) to the +X
    // base ray, giving forward = (cos rz·cos ry, sin rz, -cos rz·sin ry) with ry = LocalRotate.Y (yaw),
    // rz = LocalRotate.Z (pitch). So a placed FOLLOW camera actually looks AT the body. Pure + tested.
    public static Vector3 LookRotationTo(Vector3 dir)
    {
        if (dir.Length() < 1e-6f) return Vector3.Zero;
        Vector3 d = dir.Norm();
        float rz = MathF.Atan2(d.Y, MathF.Sqrt(d.X * d.X + d.Z * d.Z));   // pitch
        float ry = MathF.Atan2(-d.Z, d.X);                               // yaw
        return new Vector3(0f, ry, rz);
    }

    // The (position, look-rotation) a placed camera renders from: FIXED is the placed transform verbatim;
    // FOLLOW keeps the placed position but computes the look so the camera's forward reaches `targetPos`
    // (the tracked object, or the player body). Pure + static so a headless test can verify both.
    public static (Vector3 pos, Vector3 look) PlacedCameraView(CameraMode kind, Vector3 camPos, Vector3 camLook, Vector3 targetPos)
        => kind == CameraMode.Follow ? (camPos, LookRotationTo(targetPos - camPos)) : (camPos, camLook);

    // Resolves a follow camera's aim point: the live position of the editable whose id == followTargetId,
    // or the player body when the id is the sentinel (-1) OR doesn't resolve (target deleted/absent) — so a
    // follow camera never breaks. Fixed cameras ignore this (their look is verbatim).
    private Vector3 FollowTargetPosition(int followTargetId)
    {
        if (followTargetId >= 0)
        {
            var target = _editables.FirstOrDefault(e => e.Descriptor.Id == followTargetId);
            if (target != null) return target.Instance.Position;
        }
        return _localBody.Position;
    }

    // The live entry for the active placed camera, or null when viewing the player body (or it was deleted).
    private EditEntry? ActiveCameraEntry() =>
        _activeCameraId < 0 ? null : _editables.FirstOrDefault(e => IsCamera(e) && e.Descriptor.Id == _activeCameraId);

    // F8: cycle the active view among [player body] + every placed camera (ascending id, stable), wrapping.
    private void CycleActiveView()
    {
        var cams = _editables.Where(IsCamera).OrderBy(e => e.Descriptor.Id).ToList();
        if (cams.Count == 0) { SetActiveView(-1); return; }   // nothing but the body view to show
        int cur = 0;   // 0 = body; 1.. = cams[cur-1]
        if (_activeCameraId >= 0)
        {
            int ci = cams.FindIndex(e => e.Descriptor.Id == _activeCameraId);
            cur = ci < 0 ? 0 : ci + 1;
        }
        int next = (cur + 1) % (cams.Count + 1);
        SetActiveView(next == 0 ? -1 : cams[next - 1].Descriptor.Id);
    }

    // Switches the active view, handling avatar visibility on the transition. The camera you look THROUGH
    // is NOT globally removed: its marker is a closed cube centred exactly on its own camera position, so
    // from that view every face is back-face-culled and the marker is naturally invisible — while staying
    // visible as an object in every OTHER split region (Fix 2: per-region hide, not global). The body avatar
    // is shown so you can see yourself from a placed camera.
    private void SetActiveView(int newId)
    {
        _activeCameraId = newId;
        if (newId >= 0)
            EnsureAvatarShown();   // you see your own avatar from an external camera
        else
            ApplyCameraMode();     // back to the body view: avatar visibility follows the F7 (1st/3rd) mode
    }

    // Idempotently show the local body avatar (used while viewing through a placed camera).
    private void EnsureAvatarShown()
    {
        if (!_bodyDisplayed) { AddDisplaysObject(_localBody); _bodyDisplayed = true; }
    }

    // Sets _myCamera for RENDER from the currently active view. Body view -> SyncCameraToBody (unchanged);
    // a placed camera -> its Fixed transform, or Follow position + look-at(body). Falls back to the body
    // view gracefully if the active camera was deleted. Called every frame after physics settle the body.
    private void ApplyActiveView()
    {
        SyncCameraToBody();                    // body view: body faces the player look; _myCamera = body + offset
        _bodyLook = _myCamera.LocalRotate;     // persist the player's facing so a placed-camera override never leaks into control

        var cam = ActiveCameraEntry();
        if (cam == null)
        {
            // Body view — or the active camera vanished (deleted / world replaced). If we were viewing
            // THROUGH one, fall back cleanly + restore the body-view avatar visibility (per the F7 mode).
            if (_activeCameraId >= 0) { _activeCameraId = -1; ApplyCameraMode(); }
            // External body views (3rd + 2nd person) pull the camera in past obstacles between it and the
            // body (eased glide). 1st person keeps the camera AT the body (SyncCameraToBody), so reset the
            // boom for a clean re-entry. 2nd person additionally looks BACK at the body (render-only; the
            // player's own facing stays in _bodyLook, streamed to peers, so control never rotates).
            if (_cameraMode == CameraMode.ThirdPerson || _cameraMode == CameraMode.SecondPerson)
            {
                _myCamera.Position = BodyOffsetCameraPosition(_localBody.Position, _bodyLook, _cameraMode, GameTime.GetDeltaTime());
                if (_cameraMode == CameraMode.SecondPerson)
                    _myCamera.LocalRotate = LookRotationTo(_localBody.Position - _myCamera.Position);
            }
            else
                _camBackDist = 0f;
            return;
        }

        _camBackDist = 0f;   // a placed-camera view doesn't use the body-view boom
        CameraMode kind = ParseCameraKind(cam.Descriptor.CameraKind);
        Vector3 lookAt = FollowTargetPosition(cam.Descriptor.FollowTargetId);   // the object a Follow camera tracks (Fixed ignores it)
        var (pos, look) = PlacedCameraView(kind, cam.Instance.Position, cam.Instance.LocalRotate, lookAt);
        _myCamera.Position = pos;
        _myCamera.LocalRotate = look;
    }

    // ---- Split-screen viewports (stage 1: SINGLE | 2-way LEFT|RIGHT) --------------------------------
    // The viewports rendered this frame. SINGLE: the base full-screen viewport with the render camera
    // (_myCamera) — byte-identical to before. 2-WAY: the width is split in half; the left region shows the
    // current active view (_myCamera, already resolved by ApplyActiveView), the right region the NEXT active
    // view in the F8 cycle (computed into _splitCamera). Both regions cover the full width so every cell is
    // written each frame (no ghosting). Called once per frame by the screen (after Update settles state).
    public override IReadOnlyList<Viewport> GetViewports(int width, int height)
    {
        // DOCKED-EDIT: render the 3D into the docked layout's CENTRE viewport rect (the four panels fill the
        // rest). A single view this stage — split (F9) applies to the full-screen PLAY/OVERLAY modes.
        if (_hudMode == HudMode.DockedEdit)
        {
            DockRect vp = DockLayout(width, height).Viewport;
            return new[] { new Viewport(vp.X, vp.Y, vp.W, vp.H, _myCamera) };
        }

        if (!_splitScreen)
            return new[] { new Viewport(0, 0, width, height, _myCamera) };   // full screen (== base render camera)

        int leftW = Math.Max(1, width / 2);
        FillSplitCamera(NextActiveViewId(), _splitCamera);
        return new[]
        {
            new Viewport(0, 0, leftW, height, _myCamera),                 // left: the current active view
            new Viewport(leftW, 0, width - leftW, height, _splitCamera),  // right: the next active view
        };
    }

    // The active-view id AFTER the current one in the F8 cycle (body(-1) -> each placed camera (ascending
    // id) -> body). Mirrors CycleActiveView's ordering WITHOUT mutating any state — used to pick the second
    // split region's view. Returns -1 (body) when there are no placed cameras.
    private int NextActiveViewId()
    {
        var cams = _editables.Where(IsCamera).OrderBy(e => e.Descriptor.Id).ToList();
        if (cams.Count == 0) return -1;
        int cur = 0;   // 0 = body; 1.. = cams[cur-1]
        if (_activeCameraId >= 0)
        {
            int ci = cams.FindIndex(e => e.Descriptor.Id == _activeCameraId);
            cur = ci < 0 ? 0 : ci + 1;
        }
        int next = (cur + 1) % (cams.Count + 1);
        return next == 0 ? -1 : cams[next - 1].Descriptor.Id;
    }

    // Fills `into` with the view (position + look + fov) for active-view id `id`, WITHOUT touching the main
    // render camera or its eased-boom / _bodyLook state — for the SECONDARY split region. A placed camera
    // reuses PlacedCameraView (+ its follow target); the body view uses the raw per-mode offset (no
    // clip-avoidance ease, which is stateful and belongs to the primary view). Fov matches the main camera.
    private void FillSplitCamera(int id, Camera into)
    {
        into.Fov = _myCamera.Fov;
        var cam = id < 0 ? null : _editables.FirstOrDefault(e => IsCamera(e) && e.Descriptor.Id == id);
        if (cam != null)
        {
            var (pos, look) = PlacedCameraView(ParseCameraKind(cam.Descriptor.CameraKind),
                cam.Instance.Position, cam.Instance.LocalRotate, FollowTargetPosition(cam.Descriptor.FollowTargetId));
            into.Position = pos;
            into.LocalRotate = look;
        }
        else
        {
            into.Position = CameraMath.CameraPositionFor(_localBody.Position, _bodyLook, _cameraMode);
            into.LocalRotate = _cameraMode == CameraMode.SecondPerson
                ? LookRotationTo(_localBody.Position - into.Position)
                : _bodyLook;
        }
    }

    // ---- Fix 1: the aim crosshair + editing follow the interactive BODY view -------------------------
    // Which split region shows the player's interactive body view: 0 = left (also the single/full-screen
    // view), 1 = right, -1 = neither (both regions are placed cameras → nothing to aim through). Pure
    // decision from the two regions' active-view ids — the left renders the current active view (activeId),
    // the right the next in the F8 cycle (nextId); the body view is whichever id is the -1 sentinel.
    public static int BodyViewRegion(bool split, int activeId, int nextId)
    {
        if (!split) return 0;              // single view: the whole screen is the interactive view
        if (activeId < 0) return 0;        // body view is the LEFT region
        if (nextId < 0) return 1;          // body view is the RIGHT region
        return -1;                         // both regions are placed cameras: no body view on screen
    }

    // The aim crosshair's screen cell for the current layout, or show=false to suppress it. Single view →
    // the full-screen centre (unchanged). Split → the centre of the body-view region (bodyRegion 0/1);
    // suppressed when bodyRegion < 0 (no body view is shown → the user is only monitoring cameras). The left
    // region is [0,width/2), the right [width/2,width), matching GetViewports' split. The cell marks the
    // region centre, which maps to region-relative uv (0,0) — the ray the editor aims (GetRayForUv(Zero)).
    public static (bool show, int x, int y) CrosshairCell(bool split, int width, int height, int bodyRegion)
        => CrosshairCell(split, 0, 0, width, height, bodyRegion);

    // As above but within an arbitrary 3D-AREA rect [areaX,areaY,areaW,areaH] (the full screen normally, or
    // the docked-editor centre viewport), so the reticle centres on the view region — not the whole screen.
    public static (bool show, int x, int y) CrosshairCell(bool split, int areaX, int areaY, int areaW, int areaH, int bodyRegion)
    {
        if (!split) return (true, areaX + areaW / 2, areaY + areaH / 2);
        if (bodyRegion < 0) return (false, 0, 0);
        int leftW = Math.Max(1, areaW / 2);
        int cx = bodyRegion == 0 ? areaX + leftW / 2 : areaX + leftW + (areaW - leftW) / 2;
        return (true, cx, areaY + areaH / 2);
    }

    // The camera the editor aims THROUGH (pick/spawn) + whether the body view is on screen to aim with.
    // Single view → the render camera (unchanged, even if it is a placed camera). Split → the camera of
    // whichever region shows the player's body view: LEFT is _myCamera (active view), RIGHT is the body
    // camera (freshly filled so it is not a frame stale). bodyVisible is false when NEITHER region shows the
    // body (both placed cameras) so pick is suppressed; the returned camera is still body-facing for spawn.
    private (bool bodyVisible, Camera cam) BodyAimCamera()
    {
        if (!_splitScreen) return (true, _myCamera);
        int region = BodyViewRegion(true, _activeCameraId, NextActiveViewId());
        if (region == 0) return (true, _myCamera);   // body view == the left region (_myCamera; active is -1)
        FillSplitCamera(-1, _splitCamera);           // build the body-view camera (GetViewports re-fills it before render)
        return (region == 1, _splitCamera);
    }

    // Human-readable label for the active view (overlay): "1st-person"/"3rd-person" or "camera #id (Fixed|Follow)".
    private string ViewLabel()
    {
        var cam = ActiveCameraEntry();
        if (cam == null)
            return _cameraMode switch
            {
                CameraMode.ThirdPerson => "3rd-person",
                CameraMode.SecondPerson => "2nd-person",
                _ => "1st-person",
            };
        return $"camera #{cam.Descriptor.Id} ({(ParseCameraKind(cam.Descriptor.CameraKind) == CameraMode.Follow ? "Follow" : "Fixed")})";
    }

    // ---- 3rd-person clip-avoidance: pull the body camera IN past walls so it can't see through them ----
    private const float CamClipMargin = 0.3f;    // sit the camera this far IN FRONT of the obstacle it hits
    private const float CamClipMinDist = 0.6f;   // never pull the boom closer than this (avoid inside/behind the body)
    private const float CamClipEaseRate = 10f;   // ease speed of the boom length toward the raycast-limited target (1/sec)
    private float _camBackDist;                   // smoothed boom length (world units along the 3rd-person offset dir)

    // Pure + testable: the target 3rd-person boom length given the full offset and the nearest obstacle hit
    // along the body->camera ray. A hit closer than the full boom pulls the camera to `margin` in front of
    // it, floored at `minDist`; otherwise the full boom is used (nothing in the way). The boom is a
    // magnitude along the offset direction — the caller places the camera at body + (offset/|offset|)*this.
    public static float ResolveCameraBackDistance(Vector3 offset, float nearestHit, float minDist, float margin)
    {
        float d = offset.Length();
        if (nearestHit >= 0f && nearestHit < d) return MathF.Max(minDist, nearestHit - margin);
        return d;
    }

    // Nearest solid-geometry hit distance along a ray, or float.MaxValue when nothing blocks. Reuses the
    // renderer's own ray-object intersection (like PickNearest) over the EDITABLE objects — which excludes
    // the player body + remote avatars (never in _editables), so the camera can't block on itself — and
    // skips visual-only markers (lights/cameras) so they don't shove the camera. `dir` must be unit length.
    private float NearestObstacleAlongRay(Vector3 origin, Vector3 dir)
    {
        var ray = new Ray(origin, dir);
        float nearest = float.MaxValue;
        foreach (var e in _editables)
        {
            if (e.Light != null || IsCamera(e)) continue;   // visual-only markers never block the camera
            if (e.Instance is not IDisplays disp) continue;
            var rd = disp.GetRenderData(ray);
            if (rd.Intersection > 1e-3f && rd.Intersection < nearest) nearest = rd.Intersection;   // ignore a hit at the origin itself
        }
        return nearest;
    }

    // The eased external-body-view camera position (3rd OR 2nd person): cast body->desired-camera along the
    // mode's offset, pull the boom in past any obstacle (ResolveCameraBackDistance), then glide the boom
    // length toward that target (framerate-aware, never overshooting past the wall). 1st person has a zero
    // offset (handled by the caller), so this is only ever called for the offset body views.
    private Vector3 BodyOffsetCameraPosition(Vector3 body, Vector3 look, CameraMode mode, float dt)
    {
        Vector3 off = CameraMath.CameraOffsetFor(look, mode);
        float d = off.Length();
        if (d < 1e-5f) { _camBackDist = 0f; return body; }   // degenerate (never for 3rd/2nd person)
        Vector3 dir = off / d;
        float target = ResolveCameraBackDistance(off, NearestObstacleAlongRay(body, dir), CamClipMinDist, CamClipMargin);
        if (_camBackDist <= 0f) _camBackDist = d;            // (re)enter the view fully extended, then glide in if obstructed
        _camBackDist += (target - _camBackDist) * Math.Clamp(CamClipEaseRate * dt, 0f, 1f);   // exponential ease — convex, so it never overshoots past `target`
        return body + dir * _camBackDist;
    }

}
