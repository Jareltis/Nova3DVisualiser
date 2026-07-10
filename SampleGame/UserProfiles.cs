using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nova3DVisualiser.Logging;

namespace SampleGame;

/// <summary>
/// The LOCAL nickname store: a most-recently-used list of previously used nicknames, persisted to
/// users.json next to the worlds/textures/models runtime folders. Purely local — a nickname does NOT
/// gate or isolate any content (worlds/models/textures stay shared local files) and does NOT ride any
/// network packet this stage. The pure helpers (<see cref="SanitizeNick"/>, <see cref="TouchList"/>,
/// <see cref="ChooseNick"/>) are I/O-free so uitest can assert them without touching disk.
/// </summary>
public static class UserProfiles
{
    // users.json lives alongside the other local runtime state (worlds/ textures/ models/); git-ignored.
    public static readonly string DefaultPath = Path.Combine(AppPaths.ProjectRoot, "users.json");

    private const int MaxNickLength = 16;   // a stored nickname is capped at this many characters
    private const int MaxHistory = 20;      // the MRU list keeps at most this many nicknames

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // ---- pure helpers (no I/O — unit-testable) ----

    /// <summary>
    /// Cleans a raw nickname: trims, keeps ONLY [A-Za-z0-9_-] (dropping everything else, preserving the
    /// case of kept characters), and caps the result at 16 characters. Returns "" if nothing survives.
    /// </summary>
    public static string SanitizeNick(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new System.Text.StringBuilder(MaxNickLength);
        foreach (char ch in raw.Trim())
        {
            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') ||
                (ch >= '0' && ch <= '9') || ch == '_' || ch == '-')
            {
                sb.Append(ch);
                if (sb.Length >= MaxNickLength) break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// MRU move-to-front: removes any entry equal to <paramref name="nick"/> case-insensitively (so the
    /// new casing wins), inserts <paramref name="nick"/> at index 0, and caps the list at 20 entries.
    /// </summary>
    public static List<string> TouchList(IReadOnlyList<string> known, string nick)
    {
        var list = new List<string>(known.Count + 1);
        foreach (string existing in known)
            if (!string.Equals(existing, nick, StringComparison.OrdinalIgnoreCase))
                list.Add(existing);
        list.Insert(0, nick);
        if (list.Count > MaxHistory) list.RemoveRange(MaxHistory, list.Count - MaxHistory);
        return list;
    }

    /// <summary>
    /// Resolves the wizard's choice: a typed nickname (once sanitized) wins if non-empty; otherwise the
    /// selected known nickname wins if the list is non-empty and <paramref name="listSel"/> is in range;
    /// otherwise (false, "").
    /// </summary>
    public static (bool valid, string nick) ChooseNick(string? typed, IReadOnlyList<string> known, int listSel)
    {
        string clean = SanitizeNick(typed);
        if (clean.Length > 0) return (true, clean);
        if (known.Count > 0 && listSel >= 0 && listSel < known.Count) return (true, known[listSel]);
        return (false, "");
    }

    // ---- I/O wrappers (path-parameterised for tests + default-path convenience) ----

    /// <summary>Loads the MRU nickname list from <paramref name="path"/> (index 0 = last used). A missing,
    /// unreadable, corrupt or non-array file yields an EMPTY list with a logged warning — never throws.</summary>
    public static List<string> Load(string path)
    {
        if (!File.Exists(path)) return new List<string>();
        try
        {
            var nicks = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            if (nicks == null)
            {
                Logger.Warning($"User profiles at {path} deserialized to null; ignoring.");
                return new List<string>();
            }
            return nicks;
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not read user profiles at {path}: {ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>Loads the MRU nickname list from the default users.json path.</summary>
    public static List<string> Load() => Load(DefaultPath);

    /// <summary>Writes the MRU nickname list to <paramref name="path"/> (create/overwrite). Logs failures,
    /// never throws.</summary>
    public static void Save(string path, IReadOnlyList<string> nicks)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(nicks, WriteOptions));
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not save user profiles to {path}: {ex.Message}");
        }
    }

    /// <summary>Writes the MRU nickname list to the default users.json path.</summary>
    public static void Save(IReadOnlyList<string> nicks) => Save(DefaultPath, nicks);

    /// <summary>Records a used nickname at the front of the default MRU list. Sanitizes first; a nickname
    /// that sanitizes to "" is a no-op. Never throws.</summary>
    public static void Touch(string nick)
    {
        string clean = SanitizeNick(nick);
        if (clean.Length == 0) return;
        Save(TouchList(Load(), clean));
    }
}
