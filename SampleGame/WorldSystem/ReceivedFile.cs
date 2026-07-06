using System;
using System.IO;

namespace SampleGame.Worlds;

/// <summary>
/// Sanitizes + confines filenames that arrive from an UNTRUSTED source (a peer/server: packet.MeshName,
/// packet.TextureName, or a name from a received world config). Without this, such a name flows straight
/// into <c>Path.Combine</c> at a write site, so a hostile "../../evil.exe" / absolute path is an arbitrary
/// file-write (code-drop) vector. Pure — no filesystem I/O — so it is deterministic and unit-testable
/// (<c>securitytest</c>). A legitimate bare name ("brick.png", "monkey") passes through unchanged.
/// </summary>
public static class ReceivedFile
{
    /// <summary>
    /// Returns a safe BARE file name (no directory component) with <paramref name="requiredExtension"/>
    /// enforced, or null if <paramref name="name"/> is null/empty/whitespace or contains any path element
    /// (a separator, "..", or a rooted/absolute path).
    /// </summary>
    public static string? SafeName(string? name, string requiredExtension)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        string bare = Path.GetFileName(name);

        // Any of these means the name tried to escape a single flat file in the target folder.
        if (bare.Length == 0) return null;
        if (bare != name) return null;                 // had a directory component / separator
        if (name.Contains("..")) return null;          // parent traversal
        if (Path.IsPathRooted(name)) return null;      // absolute / rooted

        // Enforce the extension (case-insensitive); append it if missing.
        if (!bare.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
            bare += requiredExtension;

        return bare;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> under <paramref name="baseFolder"/> and returns the full path ONLY
    /// if it stays within <paramref name="baseFolder"/> (belt-and-suspenders over <see cref="SafeName"/>),
    /// else null.
    /// </summary>
    public static string? SafeCombine(string baseFolder, string? name, string requiredExtension)
    {
        var safe = SafeName(name, requiredExtension);
        if (safe == null) return null;

        var full = Path.GetFullPath(Path.Combine(baseFolder, safe));
        var root = Path.GetFullPath(baseFolder);

        // Final guard: the resolved path must stay under baseFolder (compare against the folder + separator
        // so a sibling folder sharing a name prefix can't sneak through).
        string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.Ordinal)) return null;

        return full;
    }
}
