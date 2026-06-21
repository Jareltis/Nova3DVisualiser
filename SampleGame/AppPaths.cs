using System;
using System.IO;

namespace SampleGame;

public static class AppPaths
{
    // Resolve the project folder (SampleGame/) from the build output (bin/<Config>/<TFM>/ -> up 3).
    public static readonly string ProjectRoot =
        Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", ".."));

    public static readonly string ModelsFolder = Path.Combine(ProjectRoot, "models");
    public static readonly string LogsFolder = Path.Combine(ProjectRoot, "logs");
    public static readonly string WorldsFolder = Path.Combine(ProjectRoot, "worlds");

    // Meshes downloaded from a server (the client writes received .obj here; never the user's models/).
    public static readonly string ReceivedFolder = Path.Combine(ProjectRoot, "received");
}
