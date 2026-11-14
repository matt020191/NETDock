namespace NETDock.Core;

/// <summary>
/// Reads a RocketDock 1.3.5 configuration (HKCU\Software\RocketDock values, or the same keys
/// from a portable Settings.ini) into NETDock settings.
///
/// Two RocketDock bugs are repaired on the way in:
/// <list type="bullet">
/// <item>Stacks: RocketDock keeps a stack's folder in Docklets\Settings.ini under
/// [DockletN], where N is the icon's position at the time. Moving or inserting icons orphans
/// the section, which is why the grid lost its folder on restart. Sections are matched by
/// position first and then by icon file, so orphaned ones are still found.</item>
/// <item>32-bit redirection: RocketDock is a 32-bit process, and paths it rewrote point into
/// "Program Files (x86)" even when the app is in "Program Files". A path that does not
/// exist is swapped to the other Program Files folder when that one does.</item>
/// </list>
/// </summary>
public sealed class RocketDockImport(PathTokens tokens, Func<string, bool> exists)
{
    /// <param name="SkinFolder">RocketDock's current skin folder, for the caller to copy in.</param>
    public sealed record Result(DockSettings Settings, List<string> Notes, Dictionary<string, string> IconFilesToCopy, string? SkinFolder = null);

    /// <param name="icons">Values of the Icons key: "0-Title", "0-Command", ..., "count".</param>
    /// <param name="root">Values of the RocketDock key itself (IconMin, Side, ...).</param>
    /// <param name="dockletIni">Contents of Docklets\Settings.ini, if present.</param>
    /// <param name="rocketDockFolder">Install folder, for relative icon paths like "Icons\x.png".</param>
    public Result Import(IReadOnlyDictionary<string, string> icons, IReadOnlyDictionary<string, string> root,
        string? dockletIni, string rocketDockFolder)
    {
        var notes = new List<string>();
        var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var settings = new DockSettings();
        ApplyOptions(settings.Dock, root);

        var docklets = ParseIni(dockletIni ?? "");
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var count = int.TryParse(Get(icons, "count"), out var c) ? c : 0;
        for (var i = 0; i < count; i++)
        {
            string V(string k) => Get(icons, $"{i}-{k}") ?? "";
            if (V("IsSeparator") == "1")
            {
                settings.Items.Add(new DockItem { Kind = ItemKind.Separator });
                continue;
            }

            var title = V("Title");
            var iconFile = V("FileName");
            var docklet = V("DockletFile");

            if (docklet.Contains("Stack", StringComparison.OrdinalIgnoreCase))
            {
                var section = FindDockletSection(docklets, i, iconFile, used);
                var folder = section is null ? null : Get(section, "Folder");
                if (folder is null)
                {
                    notes.Add($"Stack \"{title}\": RocketDock had lost its folder; set it with Change folder.");
                }
                var item = new DockItem
                {
                    Kind = ItemKind.Stack,
                    Name = title.Length > 0 ? title : Path.GetFileName(folder ?? "Stack"),
                    Target = tokens.Tokenize(folder) ?? "",
                    Stack = new StackOptions
                    {
                        // RocketDock Mode: 0 fan, 1 list(ish), 2 grid. Fan has no NETDock equivalent; use grid.
                        View = Get(section, "Mode") == "1" ? StackView.List : StackView.Grid,
                        SortBy = Get(section, "Sort") switch { "1" => StackSort.DateModified, "2" => StackSort.Kind, _ => StackSort.Name },
                    },
                };
                item.Icon = MapIcon(Get(section, "Icon") ?? iconFile, item.Target, rocketDockFolder, copies);
                settings.Items.Add(item);
                continue;
            }
            if (docklet.Length > 0)
            {
                notes.Add($"\"{title}\" is a {Path.GetFileNameWithoutExtension(docklet)} docklet; skipped (no NETDock equivalent yet).");
                continue;
            }

            var command = V("Command");
            if (command.Length == 0) continue;
            var dock = new DockItem { Kind = ItemKind.App, Name = title };

            if (command.Equals("[RecycleBin]", StringComparison.OrdinalIgnoreCase))
                command = RecycleBin.Target;

            if (command.StartsWith("::", StringComparison.Ordinal))
            {
                dock.Target = command;
            }
            else
            {
                var fixedCommand = Repair(command, notes, title);
                dock.Kind = fixedCommand.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ItemKind.Shortcut : ItemKind.App;
                dock.Target = tokens.Tokenize(fixedCommand)!;
                var wd = V("WorkingDirectory").TrimEnd('\\');
                if (wd.Length > 0)
                {
                    wd = Repair(wd, notes, title + " (Start In)");
                    // Store Start In only when it differs from the default (the target's folder),
                    // so it keeps following the app if it moves.
                    var def = Path.GetDirectoryName(fixedCommand)?.TrimEnd('\\');
                    if (!string.Equals(wd, def, StringComparison.OrdinalIgnoreCase))
                        dock.WorkingDirectory = tokens.Tokenize(wd);
                }
            }
            var args = V("Arguments");
            if (args.Length > 0) dock.Arguments = args;
            dock.Icon = MapIcon(iconFile, dock.Target, rocketDockFolder, copies);
            if (RecycleBin.Is(dock) && V("FileName2") is { Length: > 0 } full)
                dock.IconFull = MapIcon(full, dock.Target, rocketDockFolder, copies);
            settings.Items.Add(dock);
        }
        string? skin = null;
        if (Get(root, "Theme") is { Length: > 0 } theme)
        {
            var dir = Path.Combine(rocketDockFolder, "Skins", theme);
            if (exists(Path.Combine(dir, "background.ini"))) skin = dir;
        }
        settings.Dock.Style = DockStyle.Panel;
        settings.Dock.Spacing = 2;
        return new Result(settings.Sanitize(), notes, copies, skin);
    }

