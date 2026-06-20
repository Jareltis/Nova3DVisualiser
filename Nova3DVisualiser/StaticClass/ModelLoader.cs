using System;
using System.IO;
using Nova3DVisualiser.Logging;
using Nova3DVisualiser.Shape;

namespace Nova3DVisualiser.StaticClass;

/// <summary>
/// Loads mesh geometry from the models library. The models folder holds *.obj only —
/// placement (transform/anchor/color/spin) is the responsibility of the caller (the world).
/// </summary>
public static class ModelLoader
{
    /// <summary>
    /// Loads <c>&lt;folderPath&gt;/&lt;name&gt;.obj</c> as raw geometry: NO transform, NO anchor,
    /// and NO acceleration structure are applied — the caller does that.
    /// Returns null (with a logged warning) when the .obj is missing or fails to load.
    /// </summary>
    public static Object3d? LoadRawMesh(string folderPath, string name)
    {
        string objPath = Path.Combine(folderPath, name + ".obj");
        if (!File.Exists(objPath))
        {
            Logger.Warning($"Mesh '{name}' not found at {objPath}");
            return null;
        }

        try
        {
            Object3d mesh = ObjLoader.Load(objPath);
            Logger.Info($"Loaded raw mesh '{name}': {mesh.FaceCount} triangles");
            return mesh;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load mesh '{name}'", ex);
            return null;
        }
    }
}
