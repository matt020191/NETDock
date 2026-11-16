using System.Runtime.InteropServices;
using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>The dock's accent colour: Windows' own accent, or the most prominent colour of the desktop wallpaper.</summary>
public static unsafe partial class Accent
{
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetColorizationColor(uint* color, int* opaque);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, char* buf, uint winIni);

    /// <summary>Where the decodable wallpaper copy goes (the settings folder; set at start-up).</summary>
    public static string CacheFolder { get; set; } = Path.GetTempPath();

    private static string? _wallKey;
    private static (byte R, byte G, byte B)? _wallColor;

    /// <summary>Opaque accent for the source, or null (none, or nothing could be read).</summary>
    public static D2DColor? For(AccentSource source) => source switch
    {
        AccentSource.Windows => WindowsAccent(),
        AccentSource.Wallpaper => WallpaperAccent() ?? WindowsAccent(),
        _ => null,
    };

    private static D2DColor? WindowsAccent()
    {
        uint c; int opaque;
        if (DwmGetColorizationColor(&c, &opaque) < 0) return null;
        return D2DColor.FromArgb(0xFF000000 | c);
    }

    private static D2DColor? WallpaperAccent()
    {
        var buf = stackalloc char[520];
        var path = SystemParametersInfo(0x73 /* SPI_GETDESKWALLPAPER */, 520, buf, 0) ? new string(buf) : "";
        // Some setups (per-monitor or Spotlight wallpapers) answer with an empty path; the registry still has the file.
        if (path.Length == 0) path = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string ?? "";
        if (path.Length == 0 || !File.Exists(path)) return null;
        // Decoding is the slow part: only redo it when the wallpaper file changes.
        var key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
        if (key != _wallKey)
        {
            _wallKey = key;
            _wallColor = null;
            try
            {
                // Windows keeps a copied wallpaper as "TranscodedWallpaper" with no extension, which the Shell
                // can't pick a decoder for: decode a copy named .jpg (WIC sniffs the real format).
                var file = path;
                if (Path.GetExtension(path).Length == 0)
                {
                    Directory.CreateDirectory(CacheFolder);
                    file = Path.Combine(CacheFolder, "wallpaper.jpg");
                    File.Copy(path, file, overwrite: true);
                }
                if (Shell.ShellImage(file, 64, preferThumbnail: true) is { } img) _wallColor = AccentColor.Dominant(img.Bgra);
            }
            catch (Exception ex) { App.Log(ex); /* unreadable image: fall back to the Windows accent */ }
        }
        return _wallColor is { } w ? new D2DColor(w.R / 255f, w.G / 255f, w.B / 255f, 1) : null;
    }

    /// <summary>Forget the cached wallpaper colour (after Windows says the wallpaper changed).</summary>
    public static void Invalidate() => _wallKey = null;
}
