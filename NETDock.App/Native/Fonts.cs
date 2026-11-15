using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NETDock.App.Native;

/// <summary>Installed font families, for the label font picker.</summary>
public static unsafe partial class Fonts
{
    [LibraryImport("gdi32.dll", EntryPoint = "EnumFontFamiliesExW")]
    private static partial int EnumFontFamiliesEx(nint hdc, char* logfont, delegate* unmanaged<char*, nint, uint, nint, int> proc, nint lparam, uint flags);
    [LibraryImport("user32.dll")] private static partial nint GetDC(nint h);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(nint h, nint dc);

    private static HashSet<string>? _collect;

    public static List<string> Installed()
    {
        _collect = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lf = stackalloc char[46]; // LOGFONTW is 92 bytes; face name at byte 28
        new Span<char>(lf, 46).Clear();
        ((byte*)lf)[23] = 1; // lfCharSet = DEFAULT_CHARSET: every family once per charset
        var dc = GetDC(0);
        try { EnumFontFamiliesEx(dc, lf, &OnFont, 0, 0); }
        finally { ReleaseDC(0, dc); }
        var list = _collect.ToList();
        _collect = null;
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    [UnmanagedCallersOnly]
    private static int OnFont(char* logfont, nint metrics, uint type, nint lparam)
    {
        var name = new string((char*)((byte*)logfont + 28));
        if (name.Length > 0 && name[0] != '@') _collect?.Add(name); // '@' = vertical CJK variants
        return 1;
    }
}
