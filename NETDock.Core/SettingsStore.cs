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
    public string? ResolveDataIcon(string? icon) =>
        icon is not null && icon.StartsWith("icons/", StringComparison.Ordinal)
            ? Path.Combine(Location.Folder, icon.Replace('/', Path.DirectorySeparatorChar))
            : null;

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
        Save(s);
        return s;
    }
}