    /// <summary>Swap Program Files and Program Files (x86) when only the other one exists.</summary>
    private string Repair(string path, List<string> notes, string what)
    {
        if (exists(path)) return path;
        const string x86 = @"\Program Files (x86)\", x64 = @"\Program Files\";
        string? alt = null;
        var ix = path.IndexOf(x86, StringComparison.OrdinalIgnoreCase);
        if (ix >= 0) alt = path[..ix] + x64 + path[(ix + x86.Length)..];
        else if ((ix = path.IndexOf(x64, StringComparison.OrdinalIgnoreCase)) >= 0)
            alt = path[..ix] + x86 + path[(ix + x64.Length)..];
        if (alt is not null && exists(alt))
        {
            notes.Add($"{what}: fixed {path} -> {alt}");
            return alt;
        }
        return path;
    }

    /// <summary>
    /// RocketDock icon references: "file?-109" (resource id), "file?123456" (its own cache id,
    /// meaning "the file's icon"), "Icons\x.png" (relative to the install folder) or a plain path.
    /// </summary>
    private string? MapIcon(string raw, string target, string rocketDockFolder, Dictionary<string, string> copies)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var q = raw.IndexOf('?');
        var file = q >= 0 ? raw[..q] : raw;
        var index = q >= 0 && int.TryParse(raw[(q + 1)..], out var n) && n < 0 ? n : 0;
        if (!Path.IsPathRooted(file)) file = Path.Combine(rocketDockFolder, file);

        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is ".png" or ".ico" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff")
        {
            if (!exists(file)) return null;
            // Copied into the data folder by the caller so the icon travels with the dock.
            var key = "rd-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(file.ToLowerInvariant())))[..12] + ext;
            copies[file] = key;
            return "icons/" + key;
        }
        var tokenized = tokens.Tokenize(file)!;
        if (index == 0 && string.Equals(tokenized, target, StringComparison.OrdinalIgnoreCase)) return null;
        return $"{tokenized},{index}";
    }

    private static Dictionary<string, string>? FindDockletSection(
        Dictionary<string, Dictionary<string, string>> ini, int index, string iconFile, HashSet<string> used)
    {
        if (ini.TryGetValue($"Docklet{index}", out var s) && s.ContainsKey("Folder") && used.Add($"Docklet{index}")) return s;
        // Orphaned by an icon move: find the unused section showing the same icon.
        var icon = iconFile.Split('?')[0];
        foreach (var (name, sec) in ini)
        {
            if (!sec.ContainsKey("Folder") || used.Contains(name)) continue;
            if (Get(sec, "Icon") is { } i && string.Equals(i, icon, StringComparison.OrdinalIgnoreCase) && used.Add(name)) return sec;
        }
        // Last resort: a single unclaimed stack section is almost certainly this one.
        var unclaimed = ini.Where(kv => kv.Value.ContainsKey("Folder") && !used.Contains(kv.Key)).ToList();
        if (unclaimed.Count == 1 && used.Add(unclaimed[0].Key)) return unclaimed[0].Value;
        return null;
    }

    private static void ApplyOptions(DockOptions d, IReadOnlyDictionary<string, string> r)
    {
        int I(string k, int def) => int.TryParse(Get(r, k), out var v) ? v : def;
        d.IconSize = I("IconMin", d.IconSize);
        d.MagnifiedSize = I("IconMax", d.MagnifiedSize);
        d.Magnify = d.MagnifiedSize > d.IconSize;
        // ZoomWidth is the total number of icons that grow; NETDock's range is per side.
        d.MagnifyRange = Math.Max(1, I("ZoomWidth", 5) / 2.0);
        d.ShowLabels = I("HideLabels", 0) == 0;
        d.AutoHide = I("AutoHide", 0) == 1;
        d.AutoHideDelayMs = I("AutoHideDelay", d.AutoHideDelayMs);
        d.ShowRunningIndicators = true;
        d.Edge = I("Side", 1) switch { 0 => DockEdge.Top, 2 => DockEdge.Left, 3 => DockEdge.Right, _ => DockEdge.Bottom };
        d.Monitor = Math.Max(0, I("Monitor", -1) + 1);
        d.EdgeOffset = Math.Max(0, I("vOffset", d.EdgeOffset));
    }

    public static Dictionary<string, Dictionary<string, string>> ParseIni(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? cur = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1];
                if (!result.TryGetValue(name, out cur)) result[name] = cur = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq > 0 && cur is not null) cur[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return result;
    }

    private static string? Get(IReadOnlyDictionary<string, string>? d, string k) =>
        d is not null && d.TryGetValue(k, out var v) ? v : null;
}
