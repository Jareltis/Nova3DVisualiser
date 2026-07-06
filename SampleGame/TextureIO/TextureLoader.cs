using System;
using System.Collections.Generic;
using System.IO;
using Nova3DVisualiser;
using Nova3DVisualiser.Logging;
using SampleGame.Worlds;

namespace SampleGame.Textures;

/// <summary>
/// Decode-once cache of <see cref="Texture"/>s keyed by file name (e.g. "brick.png"), loaded from
/// <see cref="AppPaths.TexturesFolder"/> (<c>SampleGame/textures/</c>). A missing or undecodable file is
/// cached as null (logged once) so the caller falls back to a flat colour instead of crashing or
/// re-reading a bad file every frame.
/// </summary>
public static class TextureLoader
{
    private static readonly object _lock = new();
    // Keyed by the RESOLVED full path (not the bare name) so the same file name loaded from two folders
    // — the user's textures/ and a client's received/textures/ — never collide.
    private static readonly Dictionary<string, Texture?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _foldersLogged = new(StringComparer.OrdinalIgnoreCase);

    // Logs the RESOLVED absolute textures folder and the PNG files it contains — once per distinct folder,
    // on the first load attempt from it — so a "texture not showing" problem is diagnosable straight from
    // logs/ (right folder? file present? right name?). Safe to call under _lock; never throws.
    private static void LogFolderOnce(string dir)
    {
        if (!_foldersLogged.Add(dir)) return;
        try
        {
            if (!Directory.Exists(dir))
            {
                Logger.Warning($"Textures folder does NOT exist: {dir} — create it and put your PNGs there.");
                return;
            }
            string[] pngs = Directory.GetFiles(dir, "*.png");
            Logger.Info($"Textures folder: {dir} — {pngs.Length} .png file(s): " +
                        (pngs.Length == 0 ? "(none)" : string.Join(", ", Array.ConvertAll(pngs, Path.GetFileName))));
        }
        catch (Exception ex) { Logger.Warning($"Could not enumerate textures folder: {ex.Message}"); }
    }

    /// <summary>Loads from the default textures folder (<see cref="AppPaths.TexturesFolder"/>).</summary>
    public static Texture? Get(string? fileName) => Get(AppPaths.TexturesFolder, fileName);

    /// <summary>
    /// Returns the decoded texture for <paramref name="fileName"/> resolved under <paramref name="folder"/>,
    /// or null when the name is empty, the file is missing, or it fails to decode (all non-fatal — a
    /// warning/error is logged). Cached by full path, so repeated calls (world build, editor edits) decode
    /// at most once per file. A client points <paramref name="folder"/> at the received-textures folder so
    /// streamed textures load exactly like local ones.
    /// </summary>
    public static Texture? Get(string folder, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        fileName = fileName.Trim();

        // `fileName` can arrive from a received world config (via ApplyToInstance) — confine the read to
        // `folder`, so a hostile "../../secret" name can't be read off disk. null → flat-colour fallback.
        string? path = ReceivedFile.SafeCombine(folder, fileName, ".png");
        if (path == null) { Logger.Warning($"Rejected texture with unsafe name '{fileName}'"); return null; }
        lock (_lock)
        {
            LogFolderOnce(folder);
            if (_cache.TryGetValue(path, out var cached)) return cached;

            Texture? tex = null;
            try
            {
                if (!File.Exists(path))
                {
                    Logger.Warning($"Texture '{fileName}' NOT FOUND at {path}; object will show flat colour.");
                }
                else
                {
                    PngDecoder.Image img = PngDecoder.Decode(File.ReadAllBytes(path));
                    tex = new Texture(img.Width, img.Height, img.Pixels, fileName);
                    Logger.Info($"Texture '{fileName}' -> {path}: LOADED ({img.Width}x{img.Height}).");
                }
            }
            catch (Exception ex)
            {
                // Surface the EXACT decode-rejection reason (e.g. "JPEG renamed to .png", unsupported bit
                // depth / colour type / interlacing) — the message rides on ex.Message via Logger.Error.
                Logger.Error($"Texture '{fileName}' at {path}: DECODE FAILED — object will show flat colour. Reason", ex);
                tex = null;
            }

            _cache[path] = tex;
            return tex;
        }
    }
}
