namespace NETDock.Core;

/// <summary>
/// Stores paths with environment tokens (%ProgramFiles%, %USERPROFILE%, ...) so a dock
/// copied to another PC points at that PC's folders. Matching is on whole path segments,
/// so "C:\Program Files (x86)\x" can never become "%ProgramFiles% (x86)\x" and the two
/// Program Files folders can't be swapped (the bug that broke RocketDock's Start In).
/// </summary>
public sealed class PathTokens
{
    /// <summary>The dock's own folder, for apps carried alongside a portable dock.</summary>
    public const string DockToken = "%NETDOCK%";

    private readonly List<(string Token, string Path)> _map;

    public PathTokens(IEnumerable<(string Token, string Path)> map)
    {
        // Longest folder first, so %LOCALAPPDATA% wins over %USERPROFILE%.
        _map = map.Where(m => !string.IsNullOrWhiteSpace(m.Path))
                  .Select(m => (m.Token, Path: m.Path.TrimEnd('\\', '/')))
                  .OrderByDescending(m => m.Path.Length)
                  .ToList();
    }

    public static PathTokens ForThisMachine(string dockFolder)
    {
        static string Env(string n) => Environment.GetEnvironmentVariable(n) ?? "";
        var pf64 = Env("ProgramW6432");
        return new PathTokens([
            (DockToken, dockFolder),
            ("%LOCALAPPDATA%", Env("LOCALAPPDATA")),
            ("%APPDATA%", Env("APPDATA")),
            ("%OneDrive%", Env("OneDrive")),
            ("%USERPROFILE%", Env("USERPROFILE")),
            ("%ProgramFiles(x86)%", Env("ProgramFiles(x86)")),
            // ProgramW6432 is the real 64-bit folder even under WOW64.
            ("%ProgramFiles%", pf64.Length > 0 ? pf64 : Env("ProgramFiles")),
            ("%ProgramData%", Env("ProgramData")),
            ("%PUBLIC%", Env("PUBLIC")),
            ("%SystemRoot%", Env("SystemRoot")),
        ]);
    }

    public string? Tokenize(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith('%') || path.Contains("::")
            || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            return path;
        foreach (var (token, root) in _map)
        {
            if (path.Length < root.Length || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (path.Length == root.Length) return token;
            if (path[root.Length] is '\\' or '/') return token + path[root.Length..];
        }
        return path;
    }

    public string? Expand(string? path)
    {
        if (string.IsNullOrEmpty(path) || !path.Contains('%')) return path;
        foreach (var (token, root) in _map)
        {
            if (path.StartsWith(token, StringComparison.OrdinalIgnoreCase)
                && (path.Length == token.Length || path[token.Length] is '\\' or '/'))
                return root + path[token.Length..];
        }
        return Environment.ExpandEnvironmentVariables(path);
    }
}
