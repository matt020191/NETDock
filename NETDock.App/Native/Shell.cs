using System.Runtime.InteropServices;

namespace NETDock.App.Native;

public sealed record IconImage(byte[] Bgra, int Width, int Height);

public sealed record ShortcutInfo(string Target, string? Arguments, string? WorkingDirectory, string? IconFile, int IconIndex);

/// <summary>Shell COM calls (images, shortcuts). Must run on a thread that called CoInitializeEx.</summary>
public static unsafe class Shell
{
    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IID_IPersistFile = new("0000010b-0000-0000-C000-000000000046");

    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4, SIIGBF_THUMBNAILONLY = 0x8;

    /// <summary>
    /// The image Explorer would show for a path or shell parsing name, at <paramref name="size"/>.
    /// For image files this is the picture itself, so custom PNG icons need no decoder.
    /// </summary>
    public static IconImage? ShellImage(string parsingName, int size, bool preferThumbnail = false)
    {
        nint factory;
        var iid = IID_IShellItemImageFactory;
        if (Win32.SHCreateItemFromParsingName(parsingName, 0, &iid, &factory) < 0 || factory == 0) return null;
        try
        {
            nint hbm = 0;
            var getImage = (delegate* unmanaged<nint, SIZE, int, nint*, int>)Com.Vt(factory)[3];
            int[] attempts = preferThumbnail
                ? [SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, SIIGBF_BIGGERSIZEOK]
                : [SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, SIIGBF_BIGGERSIZEOK];
            foreach (var flags in attempts)
                if (getImage(factory, new SIZE(size, size), flags, &hbm) >= 0 && hbm != 0) break;
            if (hbm == 0) return null;
            try { return FromHBitmap(hbm, straightAlpha: null); }
            finally { Win32.DeleteObject(hbm); }
        }
        finally { Com.Release(ref factory); }
    }

    /// <summary>A specific icon resource: "file.dll,-109" (resource id) or "file.exe,2" (index).</summary>
    public static IconImage? ResourceIcon(string file, int index, int size)
    {
        nint hicon = 0;
        uint id;
        if (Win32.PrivateExtractIcons(file, index, size, size, &hicon, &id, 1, 0) == 0 || hicon == 0) return null;
        try
        {
            ICONINFO ii;
            if (!Win32.GetIconInfo(hicon, &ii)) return null;
            try { return ii.hbmColor == 0 ? null : FromHBitmap(ii.hbmColor, straightAlpha: true); }
            finally
            {
                if (ii.hbmColor != 0) Win32.DeleteObject(ii.hbmColor);
                if (ii.hbmMask != 0) Win32.DeleteObject(ii.hbmMask);
            }
        }
        finally { Win32.DestroyIcon(hicon); }
    }

    /// <summary>Copy an HBITMAP into premultiplied top-down BGRA.</summary>
    private static IconImage? FromHBitmap(nint hbm, bool? straightAlpha)
    {
        BITMAP bmp;
        if (Win32.GetObject(hbm, sizeof(BITMAP), &bmp) == 0 || bmp.bmWidth <= 0 || bmp.bmHeight <= 0) return null;
        int w = bmp.bmWidth, h = bmp.bmHeight;
        var bi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var px = new byte[w * h * 4];
        var dc = Win32.GetDC(0);
        int lines;
        fixed (byte* p = px) lines = Win32.GetDIBits(dc, hbm, 0, (uint)h, p, &bi, 0);
        Win32.ReleaseDC(0, dc);
        if (lines == 0) return null;

        bool anyAlpha = false, colourAboveAlpha = false;
        for (var i = 0; i < px.Length; i += 4)
        {
            var a = px[i + 3];
            if (a != 0) anyAlpha = true;
            if (px[i] > a || px[i + 1] > a || px[i + 2] > a) colourAboveAlpha = true;
        }
        if (!anyAlpha)
        {
            // No alpha channel at all (old 24-bit icons): treat as opaque.
            for (var i = 3; i < px.Length; i += 4) px[i] = 255;
        }
        else if (straightAlpha ?? colourAboveAlpha)
        {
            // Premultiplied pixels can never have a channel above alpha; seeing one means straight alpha.
            for (var i = 0; i < px.Length; i += 4)
            {
                var a = px[i + 3];
                px[i] = (byte)(px[i] * a / 255);
                px[i + 1] = (byte)(px[i + 1] * a / 255);
                px[i + 2] = (byte)(px[i + 2] * a / 255);
            }
        }
        return new IconImage(px, w, h);
    }

    /// <summary>
    /// Read a .lnk without changing it. GetPath uses SLGP_RAWPATH so environment strings stay
    /// unexpanded, and nothing here runs under WOW64, so Program Files is never redirected.
    /// </summary>
    public static ShortcutInfo? ReadShortcut(string lnk)
    {
        nint link;
        var clsid = CLSID_ShellLink; var iid = IID_IShellLinkW;
        if (Win32.CoCreateInstance(&clsid, 0, 1, &iid, &link) < 0) return null;
        var persist = Com.QueryInterface(link, IID_IPersistFile);
        try
        {
            if (persist == 0) return null;
            fixed (char* p = lnk)
                if (((delegate* unmanaged<nint, char*, uint, int>)Com.Vt(persist)[5])(persist, p, 0) < 0) return null;
            var v = Com.Vt(link);
            var buf = stackalloc char[1024];
            var lk = link;
            string Get(int slot)
            {
                buf[0] = '\0';
                ((delegate* unmanaged<nint, char*, int, int>)v[slot])(lk, buf, 1024);
                return new string(buf);
            }
            buf[0] = '\0';
            ((delegate* unmanaged<nint, char*, int, WIN32_FIND_DATAW*, uint, int>)v[3])(link, buf, 1024, null, 4 /* SLGP_RAWPATH */);
            var target = Environment.ExpandEnvironmentVariables(new string(buf));
            var wd = Get(8);
            var args = Get(10);
            int iconIndex;
            buf[0] = '\0';
            ((delegate* unmanaged<nint, char*, int, int*, int>)v[16])(link, buf, 1024, &iconIndex);
            var icon = new string(buf);
            return new ShortcutInfo(target,
                args.Length > 0 ? args : null,
                wd.Length > 0 ? Environment.ExpandEnvironmentVariables(wd) : null,
                icon.Length > 0 ? Environment.ExpandEnvironmentVariables(icon) : null, iconIndex);
        }
        finally
        {
            Com.Release(ref persist);
            Com.Release(ref link);
        }
    }
}
