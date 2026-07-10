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
    // Runtime camera/game input, remote-player transform sync, chat, and the inline field-entry (typed editor value) path.

    private void HandleGameInput(float dt)
    {
        float rotateSpeed = 1.5f;
        float moveSpeed = 5.0f;

        if (Input.IsGetKey(ConsoleKey.LeftArrow)) _myCamera.LocalRotate.Y += rotateSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.RightArrow)) _myCamera.LocalRotate.Y -= rotateSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.UpArrow)) _myCamera.LocalRotate.Z += rotateSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.DownArrow)) _myCamera.LocalRotate.Z -= rotateSpeed * dt;
        _myCamera.LocalRotate.Z = Math.Clamp(_myCamera.LocalRotate.Z, -1.5f, 1.5f);

        // Camera roll about the forward axis (Q left / E right, R resets). X is the roll euler,
        // applied before pitch/yaw — it spins the view without changing the look direction.
        if (Input.IsGetKey(ConsoleKey.Q)) _myCamera.LocalRotate.X += rotateSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.E)) _myCamera.LocalRotate.X -= rotateSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.R)) _myCamera.LocalRotate.X = 0f;

        // Field of view / zoom (Z in / X out, V resets). Narrower FOV = zoomed in.
        float fovSpeed = 30f;   // degrees/sec, held like the light controls
        if (Input.IsGetKey(ConsoleKey.Z)) _myCamera.Fov -= fovSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.X)) _myCamera.Fov += fovSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.V)) _myCamera.Fov = Camera.DefaultFov;
        _myCamera.Fov = Math.Clamp(_myCamera.Fov, 20f, 120f);

        Vector3 yawRotation = new Vector3(0, _myCamera.LocalRotate.Y, 0);
        Vector3 forward = new Vector3(1, 0, 0).Rotate(yawRotation);
        Vector3 right   = new Vector3(0, 0, 1).Rotate(yawRotation);

        // WASD moves the BODY (the physical anchor); the camera derives from it in SyncCameraToBody.
        if (Input.IsGetKey(ConsoleKey.W)) _localBody.Position += forward * moveSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.S)) _localBody.Position -= forward * moveSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.D)) _localBody.Position += right * moveSpeed * dt;
        if (Input.IsGetKey(ConsoleKey.A)) _localBody.Position -= right * moveSpeed * dt;
        // F1 toggles free-fly / walk. Vertical Space/C only fly when NOT walking; in walk mode Space
        // jumps (handled in StepPlayerPhysics, which reads it edge-triggered).
        if (Pressed(ConsoleKey.F1)) _flyMode = !_flyMode;
        // F7 cycles the camera rig through the three body views 1st -> 3rd -> 2nd person (a purely local
        // view choice — works on every peer).
        if (Pressed(ConsoleKey.F7)) CycleBodyView();
        // F8 cycles the ACTIVE VIEW among the player body + every placed camera object (a local, per-peer
        // choice like F7 — never world state). A view-only client can look through cameras too.
        if (Pressed(ConsoleKey.F8)) CycleActiveView();
        // F9 toggles SINGLE <-> 2-way split screen (left = the current active view, right = the next one in
        // the F8 cycle). A local view choice like F7/F8 — never world state.
        if (Pressed(ConsoleKey.F9)) _splitScreen = !_splitScreen;
        if (!PlayerWalking)
        {
            if (Input.IsGetKey(ConsoleKey.Spacebar)) _localBody.Position.Y += moveSpeed * dt;
            if (Input.IsGetKey(ConsoleKey.C))        _localBody.Position.Y -= moveSpeed * dt;
        }

        if (Input.IsGetKey(ConsoleKey.OemPlus)) _mainLight.LightPower += moveSpeed * 50 * dt;
        if (Input.IsGetKey(ConsoleKey.OemMinus)) _mainLight.LightPower -= moveSpeed* 50  * dt;

        // Detail level: tap P to cycle render resolution 1->2->3->4->1 (lower = fewer rays = faster).
        if (Pressed(ConsoleKey.P)) RenderScale = RenderScale % 4 + 1;
    }

    // The F7 body-view cycle: 1st -> 3rd -> 2nd -> 1st person. While viewing THROUGH a placed camera the
    // avatar stays shown, so only re-apply avatar visibility when the body view is active (the new mode
    // still takes effect once you return to the body). Also used by the headless CycleBodyViewForTest hook.
    private void CycleBodyView()
    {
        _cameraMode = _cameraMode switch
        {
            CameraMode.FirstPerson => CameraMode.ThirdPerson,
            CameraMode.ThirdPerson => CameraMode.SecondPerson,
            _ => CameraMode.FirstPerson,   // 2nd person (or any placed-camera fallthrough) -> back to 1st
        };
        if (_activeCameraId < 0) ApplyCameraMode();
    }

    private void OnTransformReceived(TransformPacket packet, int senderId)
    {
        // Server is a hub: re-broadcast a peer's transform to all clients (the original sender ignores
        // its own echo via senderId==_myNetId), and remember which connection this netId came in on so
        // a disconnect can map it back.
        if (_isServer && _netManager != null)
        {
            _netManager.SendPacketUnreliable(packet, senderId);   // E2: re-broadcast rides UDP too
            // A UDP-delivered transform carries LastSenderConnId == -1; guard so it can't clobber the real
            // connId->netId map (which now comes from OnWorldRequested over TCP). No-op on UDP, correct on
            // the rare TCP-fallback transform.
            RecordConnMappingIfTcp(_netManager.LastSenderConnId, senderId);
        }

        if (senderId == _myNetId) return;

        if (!_remotePlayers.ContainsKey(senderId))
        {
            CreateRemotePlayer(senderId);
        }

        var player = _remotePlayers[senderId];
        player.Position = packet.Pos;
        player.LocalRotate = packet.Rot;
        player.UpdateGeometry();
    }
    
    private void CreateRemotePlayer(int netId)
    {
        Object3d newPlayerCube = CreatePlayerAvatar(netId);   // same avatar shape/colour as the local body
        _remotePlayers.Add(netId, newPlayerCube);
        AddDisplaysObject(newPlayerCube);
    }

    // Drops a peer's avatar (server on disconnect; client on a PlayerLeft notice). No-op if unknown.
    private void RemoveRemotePlayer(int netId)
    {
        if (!_remotePlayers.TryGetValue(netId, out var cube)) return;
        RemoveDisplaysObject(cube);
        _remotePlayers.Remove(netId);
    }

    // Server, main thread (via ProcessEvents): a connection dropped — remove its avatar and tell the
    // other clients to drop it too.
    private void OnClientDisconnectedServer(int connId)
    {
        // Revoke this connection's UDP token so the valid-token set doesn't grow with connection churn.
        if (_connToken.TryGetValue(connId, out long token))
        {
            _netManager?.UnregisterValidUdpToken(token);
            _connToken.Remove(connId);
        }

        if (!_connToNet.TryGetValue(connId, out int netId)) return;
        _connToNet.Remove(connId);
        _netManager?.ForgetUdpPeer(netId);   // drop this peer's UDP rate-limit bucket (senderId == netId)
        _playerNames.Remove(netId);          // N2: drop the departed peer's roster entry (no stale nick)
        RemoveRemotePlayer(netId);
        _netManager?.SendPacket(new PlayerLeftPacket { NetId = netId }, _myNetId);
    }

    // Client (and harmless on a server): a peer left — drop its avatar and its roster entry.
    private void OnPlayerLeft(PlayerLeftPacket packet, int senderId)
    {
        _playerNames.Remove(packet.NetId);   // N2: prune the roster alongside the avatar
        RemoveRemotePlayer(packet.NetId);
    }

    // N2: a peer announced its nickname. Every peer RECORDS it (keyed by the packet's OWNER NetId, not the
    // sender — a server re-broadcast preserves the original owner). The server is a HUB: it relays the packet
    // to all peers (like OnChatReceived) and back-fills a newcomer with everyone already present (incl. its
    // own nick), so a client joining after others learns all of them. Robust to repeats — the client re-sends
    // until the world arrives, and re-recording / re-sending the same entry is a harmless idempotent overwrite.
    private void OnPlayerInfoReceived(PlayerInfoPacket packet, int senderId)
    {
        // 1) Sanitize the wire nick (bounds + strips junk — same policy as our own nick).
        string nick = UserProfiles.SanitizeNick(packet.Nick);
        // 2) Record by the packet's OWNER netId. Dupe nicks across distinct netIds are fine (not unique).
        _playerNames[packet.NetId] = nick;

        // 3/4) Server acts as the roster hub.
        if (_isServer && _netManager != null)
        {
            int connId = _netManager.LastSenderConnId;   // capture before any send (SendPacket won't change it, but be safe)
            // 3) Relay this announcement to all peers; the original sender ignores its own echo (step 5).
            _netManager.SendPacket(packet, senderId);
            // 4) Back-fill the NEWCOMER (this requester) with every OTHER known entry — including the server's
            //    own (_myNetId, _myNick) — so it learns the nicks of the server + peers who joined before it.
            //    Skip the requester's own entry (it already knows itself). Not for the server's own packets.
            if (senderId != _myNetId)
                foreach (var kv in _playerNames.ToArray())   // snapshot: the loop only reads, but be defensive
                    if (kv.Key != packet.NetId)
                        _netManager.SendPacketTo(connId, new PlayerInfoPacket { NetId = kv.Key, Nick = kv.Value }, kv.Key);
        }

        // 5) Our own relayed echo — already recorded locally; nothing more to do.
        if (senderId == _myNetId) return;
    }
    
    private void HandleChatInput()
    {
        while (Console.KeyAvailable)
        {
            var keyInfo = Console.ReadKey(true);

            if (keyInfo.Key == ConsoleKey.Enter)
            {
                if (!string.IsNullOrWhiteSpace(_currentInput))
                {
                    // N2: client-self-signed — bake our OWN nickname into the sent string (same trust model
                    // as the old "Player {id}:" prefix; the roster is NOT consulted to render chat).
                    string msg = FormatChatLine(_myNick, _myNetId, _currentInput);
                    _netManager?.SendPacket(new ChatPacket(msg), _myNetId);

                    AddChatMessage(msg);
                }
                _isChatting = false;
                _currentInput = "";
                ConsumeKeyForEditor(ConsoleKey.Enter);   // Part A: the still-held Enter must NOT edge-fire the editor next frame
            }
            else if (keyInfo.Key == ConsoleKey.Escape)
            {
                _isChatting = false;
                _currentInput = "";
                ConsumeKeyForEditor(ConsoleKey.Escape);
            }
            else if (keyInfo.Key == ConsoleKey.Backspace)
            {
                if (_currentInput.Length > 0)
                    _currentInput = _currentInput.Substring(0, _currentInput.Length - 1);
            }
            // Scroll the history: PgUp/PgDn OR Up/Down arrows (arrows are captured HERE while chatting, so
            // they never reach the camera look). Older = up, newer = down; clamped, with the scrolled-up indicator.
            else if (keyInfo.Key == ConsoleKey.PageUp   || keyInfo.Key == ConsoleKey.UpArrow)   ScrollChat(+1);
            else if (keyInfo.Key == ConsoleKey.PageDown || keyInfo.Key == ConsoleKey.DownArrow) ScrollChat(-1);
            else
            {
                if (keyInfo.KeyChar != '\u0000')
                    _currentInput += keyInfo.KeyChar;
            }
        }
    }
    
    // ---- Object identity: id + name (B-identity) ----

    // The user-given name for an entry: the platform's rides on its live PlatformConfig; every other
    // object's on its descriptor. "" means "no user name — show the derived system name".
    private static string UserName(EditEntry e) => (e.Platform != null ? e.Platform.Name : e.Descriptor.Name) ?? "";

    private static void SetUserName(EditEntry e, string name)
    {
        if (e.Platform != null) e.Platform.Name = name;   // platform saves via _world.Platform
        e.Descriptor.Name = name;                         // descriptor rides FromInstance for every other object
    }

    // Derived, never stored: "{type} #{id}" (e.g. "cube #3", "light #5", "platform #0").
    public static string SystemName(EditEntry e) => $"{e.Descriptor.Type} #{e.Descriptor.Id}";

    // What the panel shows: the user name, or the system name when the user hasn't named it.
    public static string DisplayName(EditEntry e)
    {
        string n = UserName(e);
        return string.IsNullOrWhiteSpace(n) ? SystemName(e) : n;
    }

    // Whether a field takes a TYPED numeric value (Enter → parse + clamp). Everything else is either the
    // Name (its own text path) or an enum/toggle that only makes sense to cycle with N/M.
    private static bool IsNumericField(Field f) => f switch
    {
        Field.PosX or Field.PosY or Field.PosZ or
        Field.RotX or Field.RotY or Field.RotZ or
        Field.Scale or Field.RotateSpeed or
        Field.ColorR or Field.ColorG or Field.ColorB or Field.ColorA or
        Field.Radius or Field.Power or Field.ClrInf or
        Field.DirX or Field.DirY or Field.DirZ or
        Field.ConeAngle or Field.AreaSize or Field.Spin or Field.Beams or
        Field.PlatSize or Field.PlatWidth or Field.PlatDepth or
        Field.Mass or Field.Restitution or Field.Friction or Field.RollingFriction or
        Field.ColorFade or Field.TextureScale or Field.FollowTargetId or
        // joint (C1-4b) numerics (JointKind + the three *Enabled toggles are cycled, NOT typed)
        Field.JBodyA or Field.JBodyB or
        Field.JAnchorAX or Field.JAnchorAY or Field.JAnchorAZ or
        Field.JAnchorBX or Field.JAnchorBY or Field.JAnchorBZ or
        Field.JAxisX or Field.JAxisY or Field.JAxisZ or
        Field.JLower or Field.JUpper or Field.JMotorSpeed or Field.JMaxTorque or
        Field.JRestLength or Field.JFrequency or Field.JDamping => true,
        _ => false,   // Name (text), Kind/AreaShape/Shape/PlatShape/Texture/TexFace/TexFilter/Collides/Gravity/Collider/CamKind/JointKind/J*Enabled (cycle/toggle)
    };

    // Enters inline typed-entry for field `f` on entry `e`: seed the buffer (the Name field with its
    // current name, so it can be edited; a numeric field EMPTY, for a clean exact value) and drain any
    // buffered keys (incl. the Enter that opened it) so they don't leak into the buffer.
    private void BeginFieldEntry(EditEntry e, Field f)
    {
        _entryMode = true;
        _entryField = f;
        _entryBuffer = f == Field.Name ? UserName(e) : "";
        // Drain the trigger key (and any buffered keys) so they don't leak into the buffer. Guard against a
        // redirected/absent console (headless self-tests) where Console.KeyAvailable throws.
        while (!Console.IsInputRedirected && Console.KeyAvailable) Console.ReadKey(true);
    }

    private void CancelFieldEntry() { _entryMode = false; _entryBuffer = ""; }

    // Appends one keystroke to the entry buffer under the active field's rules: the Name field takes
    // name-safe chars (letters/digits/space/-/_, capped 32); a numeric field takes digits plus a single
    // leading '-' and a single '.', capped 16. Returns whether the char was accepted.
    private bool TryAppendEntryChar(char c)
    {
        if (_entryField == Field.Name)
        {
            if ((char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_') && _entryBuffer.Length < 32)
            { _entryBuffer += c; return true; }
            return false;
        }
        bool numOk = char.IsDigit(c)
                     || (c == '-' && _entryBuffer.Length == 0)     // leading sign only
                     || (c == '.' && !_entryBuffer.Contains('.')); // single decimal point
        if (numOk && _entryBuffer.Length < 16) { _entryBuffer += c; return true; }
        return false;
    }

    // Confirms the inline entry (the Enter path): the Name writes as text; a numeric buffer is PARSED and
    // routed through SetNumericField (which clamps to the field's range). An empty/invalid/NaN buffer
    // leaves the value UNCHANGED (a safe cancel), never throwing.
    private void ConfirmFieldEntry()
    {
        if (_selected >= 0 && _selected < _editables.Count)
        {
            var entry = _editables[_selected];
            if (_entryField == Field.Name)
            {
                SetUserName(entry, _entryBuffer.Trim());
                _editDirty = true;   // stream/save the rename like any other edit
            }
            else if (float.TryParse(_entryBuffer, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                     && !float.IsNaN(v) && !float.IsInfinity(v))
            {
                SetNumericField(_entryField, entry, v);   // clamps to range + side effects
                _editDirty = true;
            }
            // else: empty/invalid -> leave the value unchanged (cancel), never crash
        }
        _entryMode = false; _entryBuffer = "";
    }

    // Text entry for the inline field buffer (mirrors HandleChatInput): Enter confirms, Esc cancels,
    // Backspace deletes, and the per-field filter (TryAppendEntryChar) gates accepted chars.
    private void HandleFieldEntryInput()
    {
        while (Console.KeyAvailable)
        {
            var keyInfo = Console.ReadKey(true);
            if (keyInfo.Key == ConsoleKey.Enter) ConfirmFieldEntry();
            else if (keyInfo.Key == ConsoleKey.Escape) CancelFieldEntry();   // cancel: leave the value unchanged
            else if (keyInfo.Key == ConsoleKey.Backspace)
            {
                if (_entryBuffer.Length > 0) _entryBuffer = _entryBuffer.Substring(0, _entryBuffer.Length - 1);
            }
            else TryAppendEntryChar(keyInfo.KeyChar);   // the per-field filter accepts/rejects (Name allows space; numeric doesn't)
        }
    }

    // Sets a NUMERIC field to an absolute value, clamped to the SAME range N/M uses and with the SAME side
    // effects (geometry/marker/derived-colour updates) — keep this in lockstep with AdjustField's clamps.
    // Non-numeric fields (Name/enums/toggles) are never routed here.
    private void SetNumericField(Field f, EditEntry entry, float v)
    {
        var inst = entry.Instance;
        var o = inst as Object3d;
        switch (f)
        {
            case Field.PosX: inst.Position.X = v; o?.UpdateGeometry(); SyncLightToMarker(entry); break;
            case Field.PosY: inst.Position.Y = v; o?.UpdateGeometry(); SyncLightToMarker(entry); break;
            case Field.PosZ: inst.Position.Z = v; o?.UpdateGeometry(); SyncLightToMarker(entry); break;
            case Field.RotX: inst.LocalRotate.X = v; o?.UpdateGeometry(); break;
            case Field.RotY: inst.LocalRotate.Y = v; o?.UpdateGeometry(); break;
            case Field.RotZ: inst.LocalRotate.Z = v; o?.UpdateGeometry(); break;
            case Field.Scale:
                if (o != null) { o.Scale = MathF.Max(0.01f, v); o.UpdateGeometry(); }
                break;
            case Field.RotateSpeed:
                if (o != null) o.RotateSpeed = v;
                break;
            case Field.ColorR: inst.Color = new Rgba32((byte)Math.Clamp((int)MathF.Round(v), 0, 255), inst.Color.G, inst.Color.B, inst.Color.A); SyncColorDerived(entry); break;
            case Field.ColorG: inst.Color = new Rgba32(inst.Color.R, (byte)Math.Clamp((int)MathF.Round(v), 0, 255), inst.Color.B, inst.Color.A); SyncColorDerived(entry); break;
            case Field.ColorB: inst.Color = new Rgba32(inst.Color.R, inst.Color.G, (byte)Math.Clamp((int)MathF.Round(v), 0, 255), inst.Color.A); SyncColorDerived(entry); break;
            case Field.ColorA: inst.Color = new Rgba32(inst.Color.R, inst.Color.G, inst.Color.B, (byte)Math.Clamp((int)MathF.Round(v), 0, 255)); SyncColorDerived(entry); break;
            case Field.Radius:
                if (inst is Sphere s) s.R = MathF.Max(0.01f, v);
                break;
            case Field.Mass:
                inst.Mass = MathF.Max(MassMin, v);
                break;
            case Field.Restitution:
                // Same semantics as N/M: a negative typed value returns to "inherit world"; else clamp 0..1.
                inst.Restitution = v < 0f ? -1f : MathF.Min(1f, v);
                break;
            case Field.Friction:
                inst.Friction = Math.Clamp(v, 0f, FrictionMax);
                break;
            case Field.RollingFriction:
                inst.RollingFriction = Math.Clamp(v, 0f, 1f);
                break;
            case Field.ColorFade:
                inst.ColorFade = Math.Clamp(v, 0f, 1f);
                if (entry.Light != null) entry.Light.ColorFade = inst.ColorFade;   // pale the emitted colour too
                break;
            case Field.TextureScale:
                inst.TextureScale = Math.Clamp(v, 0.1f, 10f);
                break;
            case Field.Power:
                if (entry.Light != null) entry.Light.LightPower = MathF.Max(0f, v);
                break;
            case Field.ClrInf:
                if (entry.Light != null) entry.Light.ColorInfluence = Math.Clamp(v, 0f, 1f);
                break;
            case Field.DirX: SetDirectionComponent(entry, 0, v); break;
            case Field.DirY: SetDirectionComponent(entry, 1, v); break;
            case Field.DirZ: SetDirectionComponent(entry, 2, v); break;
            case Field.ConeAngle:
                if (entry.Light != null) { entry.Light.ConeAngleDeg = Math.Clamp(v, 1f, 89f); RebuildLightMarker(entry); }
                break;
            case Field.AreaSize:
                if (entry.Light != null)
                {
                    entry.Light.AreaSize = MathF.Max(0.05f, v);
                    if (entry.Light.Kind == LightKind.Area && entry.Instance is Object3d am)
                    { am.Scale = entry.Light.AreaSize; am.UpdateGeometry(); }   // shaped marker tracks the area size
                }
                break;
            case Field.Spin:
                if (entry.Light != null) entry.Light.SpinSpeed = v;
                break;
            case Field.Beams:
                if (entry.Light != null) { entry.Light.BeamCount = Math.Clamp((int)MathF.Round(v), 1, 8); RebuildLightMarker(entry); }
                break;
            case Field.PlatSize:
                if (entry.Platform != null) { entry.Platform.Size = MathF.Max(PlatformMin, v); RebuildFloor(entry); }
                break;
            case Field.PlatWidth:
                if (entry.Platform != null) { entry.Platform.Width = MathF.Max(PlatformMin, v); RebuildFloor(entry); }
                break;
            case Field.PlatDepth:
                if (entry.Platform != null) { entry.Platform.Depth = MathF.Max(PlatformMin, v); RebuildFloor(entry); }
                break;
            case Field.FollowTargetId:
                // Camera follow-target id: rounded, floored at -1 (any negative == the player-body sentinel).
                // Lives on the descriptor (a camera has no engine companion), riding save/sync/FromInstance.
                entry.Descriptor.FollowTargetId = Math.Max(-1, (int)MathF.Round(v));
                break;

            // ---- joint (C1-4b): typed entry sets the LIVE JointConfig field exactly (mirror AdjustField's clamps) ----
            // F2 (RC3): typed retarget also preserves the anchor's world point.
            case Field.JBodyA: if (entry.Joint != null) RetargetJointBody(entry.Joint, isA: true,  Math.Max(-1, (int)MathF.Round(v))); break;
            case Field.JBodyB: if (entry.Joint != null) RetargetJointBody(entry.Joint, isA: false, Math.Max(-1, (int)MathF.Round(v))); break;
            case Field.JAnchorAX: if (entry.Joint != null) entry.Joint.AnchorA.X = v; break;
            case Field.JAnchorAY: if (entry.Joint != null) entry.Joint.AnchorA.Y = v; break;
            case Field.JAnchorAZ: if (entry.Joint != null) entry.Joint.AnchorA.Z = v; break;
            case Field.JAnchorBX: if (entry.Joint != null) entry.Joint.AnchorB.X = v; break;
            case Field.JAnchorBY: if (entry.Joint != null) entry.Joint.AnchorB.Y = v; break;
            case Field.JAnchorBZ: if (entry.Joint != null) entry.Joint.AnchorB.Z = v; break;
            case Field.JAxisX: if (entry.Joint != null) entry.Joint.Axis.X = v; break;
            case Field.JAxisY: if (entry.Joint != null) entry.Joint.Axis.Y = v; break;
            case Field.JAxisZ: if (entry.Joint != null) entry.Joint.Axis.Z = v; break;
            case Field.JLower: if (entry.Joint != null) entry.Joint.LowerLimit = v; break;
            case Field.JUpper: if (entry.Joint != null) entry.Joint.UpperLimit = v; break;
            case Field.JMotorSpeed: if (entry.Joint != null) entry.Joint.MotorTargetSpeed = v; break;
            case Field.JMaxTorque: if (entry.Joint != null) entry.Joint.MaxMotorTorque = MathF.Max(0f, v); break;
            case Field.JRestLength: if (entry.Joint != null) entry.Joint.RestLength = MathF.Max(0f, v); break;
            case Field.JFrequency: if (entry.Joint != null) entry.Joint.Frequency = MathF.Max(0f, v); break;
            case Field.JDamping: if (entry.Joint != null) entry.Joint.DampingRatio = MathF.Max(0f, v); break;
        }
        // F3: a typed joint-param edit rebuilds the live solver joint next frame (params copied at build) — it
        // converges DYNAMICALLY to the fresh value (no re-snap; the cfg stays snap-evaluated).
        if (entry.Joint != null && FieldGroup(f) == "Joint") InvalidateBuiltJoint(entry.Joint);
    }

    // Sets one direction component (0=X,1=Y,2=Z) to an absolute value, reusing AdjustDirection's
    // renormalize + marker re-aim by feeding it the delta from the current component.
    private void SetDirectionComponent(EditEntry entry, int axis, float v)
    {
        if (entry.Light == null) return;
        Vector3 d = entry.Light.Direction;
        float cur = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
        Vector3 delta = axis == 0 ? new Vector3(v - cur, 0f, 0f)
                       : axis == 1 ? new Vector3(0f, v - cur, 0f)
                                   : new Vector3(0f, 0f, v - cur);
        AdjustDirection(entry, delta);
    }

    // Rebuilds a light's marker mesh from its current fields (cone shape/angle/beam-fan/area). Shared by
    // the typed setters that change cone geometry, mirroring AdjustField's inline ReplaceMarker calls.
    private void RebuildLightMarker(EditEntry entry)
    {
        if (entry.Light == null) return;
        ReplaceMarker(entry, BuildLightMarker(entry.Light.Kind, entry.Light.Direction, entry.Light.AreaSize,
            entry.Light.ConeShape, entry.Light.ConeAngleDeg, entry.Light.AreaShape, entry.Light.BeamCount));
    }

    private void OnChatReceived(ChatPacket packet, int senderId)
    {
        // Server is a hub: re-broadcast to all clients (the original sender ignores its own echo below).
        if (_isServer && _netManager != null) _netManager.SendPacket(packet, senderId);
        if (senderId == _myNetId) return;   // our own relayed echo — already shown locally when sent
        AddChatMessage(packet.Message);
    }

    private void AddChatMessage(string msg)
    {
        _chatHistory.Add(msg);
        if (_chatHistory.Count > MaxHistory)
        {
            _chatHistory.RemoveAt(0);
        }
        _chatShowTimer = ChatShowFrames;   // keep the contained chat box shown briefly after a new message
        // (Auto-scroll: at the bottom (_chatScroll==0) the box shows the new message; scrolled up, the draw's
        // clamp keeps the offset valid so it doesn't jump to the bottom — see ChatVisibleSlice.)
    }

    // N2: build a chat line signed with the sender's nickname (client-self-signed). A non-empty (sanitized)
    // nick prefixes "{nick}: "; an empty nick falls back to the SAME "player #<id>:" wording NameForNetId /
    // LocalBodyName use. Pure + static so a headless test can assert it directly.
    public static string FormatChatLine(string nick, int netId, string text)
    {
        string clean = UserProfiles.SanitizeNick(nick);
        return clean.Length > 0 ? $"{clean}: {text}" : $"player #{netId}: {text}";
    }

    // Scrolls the chat history: dir > 0 = older (up), dir < 0 = newer (down). Clamped at the bottom (0);
    // the draw re-clamps the top each frame via ChatVisibleSlice. Shared by PgUp/PgDn + Up/Down arrows
    // (while chatting) and PgUp/PgDn (while the box is shown but not typing).
    private void ScrollChat(int dir) => _chatScroll = Math.Max(0, _chatScroll + dir);

    // Fix-B test hooks: drive the chat scroll (same path arrows/PgUp/PgDn use) and read the offset back.
    public void ScrollChatForTest(int dir) => ScrollChat(dir);
    public int ChatScrollForTest => _chatScroll;

    // The chat DRAW (a contained, wrapped, scrollable box) lives in the overlay partial (DrawChatInterface in
    // PriviewNetworkScene.EditorOverlay.cs) so all HUD rendering is in one place.

}
