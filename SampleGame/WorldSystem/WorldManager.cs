using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nova3DVisualiser.Logging;

namespace SampleGame.Worlds;

/// <summary>
/// Reads and writes <see cref="WorldConfig"/> files in <see cref="AppPaths.WorldsFolder"/>
/// (worlds/&lt;name&gt;.json) and enumerates the library meshes available to a world.
/// </summary>
public static class WorldManager
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Names of the worlds saved in the worlds folder (the .json files, without extension).</summary>
    public static List<string> ListWorlds()
    {
        var result = new List<string>();
        if (!Directory.Exists(AppPaths.WorldsFolder)) return result;

        foreach (string path in Directory.GetFiles(AppPaths.WorldsFolder, "*.json"))
            result.Add(Path.GetFileNameWithoutExtension(path));

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>Loads worlds/&lt;name&gt;.json. Returns null (with a log entry) on missing or malformed JSON.</summary>
    public static WorldConfig? Load(string name)
    {
        string path = Path.Combine(AppPaths.WorldsFolder, name + ".json");
        if (!File.Exists(path))
        {
            Logger.Warning($"World '{name}' not found at {path}");
            return null;
        }

        try
        {
            WorldConfig? world = JsonSerializer.Deserialize<WorldConfig>(File.ReadAllText(path), ReadOptions);
            if (world == null)
            {
                Logger.Error($"World '{name}' deserialized to null.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(world.Name)) world.Name = name;
            return world;
        }
        catch (Exception ex)
        {
            Logger.Error($"Invalid JSON in world '{name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Writes the world to worlds/&lt;name&gt;.json (pretty-printed).</summary>
    public static void Save(WorldConfig world)
    {
        Directory.CreateDirectory(AppPaths.WorldsFolder);
        string path = Path.Combine(AppPaths.WorldsFolder, world.Name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(world, WriteOptions));
        Logger.Info($"Saved world '{world.Name}' ({world.Objects.Count} objects) to {path}");
    }

    /// <summary>Library mesh names available to worlds (each &lt;name&gt;.obj in the models folder).</summary>
    public static List<string> ListAvailableMeshes()
    {
        var result = new List<string>();
        if (!Directory.Exists(AppPaths.ModelsFolder)) return result;

        foreach (string path in Directory.GetFiles(AppPaths.ModelsFolder, "*.obj"))
            result.Add(Path.GetFileNameWithoutExtension(path));

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>
    /// Ensures at least one world exists. If the worlds folder has none, writes a
    /// platform-only default world (no objects) so the app always has something to load.
    /// </summary>
    public static void EnsureDefault()
    {
        if (ListWorlds().Count > 0) return;

        var world = new WorldConfig
        {
            Name = "default",
            Graphics = new GraphicsConfig
            {
                Shadows = true,
                Bvh = true,
                ExtraLight = false,
                DisableCameraLight = false,
            },
            Platform = new PlatformConfig { Enabled = true, Size = 10f, Color = "Yellow" },
            Objects = new List<WorldObject>(),
        };
        Save(world);
    }
}
