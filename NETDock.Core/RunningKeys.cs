namespace NETDock.Core;

/// <summary>
/// How dock icons are matched to running windows. Both sides produce keys; an icon is running when any of its
/// keys is among the keys of an open window.
/// <list type="bullet">
/// <item>The exe path itself.</item>
/// <item><c>launch:&lt;folder&gt;|&lt;exe&gt;</c> for apps that start through an updater (Squirrel: Discord, Teams classic,
/// Slack...). The icon runs <c>Update.exe --processStart Discord.exe</c>; the window belongs to
/// <c>&lt;folder&gt;\app-1.0.9\Discord.exe</c>.</item>
/// <item><c>aumid:&lt;AppUserModelID&gt;</c> for Store/UWP apps (<c>shell:AppsFolder\&lt;AUMID&gt;</c>), whose windows are hosted
/// by ApplicationFrameHost.exe and only identify themselves through the window's AppUserModelID.</item>
/// </list>
/// Keys are compared case-insensitively.
/// </summary>
public static class RunningKeys
{
    public const string AppsFolder = "shell:AppsFolder\\";

    /// <summary>Keys for a dock item, given its expanded target and its arguments.</summary>
    public static List<string> ForItem(string? target, string? arguments)
    {
        var keys = new List<string>();
        if (string.IsNullOrWhiteSpace(target)) return keys;
        if (target.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase))
        {
            keys.Add("aumid:" + target[AppsFolder.Length..]);
            return keys;
        }
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return keys;
        keys.Add(target);
        if (Path.GetFileName(target).Equals("Update.exe", StringComparison.OrdinalIgnoreCase)
            && ProcessStartArgument(arguments) is { } exe && Path.GetDirectoryName(target) is { } root)
            keys.Add(Launch(root, exe));
        return keys;
    }

    /// <summary>Keys for a running process's exe path (its windows' AppUserModelIDs are added separately).</summary>
    public static List<string> ForProcess(string exePath)
    {
        var keys = new List<string> { exePath };
        var dir = Path.GetDirectoryName(exePath);
        if (dir is not null && Path.GetFileName(dir).StartsWith("app-", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(dir) is { } root)
            keys.Add(Launch(root, Path.GetFileName(exePath)));
        return keys;
    }

    public static string Aumid(string id) => "aumid:" + id;

    /// <summary>True for the plain exe-path keys (not launcher or AUMID ones).</summary>
    public static bool IsPath(string key) => !key.StartsWith("launch:", StringComparison.Ordinal) && !key.StartsWith("aumid:", StringComparison.Ordinal);

    private static string Launch(string root, string exe) => "launch:" + root.TrimEnd('\\') + "|" + exe;

    /// <summary>The program named by <c>--processStart</c> (quoted or not), or null.</summary>
    public static string? ProcessStartArgument(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;
        var i = arguments.IndexOf("--processStart", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = arguments[(i + "--processStart".Length)..].TrimStart(' ', '=');
        if (rest.Length == 0) return null;
        string value;
        if (rest[0] == '"') { var end = rest.IndexOf('"', 1); value = end > 0 ? rest[1..end] : rest[1..]; }
        else { var end = rest.IndexOf(' '); value = end > 0 ? rest[..end] : rest; }
        return value.Length > 0 && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : null;
    }
}
