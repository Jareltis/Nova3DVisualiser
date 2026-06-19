using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Shape;

namespace Nova3DVisualiser.StaticClass;

public class Vec3Config
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

public class ModelConfig
{
    public string Name { get; set; } = "";
    public Vec3Config Position { get; set; } = new();
    public Vec3Config Rotation { get; set; } = new();
    public float Scale { get; set; } = 1f;
    public string Color { get; set; } = "White";
    public float RotateSpeed { get; set; } = 0f;
    public string Anchor { get; set; } = "bottom";
}

public static class ModelLoader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    static AnchorMode ParseAnchor(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "center" => AnchorMode.Center,
        "origin" => AnchorMode.Origin,
        _ => AnchorMode.Bottom
    };

    public static List<Object3d> LoadFolder(string folderPath)
    {
        var result = new List<Object3d>();

        if (!Directory.Exists(folderPath))
        {
            Logger.Warning($"Models folder not found: {folderPath}");
            return result;
        }

        foreach (string objPath in Directory.GetFiles(folderPath, "*.obj"))
        {
            try
            {
                Object3d model = ObjLoader.Load(objPath);
                AnchorMode anchor = AnchorMode.Bottom;

                string jsonPath = Path.ChangeExtension(objPath, ".json");
                if (File.Exists(jsonPath))
                {
                    try
                    {
                        ModelConfig? cfg = JsonSerializer.Deserialize<ModelConfig>(File.ReadAllText(jsonPath), Options);
                        if (cfg != null)
                        {
                            ApplyConfig(model, cfg);
                            anchor = ParseAnchor(cfg.Anchor);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Invalid JSON in {Path.GetFileName(jsonPath)}; loading at origin with defaults. {ex.Message}");
                    }
                }
                else
                {
                    Logger.Warning($"No JSON config for {Path.GetFileName(objPath)}; placing at origin with defaults.");
                }

                model.ApplyAnchor(anchor);
                model.BuildAcceleration();
                model.UpdateGeometry();
                result.Add(model);
                Logger.Info($"Added model '{Path.GetFileNameWithoutExtension(objPath)}': size {model.Size.X:F2}x{model.Size.Y:F2}x{model.Size.Z:F2}, anchor={anchor}, bvh={model.HasBvh}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load model {Path.GetFileName(objPath)}", ex);
            }
        }

        return result;
    }

    private static void ApplyConfig(Object3d model, ModelConfig cfg)
    {
        model.Position = new Vector3(cfg.Position.X, cfg.Position.Y, cfg.Position.Z);
        model.LocalRotate = new Vector3(cfg.Rotation.X, cfg.Rotation.Y, cfg.Rotation.Z);
        model.Scale = cfg.Scale;
        model.RotateSpeed = cfg.RotateSpeed;
        if (Enum.TryParse<ConsoleColor>(cfg.Color, true, out ConsoleColor color))
            model.Color = color;
    }
}
