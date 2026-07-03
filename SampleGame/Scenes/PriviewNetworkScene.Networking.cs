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
    // World download/sync/edit, graphics-settings broadcast, mesh + texture chunk streaming, and received-world application.

    // ---- World download (4b): server answers a request; client applies the received world ----

    // Server side: a client asked for the world — pack ours (from the real models/) and send it.
    private void OnWorldRequested(WorldRequestPacket packet, int senderId)
    {
        // Send the LIVE world (current instances + live ids), not the stale last-saved _world,
        // so a client joining mid-edit sees exactly what the server has now.
        var live = BuildLiveWorldConfig();
        var sync = WorldSync.Pack(live, AppPaths.ModelsFolder, AppPaths.TexturesFolder);
        int connId = _netManager?.LastSenderConnId ?? -1;

        // Stream every LARGE referenced texture (too big to inline) as chunks to THIS requester FIRST, so
        // that — TCP being reliable + ordered — the peer materializes them to disk before the world packet
        // below triggers its build. Small textures already ride inline in `sync`.
        int chunkTex = 0, chunkParts = 0;
        foreach (var kv in WorldSync.ReadLargeTextures(live, AppPaths.TexturesFolder))
        {
            byte[] bytes = kv.Value;
            int total = (bytes.Length + MeshChunkSize - 1) / MeshChunkSize;
            for (int i = 0; i < total; i++)
            {
                int start = i * MeshChunkSize;
                int len = Math.Min(MeshChunkSize, bytes.Length - start);
                byte[] slice = new byte[len];
                Array.Copy(bytes, start, slice, 0, len);
                _netManager?.SendPacketTo(connId,
                    new TextureChunkPacket { TextureName = kv.Key, Index = i, Total = total, Data = slice }, _myNetId);
                chunkParts++;
            }
            chunkTex++;
        }

        // Answer ONLY the requester — broadcasting would rebuild every already-connected client's world.
        _netManager?.SendPacketTo(connId, sync, _myNetId);
        Logger.Info($"Sent world '{live.Name}' to client {senderId} ({sync.MeshTexts.Count} mesh file(s), {sync.TextureData.Count} inline texture(s), {chunkTex} streamed texture(s) in {chunkParts} chunk(s)).");
    }

    // Client side: the world arrived. Runs on the main thread (via ProcessEvents in Update),
    // so it is safe to rebuild the scene here — Update fully precedes the render each frame.
    private void OnWorldSyncReceived(WorldSyncPacket packet, int senderId)
    {
        var (world, meshTexts, textureData) = WorldSync.Unpack(packet);
        ApplyReceivedWorld(world, meshTexts, textureData);
    }

    // Client side: a server edit delta arrived. Runs on the main thread (via ProcessEvents in
    // Update), so applying directly to the live scene is safe. Handles Modify/Spawn/Delete.
    private void OnWorldEditReceived(WorldEditPacket packet, int senderId)
    {
        if (packet.Op == 2)   // Delete: drop the object with this id
        {
            int del = _editables.FindIndex(e => e.Descriptor.Id == packet.Id);
            if (del < 0) { Logger.Warning($"WorldEdit Delete for unknown id {packet.Id}; ignoring."); return; }
            RemoveEntryAt(del);
            return;
        }

        // Modify (0) and Spawn (1) both carry a WorldObject in ObjectJson.
        WorldObject? o;
        try { o = JsonSerializer.Deserialize<WorldObject>(packet.ObjectJson); }
        catch (Exception ex) { Logger.Error("WorldEdit: bad ObjectJson", ex); return; }
        if (o == null) return;

        if (packet.Op == 1)   // Spawn: materialize a streamed mesh (if any), then build + append
        {
            if (!string.IsNullOrWhiteSpace(packet.MeshObjText) && !string.IsNullOrWhiteSpace(packet.MeshName))
                MaterializeMesh(packet.MeshName, packet.MeshObjText);

            // Idempotent: if this id already exists, drop the stale instance first (no duplicate).
            int existing = _editables.FindIndex(e => e.Descriptor.Id == o.Id);
            if (existing >= 0) RemoveEntryAt(existing);

            BuildWorldObject(o);   // loads the mesh from _modelsFolder == received/; keeps the server's id
            return;                // do NOT touch _selected — the client's selection is its own
        }

        // Modify (0): update the existing instance in place + keep the stored descriptor in sync.
        int idx = _editables.FindIndex(e => e.Descriptor.Id == packet.Id);
        if (idx < 0)
        {
            Logger.Warning($"WorldEdit Modify for unknown id {packet.Id}; ignoring.");
            return;
        }
        ApplyToInstance(o, _editables[idx].Instance);
        _editables[idx].Descriptor = o;
        // A light: push the FULL new state (kind/direction/cone/size/spin/power/color) onto the
        // paired Light, co-located with the (just-moved) marker — not just position+power — then
        // morph + re-aim the client's marker so it mirrors the server's kind/direction/size.
        if (_editables[idx].Light is Light light)
        {
            ApplyLightFields(light, o, _editables[idx].Instance.Position);
            ReplaceMarker(_editables[idx], BuildLightMarker(light.Kind, light.Direction, light.AreaSize, light.ConeShape, light.ConeAngleDeg, light.AreaShape, light.BeamCount));
        }
    }

    // Server: push the live PlatformConfig (+ GraphicsConfig) to connected clients. Called on every
    // platform change (move/shape/size/color via the dirty-broadcast block, plus delete/spawn).
    // Applies the world's GraphicsConfig to the live engine state (shadows, BVH, camera light).
    // Idempotent — safe to call after any change to _world.Graphics, whether from a local runtime
    // toggle or a received settings/world packet. (ExtraLight is a real object, reconciled elsewhere.)
    private void ApplyGraphicsSettings()
    {
        EnableShadows = _world.Graphics.Shadows;
        Object3d.UseBvh = _world.Graphics.Bvh;

        bool wantOwn = !_world.Graphics.DisableCameraLight;
        if (wantOwn && !_ownLightEnabled) AddLight(_mainLight);
        if (!wantOwn && _ownLightEnabled) RemoveLight(_mainLight);
        _ownLightEnabled = wantOwn;
    }

    // Authority-only runtime graphics toggles (edit mode): flip a GraphicsConfig field, apply it
    // live, and — on the server — push the whole settings delta so connected clients follow.
    private void HandleGraphicsToggles()
    {
        bool changed = false;
        if (Pressed(ConsoleKey.F2)) { _world.Graphics.Shadows = !_world.Graphics.Shadows; changed = true; }
        if (Pressed(ConsoleKey.F3)) { _world.Graphics.Bvh = !_world.Graphics.Bvh; changed = true; }
        if (Pressed(ConsoleKey.F4)) { _world.Graphics.DisableCameraLight = !_world.Graphics.DisableCameraLight; changed = true; }

        if (changed)
        {
            ApplyGraphicsSettings();
            if (_online && _isServer) BroadcastWorldSettings();   // push the delta to clients
        }

        // Platform on/off is a settings field too (mirrors the spawn/delete-on-platform paths).
        if (Pressed(ConsoleKey.F6)) TogglePlatform();
    }

    // Flips the floor between built and removed, persisting the choice on _world.Platform.Enabled.
    // Mirrors SpawnCurrent (re-enable + BuildPlatform) and DeleteSelected (disable + drop floor).
    private void TogglePlatform()
    {
        _world.Platform.Enabled = !_world.Platform.Enabled;
        if (_world.Platform.Enabled)
        {
            BuildPlatform();   // appends the platform entry + sets _floor
        }
        else
        {
            int idx = _editables.FindIndex(e => string.Equals(e.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) RemoveEntryAt(idx);
            _floor = null;
        }
        if (_online && _isServer) BroadcastWorldSettings();   // push the change to clients
    }

    private void BroadcastWorldSettings()
    {
        if (_floor != null) _world.Platform.Position = FromVec(_floor.Position);   // capture live floor pos (as BuildLiveWorldConfig does)
        _netManager?.SendPacket(new WorldSettingsPacket
        {
            PlatformJson = JsonSerializer.Serialize(_world.Platform),
            GraphicsJson = JsonSerializer.Serialize(_world.Graphics),
        }, _myNetId);
    }

    // Client: apply a server settings delta. Runs on the main thread (via ProcessEvents), so rebuilding
    // the platform here is safe.
    private void OnWorldSettingsReceived(WorldSettingsPacket packet, int senderId)
    {
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            _world.Platform = JsonSerializer.Deserialize<PlatformConfig>(packet.PlatformJson, opts) ?? _world.Platform;
            _world.Graphics = JsonSerializer.Deserialize<GraphicsConfig>(packet.GraphicsJson, opts) ?? _world.Graphics;
        }
        catch (Exception ex) { Logger.Error("WorldSettings: bad JSON", ex); return; }

        ApplyGraphicsSettings();   // shadows + BVH + camera light, idempotent

        // rebuild the platform from the new config (drop the old floor+entry, rebuild if Enabled)
        int idx = _editables.FindIndex(e => string.Equals(e.Descriptor.Type, "platform", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) RemoveEntryAt(idx);
        _floor = null;
        BuildPlatform();   // builds floor + entry if Enabled; no-op if disabled
    }

    // Client: buffer a streamed mesh chunk; once all Total parts are present, concat + write the mesh
    // to disk. The trailing spawn WorldEditPacket (Op 1, empty MeshObjText) then builds it from disk.
    private void OnMeshChunkReceived(MeshChunkPacket packet, int senderId)
    {
        if (!_meshChunks.TryGetValue(packet.MeshName, out var buf) || buf.parts.Length != packet.Total)
        {
            buf = (packet.Total, new string[packet.Total]);
            _meshChunks[packet.MeshName] = buf;
        }
        if (packet.Index < 0 || packet.Index >= buf.total) return;   // out-of-range guard
        buf.parts[packet.Index] = packet.Data;

        if (buf.parts.Any(p => p == null)) return;   // still waiting on parts
        MaterializeMesh(packet.MeshName, string.Concat(buf.parts));
        _meshChunks.Remove(packet.MeshName);
    }

    // Client: a slice of a streamed texture arrived — buffer by Index; once all parts are present,
    // concatenate the bytes and write the .png to received/textures/. Mirrors OnMeshChunkReceived, but
    // for a BINARY payload. Ordered before the WorldSyncPacket by TCP, so the file is on disk in time.
    private void OnTextureChunkReceived(TextureChunkPacket packet, int senderId)
    {
        if (!_textureChunks.TryGetValue(packet.TextureName, out var buf) || buf.parts.Length != packet.Total)
        {
            buf = (packet.Total, new byte[packet.Total][]);
            _textureChunks[packet.TextureName] = buf;
        }
        if (packet.Index < 0 || packet.Index >= buf.total) return;   // out-of-range guard
        buf.parts[packet.Index] = packet.Data;

        if (buf.parts.Any(p => p == null)) return;   // still waiting on parts
        int totalLen = buf.parts.Sum(p => p.Length);
        byte[] full = new byte[totalLen];
        int off = 0;
        foreach (var part in buf.parts) { Array.Copy(part, 0, full, off, part.Length); off += part.Length; }
        MaterializeTexture(packet.TextureName, full);
        _textureChunks.Remove(packet.TextureName);
    }

    // Writes one received mesh to received/<name>.obj (idempotent overwrite). Shared by the 4b
    // bulk world download and the 5b Spawn handler that streams a brand-new mesh in real time.
    private static void MaterializeMesh(string name, string objText)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ReceivedFolder);
            File.WriteAllText(Path.Combine(AppPaths.ReceivedFolder, name + ".obj"), objText);
        }
        catch (Exception ex) { Logger.Error($"Failed writing received mesh '{name}'", ex); }
    }

    // Client: one reassembled/inline texture arrives — write its raw PNG bytes to received/textures/<name>
    // (idempotent overwrite), where the loader decodes it exactly like a local file. Mirrors MaterializeMesh.
    private static void MaterializeTexture(string name, byte[] pngBytes)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ReceivedTexturesFolder);
            File.WriteAllBytes(Path.Combine(AppPaths.ReceivedTexturesFolder, name), pngBytes);
        }
        catch (Exception ex) { Logger.Error($"Failed writing received texture '{name}'", ex); }
    }

    private void ApplyReceivedWorld(WorldConfig world, IReadOnlyDictionary<string, string> meshTexts,
                                    IReadOnlyDictionary<string, byte[]> textureData)
    {
        // 1) Materialize the received meshes + inline textures into dedicated folders (never the user's
        //    models/ or textures/). Large textures already streamed via chunks (materialized on arrival,
        //    which — TCP being ordered — precedes this packet, so they are on disk before the build below).
        foreach (var kv in meshTexts)
            MaterializeMesh(kv.Key, kv.Value);
        foreach (var kv in textureData)
            MaterializeTexture(kv.Key, kv.Value);
        _modelsFolder = AppPaths.ReceivedFolder;
        _texturesFolder = AppPaths.ReceivedTexturesFolder;

        // 2) Tear down the current world's objects + platform (camera stays; lights reconciled below).
        foreach (var e in _editables)
        {
            if (e.Instance is IDisplays disp) RemoveDisplaysObject(disp);
            if (e.Light != null) RemoveLight(e.Light);   // drop a light object's engine Light too
        }
        _editables.Clear();
        _models.Clear();
        _impBodies.Clear(); _physMoved.Clear(); _physTargets.Clear();   // drop stale physics/sync state
        _selected = -1;
        _floor = null;   // the floor is an editable entry now; the loop above already removed its display

        // 3) Build the received world (BuildWorldObject loads meshes from _modelsFolder == received/).
        _world = world;
        // Adopt the server's ids verbatim (do NOT reassign); future local spawns (5b) start past them.
        _nextObjectId = _world.Objects.Count == 0 ? PlatformId + 1 : _world.Objects.Max(o => o.Id) + 1;   // never hand out id 0 (platform)
        BuildPlatform();
        foreach (var o in _world.Objects)
            BuildWorldObject(o);

        // 4) Apply its graphics and reconcile the lights against the placeholder's.
        ApplyGraphicsSettings();   // shadows + BVH + camera light, idempotent

        // The settings extra light is now a normal "light" object inside the received config and was
        // already built in step 3 — no separate reconcile needed (the camera light above stays special).

        // 5) Done — drop the waiting overlay (replace on a repeat send).
        _awaitingWorld = false;
        _worldReceived = true;
        Logger.Info($"Applied received world '{_world.Name}': {_world.Objects.Count} objects, {meshTexts.Count} mesh file(s), {textureData.Count} inline texture(s) (+ any streamed to received/textures).");
    }

}
