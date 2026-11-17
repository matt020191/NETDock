using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NETDock.Core;

/// <summary>
/// The best logo file for a Store/MSIX app at dock sizes. The shell's own image for <c>shell:AppsFolder\&lt;AUMID&gt;</c>
/// is sometimes a blank tile frame with a tiny icon in it at 128 px and up (seen with Exora), although the package
/// ships 256 px logos. This reads the package's manifest and picks the largest one itself.
/// </summary>
public static partial class StoreAppLogo
{
    /// <summary>Split "PackageFamilyName!AppId" (an AUMID); null for anything else (desktop app ids).</summary>
    public static (string Family, string AppId)? SplitAumid(string target)
    {
        var id = target.StartsWith(RunningKeys.AppsFolder, StringComparison.OrdinalIgnoreCase) ? target[RunningKeys.AppsFolder.Length..] : target;
        var bang = id.IndexOf('!');
        if (bang <= 0 || bang == id.Length - 1 || id.Contains('\\')) return null;
        return (id[..bang], id[(bang + 1)..]);
    }

    /// <summary>The Square44x44Logo (else Square150x150Logo) the manifest names for <paramref name="appId"/>, as a package-relative path.</summary>
    public static IEnumerable<string> LogoNames(string manifestXml, string appId)
    {
        XDocument doc;
        try { doc = XDocument.Parse(manifestXml); }
        catch (System.Xml.XmlException) { yield break; }
        var app = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application" && (string?)e.Attribute("Id") == appId);
        var visual = app?.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
        if (visual is null) yield break;
        foreach (var attr in new[] { "Square44x44Logo", "Square150x150Logo" })
            if ((string?)visual.Attribute(attr) is { Length: > 0 } v) yield return v;
    }

    [GeneratedRegex(@"targetsize-(\d+)", RegexOptions.IgnoreCase)] private static partial Regex TargetSize();
    [GeneratedRegex(@"scale-(\d+)", RegexOptions.IgnoreCase)] private static partial Regex Scale();

    /// <summary>
    /// From the files beside a logo (e.g. Square44x44Logo.targetsize-256_altform-unplated.png), the one best for a
    /// <paramref name="size"/> px icon: big enough first, then the "unplated" variants (transparent, what the taskbar
    /// shows), then the smallest that is at least <paramref name="size"/>, else the largest. Light-theme and contrast variants are skipped.
    /// </summary>
    public static string? Pick(string logoName, IEnumerable<string> files, int size)
    {
        var stem = Path.GetFileNameWithoutExtension(logoName);
        var ext = Path.GetExtension(logoName);
        var best = files
            .Select(f => (File: f, Name: Path.GetFileName(f)))
            .Where(f => f.Name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) && f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Name.Contains("lightunplated", StringComparison.OrdinalIgnoreCase) && !f.Name.Contains("contrast-", StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                var t = TargetSize().Match(f.Name);
                var s = Scale().Match(f.Name);
                // A scale-N 44x44 logo is 44*N/100 px; a 150x150 tile likewise.
                var basePx = stem.Contains("150") ? 150 : 44;
                var px = t.Success ? int.Parse(t.Groups[1].Value) : s.Success ? basePx * int.Parse(s.Groups[1].Value) / 100 : basePx;
                return (f.File, Px: px, Unplated: f.Name.Contains("unplated", StringComparison.OrdinalIgnoreCase));
            })
            .OrderBy(f => f.Px >= size ? 0 : 1)
            .ThenByDescending(f => f.Unplated)
            .ThenBy(f => f.Px >= size ? f.Px : -f.Px)
            .FirstOrDefault();
        return best.File;
    }
}
