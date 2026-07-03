using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nova3DVisualiser.Logging;
using SampleGame.NetworkPackets;

namespace SampleGame.Worlds;

/// <summary>
/// Packs a world (its config + the .obj text of every mesh it references) into a
/// transferable <see cref="WorldSyncPacket"/> and back. No networking here — just the
/// data format. The client decides in 4b how to turn the mesh texts into renderable meshes.
/// </summary>
public static class WorldSync
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    // Textures whose bytes are at or under this size are inlined in the packet; larger ones are streamed
    // as TextureChunkPackets by the caller. Mirrors PriviewNetworkScene.MeshChunkThreshold (49152).
    private const int TextureInlineThreshold = 49152;

    /// <summary>
    /// Serializes the config, reads the .obj text of every distinct mesh the world's objects reference,
    /// and reads the raw PNG bytes of every distinct texture they reference — inlining SMALL textures
    /// (&lt;= <see cref="TextureInlineThreshold"/>) into the packet. LARGE textures are left out (the caller
    /// streams them via <see cref="TextureChunkPacket"/>). A missing .obj / .png is logged and skipped
    /// (not fatal).
    /// </summary>
    public static WorldSyncPacket Pack(WorldConfig world, string modelsFolder, string texturesFolder)
    {
        var packet = new WorldSyncPacket { ConfigJson = JsonSerializer.Serialize(world) };

        var meshNames = world.Objects
            .Where(o => string.Equals(o.Type, "mesh", System.StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(o.Mesh))
            .Select(o => o.Mesh!)
            .Distinct();

        foreach (var name in meshNames)
        {
            string path = Path.Combine(modelsFolder, name + ".obj");
            if (!File.Exists(path))
            {
                Logger.Warning($"WorldSync: mesh '{name}' .obj not found at {path}; skipping.");
                continue;
            }
            packet.MeshTexts[name] = File.ReadAllText(path);
        }

        // Any object (not just meshes) can wear a texture; scan every referenced .png by file name.
        var texNames = world.Objects
            .Where(o => !string.IsNullOrWhiteSpace(o.Texture))
            .Select(o => o.Texture!)
            .Distinct(System.StringComparer.OrdinalIgnoreCase);

        int inlined = 0, deferred = 0;
        foreach (var name in texNames)
        {
            string path = Path.Combine(texturesFolder, name);
            if (!File.Exists(path))
            {
                Logger.Warning($"WorldSync: texture '{name}' not found at {path}; skipping.");
                continue;
            }
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length <= TextureInlineThreshold) { packet.TextureData[name] = bytes; inlined++; }
            else deferred++;   // too big to inline — the caller streams it via TextureChunkPacket
        }

        Logger.Info($"WorldSync packed world '{world.Name}': {world.Objects.Count} objects, {packet.MeshTexts.Count} mesh file(s), {inlined} texture(s) inline, {deferred} texture(s) to stream.");
        return packet;
    }

    /// <summary>
    /// Reads the raw PNG bytes of the textures the world references that are TOO LARGE to inline
    /// (&gt; <see cref="TextureInlineThreshold"/>). The caller streams these as TextureChunkPackets so the
    /// peer reconstructs them before it builds the world. A missing/small file is not returned here.
    /// </summary>
    public static Dictionary<string, byte[]> ReadLargeTextures(WorldConfig world, string texturesFolder)
    {
        var big = new Dictionary<string, byte[]>(System.StringComparer.OrdinalIgnoreCase);
        var texNames = world.Objects
            .Where(o => !string.IsNullOrWhiteSpace(o.Texture))
            .Select(o => o.Texture!)
            .Distinct(System.StringComparer.OrdinalIgnoreCase);

        foreach (var name in texNames)
        {
            string path = Path.Combine(texturesFolder, name);
            if (!File.Exists(path)) continue;
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > TextureInlineThreshold) big[name] = bytes;
        }
        return big;
    }

    /// <summary>Recovers the WorldConfig, the mesh-name to .obj-text map, and the texture-name to raw
    /// PNG-bytes map (inlined small textures) from a packet.</summary>
    public static (WorldConfig world, IReadOnlyDictionary<string, string> meshTexts, IReadOnlyDictionary<string, byte[]> textureData) Unpack(WorldSyncPacket packet)
    {
        WorldConfig world = JsonSerializer.Deserialize<WorldConfig>(packet.ConfigJson, ReadOptions) ?? new WorldConfig();
        return (world, packet.MeshTexts, packet.TextureData);
    }
}
