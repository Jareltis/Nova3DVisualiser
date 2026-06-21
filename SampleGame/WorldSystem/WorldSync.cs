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

    /// <summary>
    /// Serializes the config and reads the .obj text of every distinct mesh the world's
    /// objects reference. A missing .obj is logged and skipped (not fatal).
    /// </summary>
    public static WorldSyncPacket Pack(WorldConfig world, string modelsFolder)
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

        Logger.Info($"WorldSync packed world '{world.Name}': {world.Objects.Count} objects, {packet.MeshTexts.Count} mesh file(s).");
        return packet;
    }

    /// <summary>Recovers the WorldConfig and the mesh-name to .obj-text map from a packet.</summary>
    public static (WorldConfig world, IReadOnlyDictionary<string, string> meshTexts) Unpack(WorldSyncPacket packet)
    {
        WorldConfig world = JsonSerializer.Deserialize<WorldConfig>(packet.ConfigJson, ReadOptions) ?? new WorldConfig();
        return (world, packet.MeshTexts);
    }
}
