using System.Text.Json;

namespace NETDock.Core;

/// <summary>
/// UI text catalog. Code writes the English text, wrapped in <see cref="T"/>; a language file maps that English
/// text to a translation (gettext style, so the English is the key and nothing goes missing when a file is old).
/// Language files are JSON objects, <c>{ "English text": "Translated text" }</c>, named <c>&lt;Language&gt;.json</c>
/// in a <c>languages</c> folder next to the exe or in the data folder. <c>languages\template.json</c> lists every string.
/// </summary>
public static class L
{
    private static Dictionary<string, string>? _map;

    /// <summary>Name of the loaded language, or null for English.</summary>
    public static string? Current { get; private set; }

    /// <summary>The translation of <paramref name="english"/>, or the English itself.</summary>
    public static string T(string english) =>
        _map is not null && _map.TryGetValue(english, out var t) && !string.IsNullOrEmpty(t) ? t : english;

    /// <summary><see cref="T"/> then string.Format: placeholders like {0} stay in the translated text.</summary>
    public static string F(string english, params object?[] args)
    {
        var text = T(english);
        try { return string.Format(System.Globalization.CultureInfo.CurrentCulture, text, args); }
        catch (FormatException) { return string.Format(System.Globalization.CultureInfo.CurrentCulture, english, args); } // bad translation
    }

    /// <summary>Loads a language file; null, a missing file or a broken file means English.</summary>
    public static void Load(string? path, string? name)
    {
        _map = null;
        Current = null;
        if (path is null || !File.Exists(path)) return;
        try
        {
            using var s = File.OpenRead(path);
            _map = JsonSerializer.Deserialize(s, SettingsJson.Default.DictionaryStringString);
            Current = _map is null ? null : name;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            _map = null;
        }
    }

    /// <summary>Language files in the given folders (file name without .json), excluding the template.</summary>
    public static List<string> Available(params string?[] folders)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in folders)
            if (f is not null && Directory.Exists(f))
                foreach (var file in Directory.EnumerateFiles(f, "*.json"))
                    if (Path.GetFileNameWithoutExtension(file) is { } n && !n.Equals("template", StringComparison.OrdinalIgnoreCase))
                        names.Add(n);
        return [.. names];
    }

    /// <summary>The file for a language name: data folder first, then next to the exe. Bare names only.</summary>
    public static string? Find(string? name, params string?[] folders)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', ':', '.']) >= 0) return null;
        foreach (var f in folders)
            if (f is not null && Path.Combine(f, name + ".json") is var p && File.Exists(p)) return p;
        return null;
    }
}
