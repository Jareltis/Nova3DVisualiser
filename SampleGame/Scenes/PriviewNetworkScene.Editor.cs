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
    // In-scene editor actions: input handling, field adjustment, spawn/delete, floor/marker rebuilds, save + live-world build, and the key edge-detector.

    // ---- Editor input (only called in edit mode, never while chatting) ----
    private void HandleEditorInput()
    {
        // Enter enters inline TYPED entry on the active EDITABLE field: the Name field takes text (as
        // before), a NUMERIC field takes a typed value (parsed + clamped on confirm). Enum/toggle fields
        // (Collider/Gravity/Collides/Kind/Shape/Texture/TexFace/TexFilter…) aren't typeable — Enter is
        // inert there (use N/M to cycle). Spawn moved off Enter to [B] so it always works. Checked first so
        // Enter takes the editable field before anything else reads it.
        if (_selected >= 0 && _selected < _editables.Count && Pressed(ConsoleKey.Enter))
        {
            var entry = _editables[_selected];
            var (af, onHeader, section) = ActiveInspectorTarget(entry);
            // DOCKED: Enter on a section HEADER collapses/expands it (a LOCAL view action — clients too).
            if (onHeader) ToggleSection(section);
            // Enter on a typeable FIELD (Name/numeric) begins inline entry (authority only). Enum/toggle
            // fields aren't typeable — Enter is inert there (use N/M). Checked first so Enter is consumed.
            else if (CanEdit && af is Field f && (f == Field.Name || IsNumericField(f))) { BeginFieldEntry(entry, f); return; }
        }

        // Cycle the spawn type.
        if (Pressed(ConsoleKey.G) && _spawnTypes.Count > 0)
            _spawnIndex = (_spawnIndex + 1) % _spawnTypes.Count;

        // Spawn the current type a couple of units in front of the camera, at ground level. Relocated off
        // Enter (now the field-edit trigger) to [B]. (A view-only client cannot mutate the synced world.)
        if (CanEdit && Pressed(ConsoleKey.B))
            SpawnCurrent();

        // Aim-to-select: pick the editable object under the crosshair — cast from the BODY-view camera
        // through the crosshair (region centre → uv (0,0)). Suppressed when no region shows the body view.
        if (Pressed(ConsoleKey.F))
        {
            var (bodyVisible, aimCam) = BodyAimCamera();
            if (bodyVisible)
            {
                int hit = PickNearest(aimCam.GetRayForUv(Vector2.Zero), _editables);
                if (hit >= 0) { _selected = hit; _fieldIndex = 0; }
            }
        }

        // Cycle the selection (wrap around); reset the field cursor on a new selection.
        if (_editables.Count > 0)
        {
            if (Pressed(ConsoleKey.Oem4)) // '['
            { _selected = (_selected <= 0 ? _editables.Count : _selected) - 1; _fieldIndex = 0; }
            if (Pressed(ConsoleKey.Oem6)) // ']'
            { _selected = (_selected + 1) % _editables.Count; _fieldIndex = 0; }
        }

        if (_selected >= 0 && _selected < _editables.Count)
        {
            // Field cursor (,/.) — open to clients (inspect). DOCKED navigates the section headers + the
            // currently-VISIBLE fields (collapsed sections hide their fields, so the cursor SKIPS them);
            // OVERLAY navigates the flat field list. `_fieldIndex` is the cursor in BOTH (row index vs field index).
            var entry = _editables[_selected];
            var fields = FieldsFor(entry);
            int cursorLen = _hudMode == HudMode.DockedEdit
                ? BuildInspectorRows(SectionsOf(fields), _collapsedSections).Count
                : fields.Length;
            if (Pressed(ConsoleKey.OemComma))  _fieldIndex = (_fieldIndex - 1 + cursorLen) % cursorLen;
            if (Pressed(ConsoleKey.OemPeriod)) _fieldIndex = (_fieldIndex + 1) % cursorLen;
            _fieldIndex = Math.Clamp(_fieldIndex, 0, cursorLen - 1);

            // World-mutating actions are authority-only; a client never edits the synced world.
            if (CanEdit)
            {
                // Move the selected object by a fixed step per press (world axes); fast nudge.
                var delta = Vector3.Zero;
                if (Pressed(ConsoleKey.L)) delta.X += MoveStep;
                if (Pressed(ConsoleKey.J)) delta.X -= MoveStep;
                if (Pressed(ConsoleKey.I)) delta.Z += MoveStep;
                if (Pressed(ConsoleKey.K)) delta.Z -= MoveStep;
                if (Pressed(ConsoleKey.U)) delta.Y += MoveStep;
                if (Pressed(ConsoleKey.O)) delta.Y -= MoveStep;

                if (delta.X != 0f || delta.Y != 0f || delta.Z != 0f)
                {
                    var inst = entry.Instance;
                    inst.Position += delta;
                    if (inst is Object3d o) o.UpdateGeometry();
                    SyncLightToMarker(entry);   // a light's Light follows its marker
                    _editDirty = true;
                }

                // Adjust the active field (N/M) — covers pos/rot/scale/spin/color/radius/power. In DOCKED,
                // when the cursor is on a section HEADER there is no field to step, so N/M do nothing.
                int dir = 0;
                if (Pressed(ConsoleKey.N)) dir = -1;
                if (Pressed(ConsoleKey.M)) dir = +1;
                if (dir != 0)
                {
                    var (af, _, _) = ActiveInspectorTarget(entry);
                    if (af is Field f) { AdjustField(f, entry, dir); _editDirty = true; }
                }

                // Delete the selected object (the platform deletes by disabling — see DeleteSelected).
                if (Pressed(ConsoleKey.Delete)) DeleteSelected();
            }
        }

        // Runtime graphics settings (shadows/BVH/camera-light/platform) — authority only, live-synced.
        if (CanEdit) HandleGraphicsToggles();

        // Save the arrangement back into the world JSON (authority only).
        if (CanEdit && Pressed(ConsoleKey.F5))
            SaveWorld();
    }

    // Applies a single decrease/increase (dir = -1/+1) to the active field on the selected entry.
    private void AdjustField(Field f, EditEntry entry, int dir)
    {
        var inst = entry.Instance;
        var o = inst as Object3d;
        switch (f)
        {
            case Field.PosX: inst.Position.X += dir * MoveStep; o?.UpdateGeometry(); SyncLightToMarker(entry); break;
            case Field.PosY: inst.Position.Y += dir * MoveStep; o?.UpdateGeometry(); SyncLightToMarker(entry); break;
            case Field.PosZ: inst.Position.Z += dir * MoveStep; o?.UpdateGeometry(); SyncLightToMarker(entry); break;
            case Field.RotX: inst.LocalRotate.X += dir * RotStep; o?.UpdateGeometry(); break;
            case Field.RotY: inst.LocalRotate.Y += dir * RotStep; o?.UpdateGeometry(); break;
            case Field.RotZ: inst.LocalRotate.Z += dir * RotStep; o?.UpdateGeometry(); break;
            case Field.Scale:
                if (o != null) { o.Scale = MathF.Max(0.01f, o.Scale + dir * ScaleStep); o.UpdateGeometry(); }
                break;
            case Field.RotateSpeed:
                if (o != null) o.RotateSpeed += dir * SpinStep;
                break;
            case Field.ColorR: inst.Color = new Rgba32((byte)Math.Clamp(inst.Color.R + dir * ColorStep, 0, 255), inst.Color.G, inst.Color.B, inst.Color.A); SyncColorDerived(entry); break;
            case Field.ColorG: inst.Color = new Rgba32(inst.Color.R, (byte)Math.Clamp(inst.Color.G + dir * ColorStep, 0, 255), inst.Color.B, inst.Color.A); SyncColorDerived(entry); break;
            case Field.ColorB: inst.Color = new Rgba32(inst.Color.R, inst.Color.G, (byte)Math.Clamp(inst.Color.B + dir * ColorStep, 0, 255), inst.Color.A); SyncColorDerived(entry); break;
            case Field.ColorA: inst.Color = new Rgba32(inst.Color.R, inst.Color.G, inst.Color.B, (byte)Math.Clamp(inst.Color.A + dir * ColorStep, 0, 255)); SyncColorDerived(entry); break;
            case Field.Radius:
                if (inst is Sphere s) s.R = MathF.Max(0.01f, s.R + dir * RadiusStep);
                break;
            case Field.Collides:
                if (!_world.Physics.CollisionEnabled) break;                          // world collision off -> locked off
                inst.Collides = !inst.Collides;                                       // N or M toggles it
                if (entry.Platform != null) entry.Platform.Collides = inst.Collides;  // platform: persist on the live config
                break;
            case Field.Gravity:
                if (!_world.Physics.GravityEnabled) break;                            // world gravity off -> locked off
                inst.Gravity = !inst.Gravity;                                         // N or M toggles it
                if (!inst.Gravity) { _impBodies.Remove(inst); _hullScale.Remove(inst); }   // stop simulating it once it can't fall
                if (entry.Platform != null) entry.Platform.Gravity = inst.Gravity;    // platform: persist on the live config
                break;
            case Field.Collider:
                if (!_world.Physics.CollisionEnabled) break;                          // world collision off -> shape moot
                inst.Collider = inst.Collider == ColliderShape.Obb ? ColliderShape.Aabb : ColliderShape.Obb;  // N or M toggles AABB<->OBB
                break;
            case Field.Mass:
                inst.Mass = MathF.Max(MassMin, inst.Mass + dir * MassStep);
                break;
            case Field.Restitution:
                // Cycle inherit(<0) <-> explicit 0..1: from "inherit", M sets 0.0 (explicit); stepping an
                // explicit value below 0 returns to "inherit world", above 1 clamps. (Bouncy = closer to 1.)
                if (inst.Restitution < 0f) inst.Restitution = dir > 0 ? 0f : -1f;
                else { float nv = inst.Restitution + dir * RestStep; inst.Restitution = nv < 0f ? -1f : MathF.Min(1f, nv); }
                break;
            case Field.Friction:
                inst.Friction = Math.Clamp(inst.Friction + dir * FrictionStep, 0f, FrictionMax);
                break;
            case Field.RollingFriction:
                inst.RollingFriction = Math.Clamp(inst.RollingFriction + dir * RollFrictionStep, 0f, 1f);
                break;
            case Field.ColorFade:
                inst.ColorFade = Math.Clamp(inst.ColorFade + dir * ColorFadeStep, 0f, 1f);   // 0 = true colour, 1 = washed to white
                if (entry.Light != null) entry.Light.ColorFade = inst.ColorFade;             // pale the emitted colour too
                break;
            case Field.Texture:
            {
                // Cycle "none" + every PNG in textures/. Set via the decode-once cache (null = none). A live
                // swap re-uploads ONLY the texture pool (A3) — the static geometry + BVH stay cached.
                var opts = new List<string> { "" };
                opts.AddRange(WorldManager.ListAvailableTextures(_texturesFolder));
                string cur = inst.Texture?.Name ?? "";
                int idx = opts.FindIndex(t => string.Equals(t, cur, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) idx = 0;
                int n = opts.Count;
                string next = opts[(((idx + dir) % n) + n) % n];
                inst.Texture = string.IsNullOrEmpty(next) ? null : TextureLoader.Get(_texturesFolder, next);
                InvalidateGpuTextures();
                // A1 tail: a peer may lack this PNG — stream its bytes NOW (before the Modify broadcast that
                // this edit dirties, later in Update). TCP FIFO ⇒ the peer materializes it before applying
                // the edit that names it. No-op when not the online authority or cycling to "none".
                StreamTextureToPeers(next);
                break;
            }
            case Field.TextureScale:
                inst.TextureScale = Math.Clamp(inst.TextureScale + dir * TextureScaleStep, 0.1f, 10f);
                break;
            case Field.TextureFace:
            {
                // Cycle "All" (-1) then each face option (a cube: +X..-Z); other shapes have only "All".
                int total = TextureFaceOptions(entry.Descriptor.Type).Length + 1;
                int slot = (((inst.TextureFace + 1 + dir) % total) + total) % total;
                inst.TextureFace = slot - 1;
                break;
            }
            case Field.TextureFilter:
                // Cycle Nearest -> Bilinear -> Mipmapped (N steps back). Rides in the per-frame SnapObject,
                // and the mip chain is already resident in the (geometry-versioned) texture pool, so NO GPU
                // re-upload is needed on a live switch.
                inst.TextureFilter = (TextureFilterMode)(((((int)inst.TextureFilter + dir) % 3) + 3) % 3);
                break;
            case Field.Power:
                if (entry.Light != null) entry.Light.LightPower = MathF.Max(0f, entry.Light.LightPower + dir * PowerStep);
                break;
            case Field.ClrInf:
                if (entry.Light != null)
                    entry.Light.ColorInfluence = Math.Clamp(entry.Light.ColorInfluence + dir * InfluenceStep, 0f, 1f);
                break;
            case Field.Kind:
                if (entry.Light != null)
                {
                    int n = Enum.GetValues<LightKind>().Length;
                    entry.Light.Kind = (LightKind)((((int)entry.Light.Kind + dir) % n + n) % n);
                    // Morph the marker mesh (cube <-> cone <-> shaped panel) to match the new kind.
                    ReplaceMarker(entry, BuildLightMarker(entry.Light.Kind, entry.Light.Direction, entry.Light.AreaSize, entry.Light.ConeShape, entry.Light.ConeAngleDeg, entry.Light.AreaShape, entry.Light.BeamCount));
                }
                break;
            case Field.DirX: AdjustDirection(entry, new Vector3(dir * DirStep, 0f, 0f)); break;
            case Field.DirY: AdjustDirection(entry, new Vector3(0f, dir * DirStep, 0f)); break;
            case Field.DirZ: AdjustDirection(entry, new Vector3(0f, 0f, dir * DirStep)); break;
            case Field.ConeAngle:
                if (entry.Light != null)
                {
                    entry.Light.ConeAngleDeg = Math.Clamp(entry.Light.ConeAngleDeg + dir * ConeStep, 1f, 89f);
                    ReplaceMarker(entry, BuildLightMarker(entry.Light.Kind, entry.Light.Direction, entry.Light.AreaSize, entry.Light.ConeShape, entry.Light.ConeAngleDeg, entry.Light.AreaShape, entry.Light.BeamCount));
                }
                break;
            case Field.AreaSize:
                if (entry.Light != null)
                {
                    entry.Light.AreaSize = MathF.Max(0.05f, entry.Light.AreaSize + dir * AreaStep);
                    if (entry.Light.Kind == LightKind.Area && entry.Instance is Object3d am)
                    { am.Scale = entry.Light.AreaSize; am.UpdateGeometry(); }   // shaped marker tracks the area size
                }
                break;
            case Field.AreaShape:
                if (entry.Light != null)
                {
                    int n = Enum.GetValues<ConeShapeKind>().Length;
                    entry.Light.AreaShape = (ConeShapeKind)((((int)entry.Light.AreaShape + dir) % n + n) % n);
                    ReplaceMarker(entry, BuildLightMarker(entry.Light.Kind, entry.Light.Direction, entry.Light.AreaSize,
                        entry.Light.ConeShape, entry.Light.ConeAngleDeg, entry.Light.AreaShape, entry.Light.BeamCount));
                }
                break;
            case Field.Spin:
                if (entry.Light != null) entry.Light.SpinSpeed += dir * LightSpinStep;
                break;
            case Field.Beams:
                if (entry.Light != null)
                {
                    entry.Light.BeamCount = Math.Clamp(entry.Light.BeamCount + dir, 1, 8);
                    // Always rebuild: this swaps mesh TYPE both ways (1 -> baked fan, many -> single cone).
                    ReplaceMarker(entry, BuildLightMarker(entry.Light.Kind, entry.Light.Direction, entry.Light.AreaSize,
                        entry.Light.ConeShape, entry.Light.ConeAngleDeg, entry.Light.AreaShape, entry.Light.BeamCount));
                }
                break;
            case Field.Shape:
                if (entry.Light != null)
                {
                    int n = Enum.GetValues<ConeShapeKind>().Length;
                    entry.Light.ConeShape = (ConeShapeKind)((((int)entry.Light.ConeShape + dir) % n + n) % n);
                    ReplaceMarker(entry, BuildLightMarker(entry.Light.Kind, entry.Light.Direction, entry.Light.AreaSize, entry.Light.ConeShape, entry.Light.ConeAngleDeg, entry.Light.AreaShape, entry.Light.BeamCount));
                }
                break;
            case Field.PlatShape:
                if (entry.Platform != null)
                {
                    int idx = Array.IndexOf(PlatformShapes, entry.Platform.Shape?.Trim().ToLowerInvariant());
                    if (idx < 0) idx = 0;
                    int n = PlatformShapes.Length;
                    entry.Platform.Shape = PlatformShapes[(((idx + dir) % n) + n) % n];
                    RebuildFloor(entry);
                }
                break;
            case Field.PlatSize:
                if (entry.Platform != null) { entry.Platform.Size = MathF.Max(PlatformMin, entry.Platform.Size + dir * PlatformStep); RebuildFloor(entry); }
                break;
            case Field.PlatWidth:
                if (entry.Platform != null) { entry.Platform.Width = MathF.Max(PlatformMin, entry.Platform.Width + dir * PlatformStep); RebuildFloor(entry); }
                break;
            case Field.PlatDepth:
                if (entry.Platform != null) { entry.Platform.Depth = MathF.Max(PlatformMin, entry.Platform.Depth + dir * PlatformStep); RebuildFloor(entry); }
                break;
            case Field.CamKind:
                // Cycle Fixed <-> Follow (two values, so N and M both toggle). A camera has no engine
                // companion — its kind lives on the descriptor, which rides FromInstance/save/sync.
                entry.Descriptor.CameraKind = CameraKindToString(
                    ParseCameraKind(entry.Descriptor.CameraKind) == CameraMode.Fixed ? CameraMode.Follow : CameraMode.Fixed);
                break;
            case Field.FollowTargetId:
                // Step the follow-target id, floored at -1 (the player-body sentinel). N/M nudges; typed
                // entry sets it exactly. Lives on the descriptor like CamKind (rides save/sync/FromInstance).
                entry.Descriptor.FollowTargetId = Math.Max(-1, entry.Descriptor.FollowTargetId + dir);
                break;

            // ---- joint fields (C1-4b): all mutate the LIVE JointConfig in _world.Joints ----
            case Field.JointKind:
                if (entry.Joint != null)
                {
                    entry.Joint.Kind = NextJointKind(entry.Joint.Kind, dir);   // ballsocket -> hinge -> distance -> …
                    _fieldIndex = 0;   // the field set changed (like changing a light's Kind)
                }
                break;
            // Body ids reference LIVE runtime object ids; floored at -1 (= the static world), like FollowTargetId.
            case Field.JBodyA: if (entry.Joint != null) entry.Joint.BodyA = Math.Max(-1, entry.Joint.BodyA + dir); break;
            case Field.JBodyB: if (entry.Joint != null) entry.Joint.BodyB = Math.Max(-1, entry.Joint.BodyB + dir); break;
            case Field.JAnchorAX: if (entry.Joint != null) entry.Joint.AnchorA.X += dir * MoveStep; break;
            case Field.JAnchorAY: if (entry.Joint != null) entry.Joint.AnchorA.Y += dir * MoveStep; break;
            case Field.JAnchorAZ: if (entry.Joint != null) entry.Joint.AnchorA.Z += dir * MoveStep; break;
            case Field.JAnchorBX: if (entry.Joint != null) entry.Joint.AnchorB.X += dir * MoveStep; break;
            case Field.JAnchorBY: if (entry.Joint != null) entry.Joint.AnchorB.Y += dir * MoveStep; break;
            case Field.JAnchorBZ: if (entry.Joint != null) entry.Joint.AnchorB.Z += dir * MoveStep; break;
            case Field.JAxisX: if (entry.Joint != null) entry.Joint.Axis.X += dir * MoveStep; break;
            case Field.JAxisY: if (entry.Joint != null) entry.Joint.Axis.Y += dir * MoveStep; break;
            case Field.JAxisZ: if (entry.Joint != null) entry.Joint.Axis.Z += dir * MoveStep; break;
            case Field.JLimitEnabled: if (entry.Joint != null) entry.Joint.LimitEnabled = !entry.Joint.LimitEnabled; break;   // N or M toggles
            case Field.JLower: if (entry.Joint != null) entry.Joint.LowerLimit += dir * RotStep; break;
            case Field.JUpper: if (entry.Joint != null) entry.Joint.UpperLimit += dir * RotStep; break;
            case Field.JMotorEnabled: if (entry.Joint != null) entry.Joint.MotorEnabled = !entry.Joint.MotorEnabled; break;
            case Field.JMotorSpeed: if (entry.Joint != null) entry.Joint.MotorTargetSpeed += dir * JointSpeedStep; break;
            case Field.JMaxTorque: if (entry.Joint != null) entry.Joint.MaxMotorTorque = MathF.Max(0f, entry.Joint.MaxMotorTorque + dir * JointTorqueStep); break;
            case Field.JRestLength: if (entry.Joint != null) entry.Joint.RestLength = MathF.Max(0f, entry.Joint.RestLength + dir * JointLengthStep); break;
            case Field.JSpringEnabled: if (entry.Joint != null) entry.Joint.SpringEnabled = !entry.Joint.SpringEnabled; break;
            case Field.JFrequency: if (entry.Joint != null) entry.Joint.Frequency = MathF.Max(0f, entry.Joint.Frequency + dir * JointFreqStep); break;
            case Field.JDamping: if (entry.Joint != null) entry.Joint.DampingRatio = MathF.Max(0f, entry.Joint.DampingRatio + dir * JointDampStep); break;
        }
    }

    // Cycles a joint Kind ballsocket -> hinge -> distance -> ballsocket (N = forward, M = back).
    private static readonly string[] JointKinds = { "ballsocket", "hinge", "distance" };
    private static string NextJointKind(string? kind, int dir)
    {
        int idx = Array.IndexOf(JointKinds, kind?.Trim().ToLowerInvariant());
        if (idx < 0) idx = 0;
        int n = JointKinds.Length;
        return JointKinds[(((idx + dir) % n) + n) % n];
    }

    // C1-4c: the joint marker's colour by kind (bright, so it reads like the camera/light markers). A hinge
    // marker is one Object3d (single Colour), so its axis stub shares the hinge orange — it reads as a
    // separate element by its perpendicular geometry (per-face colour isn't supported by a single-Colour mesh).
    private static Rgba32 JointColor(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "hinge"      => new Rgba32(255, 150, 40),    // orange
        "distance"   => new Rgba32(60, 220, 90),     // green
        "ballsocket" => new Rgba32(0, 220, 255),     // cyan
        _            => new Rgba32(255, 120, 255),   // magenta (unknown kind)
    };

    // C1-4c: builds the editor entry for a joint — a thin LINE marker (a non-colliding, PICKABLE Object3d)
    // spanning the two anchors (coloured by kind; a hinge adds an axis stub), a "joint" descriptor (a fresh
    // id), and an EditEntry carrying the LIVE JointConfig (mutated in place → save + the C1-4a bridge follow).
    // Used by spawn AND load; the marker is rebuilt as the bodies move (UpdateJointMarkers). Replaced the
    // C1-4b placeholder sphere.
    private EditEntry BuildJointEntry(JointConfig cfg)
    {
        Vector3 wa = JointAnchorWorld(cfg.BodyA, cfg.AnchorA);
        Vector3 wb = JointAnchorWorld(cfg.BodyB, cfg.AnchorB);
        Vector3? axis = HingeAxisWorld(cfg);
        var marker = BuildJointMarkerMesh(wa, wb, cfg.Kind, axis);   // verts are in WORLD space; the transform stays at the origin
        marker.Color = JointColor(cfg.Kind);
        marker.Collides = false;   // visual-only — never a collider or a physics body
        marker.Gravity = false;
        marker.UpdateGeometry();
        // C1-5: the joint's stable id (shared with its cfg). An unset id (-1: a live spawn OR an old save with
        // no id) draws a fresh one; a synced/saved id is kept verbatim so the joint is the same everywhere.
        if (cfg.Id < 0) cfg.Id = _nextObjectId++;
        var descriptor = new WorldObject { Id = cfg.Id, Type = "joint", Position = FromVec(JointMidpoint(cfg)) };
        _models.Add(marker);
        AddDisplaysObject(marker, castsShadow: false);   // visual-only; it must not cast shadows
        var entry = new EditEntry { Descriptor = descriptor, Instance = marker, Joint = cfg };
        _editables.Add(entry);
        _jointMarkerCache[cfg] = (wa, wb, axis ?? Vector3.Zero);   // seed the rebuild cache (a resting joint won't rebuild)
        return entry;
    }

    // Rebuilds the live floor mesh from the (just-mutated) PlatformConfig: a shape/size change
    // swaps the geometry in place, keeping the entry selectable. Mirrors BuildPlatform's floor
    // build (a shadow caster, NOT a _models entry) — not ReplaceMarker (markers are visual-only).
    private void RebuildFloor(EditEntry entry)
    {
        if (entry.Platform == null) return;
        Object3d fresh = CreatePlatform(entry.Platform);
        fresh.Position = entry.Instance.Position;   // a shape/size change must keep the floor where it was moved to
        fresh.Color = ParseColor(entry.Platform.Color, new Rgba32(255, 255, 0));
        ApplyPhysicsFlags(fresh, entry.Platform.Collides, entry.Platform.Gravity);
        fresh.UpdateGeometry();
        if (entry.Instance is IDisplays oldDisp) RemoveDisplaysObject(oldDisp);
        AddDisplaysObject(fresh);          // default castsShadow:true, same as BuildPlatform
        entry.Instance = fresh;
        _floor = fresh;
    }

    // Nudges one light's Direction by delta and re-normalizes (kept a unit vector; falls back to
    // straight down if it collapses to zero), then re-aims the marker to match.
    private void AdjustDirection(EditEntry entry, Vector3 delta)
    {
        if (entry.Light == null) return;
        Vector3 d = entry.Light.Direction + delta;
        entry.Light.Direction = d.Length() > 1e-6f ? d.Norm() : new Vector3(0f, -1f, 0f);
        OrientLightMarker(entry);
    }

    // Re-aims a light's marker along its current Direction. A multi-beam spot bakes each beam's aim
    // into the mesh (no single LocalRotate), so a direction change must REBUILD it; every other marker
    // just updates LocalRotate. No-op for a point light (its cube stays axis-aligned).
    private void OrientLightMarker(EditEntry entry)
    {
        if (entry.Light == null || entry.Instance is not Object3d m) return;
        if (entry.Light.Kind == LightKind.Spot && entry.Light.BeamCount > 1)   // baked fan -> re-bake for new dir
        {
            ReplaceMarker(entry, BuildLightMarker(entry.Light.Kind, entry.Light.Direction, entry.Light.AreaSize,
                entry.Light.ConeShape, entry.Light.ConeAngleDeg, entry.Light.AreaShape, entry.Light.BeamCount));
            return;
        }
        if (entry.Light.Kind == LightKind.Point) return;
        m.LocalRotate = DirToEuler(entry.Light.Direction); m.UpdateGeometry();
    }

    // Carries a freshly-edited instance color to its derivatives: a light emits its marker's color,
    // and a platform persists its color as hex (so save/sync pick it up without a floor rebuild).
    private static void SyncColorDerived(EditEntry entry)
    {
        if (entry.Light != null) entry.Light.Rgb = entry.Instance.Color.ToUnit();
        if (entry.Platform != null) entry.Platform.Color = ToHex(entry.Instance.Color);
    }

    private void DeleteSelected()
    {
        if (_selected < 0 || _selected >= _editables.Count) return;

        // The platform "deletes" by disabling (so the removal persists on save/sync) + dropping the floor.
        var sel = _editables[_selected];
        if (string.Equals(sel.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase))
        {
            _world.Platform.Enabled = false;
            RemoveEntryAt(_selected);
            _floor = null;
            if (_online && _isServer) BroadcastWorldSettings();   // push the disable to connected clients
            return;
        }

        // C1-5: a joint deletes with a JointDelete (Op 5, keyed by cfg.Id); everything else uses the object
        // Delete (Op 2). Capture the id/kind BEFORE removal (RemoveEntryAt also drops the cfg from _world.Joints).
        bool isJoint = sel.Joint != null;
        int id = isJoint ? sel.Joint!.Id : sel.Descriptor.Id;
        RemoveEntryAt(_selected);

        // Server is the world authority: tell viewing clients to drop this object/joint by id.
        if (_online && _isServer)
            _netManager?.SendPacket(new WorldEditPacket { Op = isJoint ? (byte)5 : (byte)2, Id = id }, _myNetId);
    }

    // Removes one editable entry from the display + tracking lists and keeps _selected/_fieldIndex
    // consistent. Shared by DeleteSelected (server/local) and the client's Delete-delta handler,
    // so both paths stay identical.
    private void RemoveEntryAt(int index)
    {
        if (index < 0 || index >= _editables.Count) return;

        var entry = _editables[index];
        // Deleting the camera we're viewing THROUGH -> fall back to the body view (restore avatar visibility).
        if (IsCamera(entry) && entry.Descriptor.Id == _activeCameraId) { _activeCameraId = -1; ApplyCameraMode(); }
        // C1-4b: deleting a joint entry drops its config from _world.Joints (so it's gone everywhere — the
        // save list + the physics bridge). Deleting an OBJECT never touches _world.Joints; a joint left
        // dangling by a deleted body is harmless (the bridge skips a joint whose id doesn't resolve — C1-4a).
        if (entry.Joint != null) { _world.Joints.Remove(entry.Joint); _jointMarkerCache.Remove(entry.Joint); }   // C1-4c: drop its marker-rebuild cache too
        if (entry.Instance is IDisplays disp) RemoveDisplaysObject(disp);
        if (entry.Instance is Object3d o) _models.Remove(o);
        if (entry.Light != null) RemoveLight(entry.Light);   // a light: actually turn it off, not just hide the marker
        _impBodies.Remove(entry.Instance); _hullScale.Remove(entry.Instance);   // drop the solver RigidBody state
        _physMoved.Remove(entry.Descriptor.Id);              // and any pending/interp sync state
        _physTargets.Remove(entry.Descriptor.Id);
        _editables.RemoveAt(index);

        if (_editables.Count == 0) _selected = -1;
        else
        {
            if (index < _selected) _selected--;   // an earlier slot vanished — keep tracking the same object
            _selected = Math.Clamp(_selected, 0, _editables.Count - 1);
        }
        _fieldIndex = 0;
    }

    private void SpawnCurrent()
    {
        if (_spawnTypes.Count == 0) return;

        // Place in front of the BODY along the body-view facing (matches the crosshair region even in split;
        // in single view this is _myCamera, unchanged).
        Vector3 yaw = new Vector3(0, BodyAimCamera().cam.LocalRotate.Y, 0);
        Vector3 forward = new Vector3(1, 0, 0).Rotate(yaw);
        Vector3 spawnPos = _localBody.Position + forward * 3f;   // in front of the BODY (same in 1st person; sensible in 3rd)
        spawnPos.Y = 0f; // ground level

        string label = _spawnTypes[_spawnIndex];

        // The platform is a SINGLE-INSTANCE object: spawn re-enables + rebuilds it (restoring its last
        // config Shape/Size/Color), or just selects the existing one. (No spawn broadcast — out of scope.)
        if (string.Equals(label, "platform", StringComparison.OrdinalIgnoreCase))
        {
            int existing = _editables.FindIndex(e =>
                string.Equals(e.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) { _selected = existing; _fieldIndex = 0; return; }   // already one — just select it
            _world.Platform.Enabled = true;
            BuildPlatform();                          // appends the platform entry + sets _floor
            if (_editables.Count > 0) { _selected = _editables.Count - 1; _fieldIndex = 0; }
            if (_online && _isServer) BroadcastWorldSettings();   // push the re-enable to connected clients
            return;
        }

        // A JOINT is authored like the platform special-case (C1-4b): it isn't an Object — it's a
        // constraint. Create a JointConfig (defaults pinning the selected body to the world point in front),
        // add it to _world.Joints (which the C1-4a bridge reads), and build its placeholder midpoint marker.
        // No spawn broadcast — joint sync defers to C1-5.
        if (string.Equals(label, "joint", StringComparison.OrdinalIgnoreCase))
        {
            // BodyA = the selected real body (an object, or the platform id 0) if one is selected; else 0
            // (the platform). BodyB = -1 (the static WORLD). AnchorA = BodyA's local origin; AnchorB = the
            // spawn world point (so it reads as "pinned there"). RestLength = the BodyA↔anchor distance.
            int bodyA = 0;
            if (_selected >= 0 && _selected < _editables.Count)
            {
                var selEntry = _editables[_selected];
                if (selEntry.Joint == null && selEntry.Descriptor.Id >= 0) bodyA = selEntry.Descriptor.Id;
            }
            Vector3 anchorAWorld = JointAnchorWorld(bodyA, new Vec3Config());
            var cfg = new JointConfig
            {
                Kind = "ballsocket",
                BodyA = bodyA,
                BodyB = -1,                              // the static world
                AnchorA = new Vec3Config(),              // BodyA's local origin
                AnchorB = FromVec(spawnPos),             // the world point in front of the body
                RestLength = MathF.Max(0.5f, (anchorAWorld - spawnPos).Length()),
            };
            _world.Joints.Add(cfg);
            BuildJointEntry(cfg);   // assigns cfg.Id
            _selected = _editables.Count - 1; _fieldIndex = 0;
            // C1-5: the authority streams the new joint to viewing clients (reliable TCP; no mesh/texture).
            if (_online && _isServer)
                _netManager?.SendPacket(new WorldEditPacket { Op = 4, Id = cfg.Id, ObjectJson = JsonSerializer.Serialize(cfg) }, _myNetId);
            return;
        }

        // The built-in types (primitives + light + camera) keep their label as the Type; anything else is a
        // library mesh.
        bool isBuiltIn = label is "cube" or "sphere" or "cylinder" or "cone" or "pyramid" or "ramp" or "flatpicture" or "light" or "camera";
        // A flat picture (thin quad) and a camera marker are visual-only — non-colliding by default; gravity
        // already defaults off. Every other primitive keeps the default (collides).
        bool isFlatPic = string.Equals(label, "flatpicture", StringComparison.OrdinalIgnoreCase);
        bool isCamera = string.Equals(label, "camera", StringComparison.OrdinalIgnoreCase);
        var descriptor = new WorldObject
        {
            Id = _nextObjectId++,   // unique stable id (only the authority spawns; 5b broadcasts it)
            Type = isBuiltIn ? label : "mesh",
            Mesh = isBuiltIn ? null : label,
            Position = FromVec(spawnPos),
            Scale = 1f,
            Color = "White",
            Anchor = "Bottom",
            Radius = 1f,
            Collides = !(isFlatPic || isCamera),
        };

        var entry = BuildWorldObject(descriptor);
        if (entry == null) return;
        _selected = _editables.Count - 1; _fieldIndex = 0;

        // Server is the world authority: stream the new object to viewing clients. For a mesh,
        // also stream its .obj so a client that never had it can render it (idempotent overwrite).
        if (_online && _isServer)
        {
            // A1 tail: if the spawned object carries a texture a peer may lack, stream its bytes FIRST
            // (before the mesh chunks + the spawn packet below) so the peer has it when the spawn applies.
            StreamTextureToPeers(descriptor.Texture);

            var packet = new WorldEditPacket
            {
                Op = 1,
                Id = descriptor.Id,
                ObjectJson = JsonSerializer.Serialize(descriptor),
            };

            string? objText = null;
            if (string.Equals(descriptor.Type, "mesh", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(descriptor.Mesh))
            {
                string path = Path.Combine(AppPaths.ModelsFolder, descriptor.Mesh + ".obj");
                try { objText = File.ReadAllText(path); }
                catch (Exception ex) { Logger.Error($"Spawn: failed reading mesh '{descriptor.Mesh}' at {path}", ex); }
            }

            // LARGE mesh: stream it as chunks (paced a few per frame in Update) BEFORE the spawn, so
            // real-time packets interleave; the trailing spawn carries MeshName but EMPTY text (the
            // mesh is already on disk by then). SMALL mesh (or none): inline exactly as today.
            if (objText != null && objText.Length > MeshChunkThreshold)
            {
                string name = descriptor.Mesh!;
                int total = (objText.Length + MeshChunkSize - 1) / MeshChunkSize;
                for (int i = 0; i < total; i++)
                {
                    int start = i * MeshChunkSize;
                    string data = objText.Substring(start, Math.Min(MeshChunkSize, objText.Length - start));
                    int index = i;   // capture per iteration
                    _outgoing.Enqueue(() => _netManager?.SendPacket(
                        new MeshChunkPacket { MeshName = name, Index = index, Total = total, Data = data }, _myNetId));
                }
                packet.MeshName = name;   // MeshObjText stays empty; chunks already delivered the mesh
                _outgoing.Enqueue(() => _netManager?.SendPacket(packet, _myNetId));
            }
            else
            {
                if (objText != null) { packet.MeshName = descriptor.Mesh!; packet.MeshObjText = objText; }
                _netManager?.SendPacket(packet, _myNetId);
            }
        }
    }

    private void SaveWorld()
    {
        _world = BuildLiveWorldConfig();   // now carries Joints (C1-5) — one builder for save AND world-sync
        WorldManager.Save(_world);
        _saveMsg = $"Saved to {_world.Name} ({_world.Objects.Count} objects)";
        _saveFlash = 120;
        Logger.Info(_saveMsg);
    }

    /// <summary>
    /// Builds a WorldConfig from the CURRENT live instances (the same FromInstance read-back
    /// SaveWorld does), preserving each object's id plus the platform and graphics — WITHOUT
    /// writing to disk. Used by SaveWorld and by OnWorldRequested, so a client connecting
    /// mid-edit gets the live state (with live ids), not the stale last-saved _world.
    /// </summary>
    private WorldConfig BuildLiveWorldConfig()
    {
        // The platform is excluded from FromInstance, so sync its live floor position here — this is
        // the SINGLE place the moved floor's position flows back into the config for save/sync.
        if (_floor != null) _world.Platform.Position = FromVec(_floor.Position);
        return new WorldConfig
        {
            Name = _world.Name,
            Graphics = _world.Graphics,
            Physics = _world.Physics,     // gravity + collision world switches persist on save/sync
            Platform = _world.Platform,   // the live platform (mutated in place) rides along for save/sync
            // EXCLUDE the platform AND joint entries: they are extra selectables, not Objects — they must
            // never leak into the saved/synced object list (the platform carries through Platform above; a
            // joint is a constraint that rides WorldConfig.Joints below).
            Objects = _editables
                .Where(e => !string.Equals(e.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(e.Descriptor.Type, "joint", StringComparison.OrdinalIgnoreCase))
                .Select(e => FromInstance(e.Descriptor, e.Instance, e.Light)).ToList(),
            // C1-5: the LIVE joint list (same references) rides save AND the world-sync pack (OnWorldRequested
            // packs this builder), so a joining client receives the world's joints.
            Joints = _world.Joints,
        };
    }

    // Edge-triggered key press (true only on the frame the key transitions to down).
    private bool Pressed(ConsoleKey key)
    {
        bool down = Input.IsGetKey(key);
        bool was = _prevDown.Contains(key);
        if (down) _prevDown.Add(key); else _prevDown.Remove(key);
        return down && !was;
    }

    // Part A: marks `key` as "already seen" in the edge-detection set. The chat/field-entry read the console
    // BUFFER while the editor reads the live PHYSICAL key state (GetAsyncKeyState) via Pressed(). When the
    // chat closes on a still-held Enter, without this the editor's next-frame Pressed(Enter) would see a
    // FALSE edge (down now, "not down" before, since Pressed was never called while chatting) and wrongly
    // begin Inspector field-entry. Adding the key to _prevDown makes that Pressed() return false until it is
    // released and pressed again.
    private void ConsumeKeyForEditor(ConsoleKey key) => _prevDown.Add(key);

    // Part A (testable): the editor input (field cursor / N,M / Enter type-value / spawn / Del) runs ONLY
    // when NOT chatting and NOT typing a field, so the chat/field-entry has EXCLUSIVE input for the frame —
    // this mirrors the Update dispatch (chatting/entry → the editor branch is skipped). Exposed for a test.
    private bool EditorProcessesInput => !_isChatting && !_entryMode;
    public bool EditorProcessesInputForTest => EditorProcessesInput;
    public void SetChattingForTest(bool on) => _isChatting = on;

}
