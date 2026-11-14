using System.IO.Compression;
using System.Text.Json;

namespace NETDock.Core;

/// <summary>
/// Where NETDock keeps its files. Portable when a "portable" file or a Data folder sits
/// next to the exe: then settings and custom icons live in .\Data and the whole folder can
/// be copied to another PC. Otherwise %APPDATA%\NETDock.
/// </summary>
public sealed class DataLocation
{
    public string Folder { get; }
    public bool IsPortable { get; }
    public string SettingsFile => Path.Combine(Folder, "settings.json");
    public string IconsFolder => Path.Combine(Folder, "icons");
    /// <summary>Extracted target icons: machine-specific and rebuildable, so it never travels.</summary>
    public string CacheFolder { get; }

    public DataLocation(string folder, bool portable, string? cacheFolder = null)
    {
        Folder = folder;
        IsPortable = portable;
        CacheFolder = cacheFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NETDock", "cache");
    }

    public static DataLocation Resolve(string exeFolder)
    {
        var data = Path.Combine(exeFolder, "Data");
        if (File.Exists(Path.Combine(exeFolder, "portable")) || Directory.Exists(data))
            return new DataLocation(data, portable: true);
        return new DataLocation(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NETDock"), portable: false);
    }
}

public sealed class SettingsStore(DataLocation location)
{
    public DataLocation Location { get; } = location;

    public bool Exists => File.Exists(Location.SettingsFile);

    public DockSettings Load()
    {
        var file = Location.SettingsFile;
        if (!File.Exists(file)) return new DockSettings().Sanitize();
        try
        {
            var s = JsonSerializer.Deserialize(File.ReadAllText(file), SettingsJson.Default.DockSettings);
            return (s ?? new DockSettings()).Sanitize();
        }
        catch (JsonException)
        {
            // Keep the broken file for the user rather than silently losing their dock.
            File.Copy(file, file + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);
            return new DockSettings().Sanitize();
        }
    }

    public void Save(DockSettings settings)
    {
        Directory.CreateDirectory(Location.Folder);
        var file = Location.SettingsFile;
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SettingsJson.Default.DockSettings));
        if (File.Exists(file)) File.Replace(tmp, file, file + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, file);
    }

    /// <summary>Copy a user-chosen icon into the data folder and return its portable relative reference.</summary>
    public string ImportIcon(string sourceFile)
    {
        Directory.CreateDirectory(Location.IconsFolder);
        var bytes = File.ReadAllBytes(sourceFile);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
        var name = hash + Path.GetExtension(sourceFile).ToLowerInvariant();
        var dest = Path.Combine(Location.IconsFolder, name);
        if (!File.Exists(dest)) File.WriteAllBytes(dest, bytes);
        return "icons/" + name;
    }

    /// <summary>Resolve an item's icon reference to a file, if it is a data-folder one.</summary>
    public string? ResolveDataIcon(string? icon) => DataFile(icon, "icons/");

    /// <summary>
    /// "prefix/name" → a file or folder directly inside the data folder. Only a bare name is
    /// accepted, so a hand-edited or imported settings file can't point outside it.
    /// </summary>
    private string? DataFile(string? reference, string prefix)
    {
        if (!IsDataReference(reference, prefix)) return null;
        return Path.Combine(Location.Folder, prefix.TrimEnd('/'), reference![prefix.Length..]);
    }

    /// <summary>
    /// Copy a file, replacing any existing copy, and leave the result writable. Sources such
    /// as C:\Windows\Installer\*.ico are read-only, and File.Copy carries that attribute
    /// across, which would make the next import fail to overwrite it.
    /// </summary>
    public static void CopyOver(string source, string dest)
    {
        if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
        File.Copy(source, dest, overwrite: true);
        File.SetAttributes(dest, FileAttributes.Normal);
    }

    /// <summary>Copy a RocketDock skin folder into the data folder; returns its portable reference.</summary>
    public string ImportSkin(string skinFolder)
    {
        var name = Path.GetFileName(skinFolder.TrimEnd(Path.DirectorySeparatorChar, '/'));
        var dest = Path.Combine(Location.Folder, "skins", name);
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(skinFolder))
            CopyOver(f, Path.Combine(dest, Path.GetFileName(f)));
        return "skins/" + name;
    }

    /// <summary>Skins are only ever read from the data folder (ImportSkin copies them in).</summary>
    public string? ResolveSkin(string? skin) => DataFile(skin, "skins/");

    /// <summary>True for "prefix/name" where name is a single plain path segment.</summary>
    public static bool IsDataReference(string? reference, string prefix)
    {
        if (reference is null || !reference.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var name = reference[prefix.Length..];
        return name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['/', '\\', ':']) < 0;
    }

    /// <summary>One-file backup: settings.json plus every custom icon.</summary>
    public void Export(DockSettings settings, string zipFile)
    {
        Save(settings);
        if (File.Exists(zipFile)) File.Delete(zipFile);
        using var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(Location.SettingsFile, "settings.json");
        if (Directory.Exists(Location.IconsFolder))
            foreach (var f in Directory.EnumerateFiles(Location.IconsFolder))
                zip.CreateEntryFromFile(f, "icons/" + Path.GetFileName(f));
        var skins = Path.Combine(Location.Folder, "skins");
        if (Directory.Exists(skins))
            foreach (var f in Directory.EnumerateFiles(skins, "*", SearchOption.AllDirectories))
                zip.CreateEntryFromFile(f, "skins/" + Path.GetRelativePath(skins, f).Replace(Path.DirectorySeparatorChar, '/'));
    }

    public DockSettings Import(string zipFile)
    {
        using var zip = ZipFile.OpenRead(zipFile);
        var entry = zip.GetEntry("settings.json") ?? throw new InvalidDataException("Not a NETDock backup: no settings.json.");
        DockSettings s;
        using (var st = entry.Open())
            s = (JsonSerializer.Deserialize(st, SettingsJson.Default.DockSettings)
                 ?? throw new InvalidDataException("Empty settings.")).Sanitize();
        Directory.CreateDirectory(Location.IconsFolder);
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("icons/", StringComparison.Ordinal) && e.Name.Length > 0))
            e.ExtractToFile(Path.Combine(Location.IconsFolder, Path.GetFileName(e.Name)), overwrite: true);
        var skinsRoot = Path.GetFullPath(Path.Combine(Location.Folder, "skins")) + Path.DirectorySeparatorChar;
        foreach (var e in zip.Entries)
        {
            // Only "skins/<name>/<file>", each a plain segment: no "..", no deeper nesting.
            var parts = e.FullName.Split('/');
            if (parts.Length != 3 || parts[0] != "skins" || !IsDataReference("x/" + parts[1], "x/") || !IsDataReference("x/" + parts[2], "x/"))
                continue;
            var dest = Path.GetFullPath(Path.Combine(skinsRoot, parts[1], parts[2]));
            if (!dest.StartsWith(skinsRoot, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
            e.ExtractToFile(dest, overwrite: true);
        }
        Save(s);
        return s;
    }
}
