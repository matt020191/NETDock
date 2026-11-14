using System.Runtime.InteropServices;

namespace NETDock.App.Native;

// Direct2D / DirectWrite through raw vtable slots. No COM wrappers, no reflection: it is
// AOT-clean and costs nothing per call. Slot numbers come from d2d1.h / dwrite.h; every
// interface listed here is a single-inheritance chain, so slots are stable.

[StructLayout(LayoutKind.Sequential)] public struct D2DColor { public float R, G, B, A; public D2DColor(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
    public static D2DColor FromArgb(uint argb) => new(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, (argb >> 24) / 255f);
    public static D2DColor Parse(string? hex, uint fallback)
    {
        if (hex is { Length: > 1 } && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var v))
            return FromArgb(hex.Length == 7 ? 0xFF000000 | v : v);
        return FromArgb(fallback);
    }
    public readonly D2DColor WithAlpha(float a) => new(R, G, B, A * a);
}
[StructLayout(LayoutKind.Sequential)] public struct D2DRect { public float L, T, R, B; public D2DRect(float l, float t, float r, float b) { L = l; T = t; R = r; B = b; } }
[StructLayout(LayoutKind.Sequential)] public struct D2DRoundRect { public D2DRect Rect; public float RX, RY; }
[StructLayout(LayoutKind.Sequential)] public struct D2DEllipse { public float X, Y, RX, RY; }
[StructLayout(LayoutKind.Sequential)] public struct D2DPoint { public float X, Y; public D2DPoint(float x, float y) { X = x; Y = y; } }
[StructLayout(LayoutKind.Sequential)] public struct D2DMatrix { public float M11, M12, M21, M22, DX, DY;
    public static D2DMatrix Identity => new() { M11 = 1, M22 = 1 };
    public static D2DMatrix FlipY(float axis) => new() { M11 = 1, M22 = -1, DY = 2 * axis }; }
[StructLayout(LayoutKind.Sequential)] public struct D2DPixelFormat { public uint Format, AlphaMode; }
[StructLayout(LayoutKind.Sequential)] public struct D2DRenderTargetProps { public uint Type; public D2DPixelFormat PixelFormat; public float DpiX, DpiY; public uint Usage, MinLevel; }
[StructLayout(LayoutKind.Sequential)] public struct D2DBitmapProps { public D2DPixelFormat PixelFormat; public float DpiX, DpiY; }
[StructLayout(LayoutKind.Sequential)] public struct D2DSizeU { public uint W, H; }
[StructLayout(LayoutKind.Sequential)] public struct D2DGradientStop { public float Position; public D2DColor Color; }
[StructLayout(LayoutKind.Sequential)] public struct D2DLinearGradientProps { public D2DPoint Start, End; }
[StructLayout(LayoutKind.Sequential)] public struct DWriteTrimming { public uint Granularity, Delimiter, DelimiterCount; }

public static unsafe class Com
{
    public static void** Vt(nint p) => *(void***)p;
    public static void Release(ref nint p)
    {
        if (p == 0) return;
        ((delegate* unmanaged<nint, uint>)Vt(p)[2])(p);
        p = 0;
    }
    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException(what, hr);
    }
    public static nint QueryInterface(nint p, Guid iid)
    {
        nint o;
        var hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)Vt(p)[0])(p, &iid, &o);
        return hr < 0 ? 0 : o;
    }
}

/// <summary>One D2D + DWrite factory pair for the process.</summary>
public static unsafe partial class D2D
{
    [LibraryImport("d2d1.dll")] private static partial int D2D1CreateFactory(uint type, Guid* iid, void* opts, nint* f);
    [LibraryImport("dwrite.dll")] private static partial int DWriteCreateFactory(uint type, Guid* iid, nint* f);

    public static nint Factory { get; private set; }
    public static nint DWrite { get; private set; }

    public static void Init()
    {
        var iid = new Guid("06152247-6f50-465a-9245-118bfd3b6007");
        nint f;
        Com.Check(D2D1CreateFactory(0, &iid, null, &f), "D2D1CreateFactory");
        Factory = f;
        var iidW = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
        Com.Check(DWriteCreateFactory(0, &iidW, &f), "DWriteCreateFactory");
        DWrite = f;
    }

    public static nint CreateTextFormat(string family, float size, bool bold, bool centre, bool ellipsis)
    {
        nint fmt;
        fixed (char* fam = family)
        fixed (char* loc = "en-us")
            Com.Check(((delegate* unmanaged<nint, char*, nint, uint, uint, uint, float, char*, nint*, int>)Com.Vt(DWrite)[15])(
                DWrite, fam, 0, bold ? 600u : 400u, 0, 5, size, loc, &fmt), "CreateTextFormat");
        var v = Com.Vt(fmt);
        ((delegate* unmanaged<nint, uint, int>)v[3])(fmt, centre ? 2u : 0u);  // SetTextAlignment
        ((delegate* unmanaged<nint, uint, int>)v[4])(fmt, 2);                  // SetParagraphAlignment: centre
        ((delegate* unmanaged<nint, uint, int>)v[5])(fmt, 1);                  // SetWordWrapping: none
        if (ellipsis)
        {
            nint sign;
            if (((delegate* unmanaged<nint, nint, nint*, int>)Com.Vt(DWrite)[20])(DWrite, fmt, &sign) >= 0)
            {
                var t = new DWriteTrimming { Granularity = 1 };
                ((delegate* unmanaged<nint, DWriteTrimming*, nint, int>)v[9])(fmt, &t, sign); // SetTrimming
                Com.Release(ref sign);
            }
        }
        return fmt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextMetrics { public float Left, Top, Width, WidthWithTrailing, Height, LayoutWidth, LayoutHeight; public uint MaxBidi, LineCount; }

    /// <summary>Rendered width of a single line of text, in pixels.</summary>
    public static float MeasureWidth(string text, nint format, float max = 4000)
    {
        nint layout;
        fixed (char* s = text)
            if (((delegate* unmanaged<nint, char*, uint, nint, float, float, nint*, int>)Com.Vt(DWrite)[18])(
                    DWrite, s, (uint)text.Length, format, max, 200, &layout) < 0)
                return text.Length * 7f;
        try
        {
            TextMetrics m;
            return ((delegate* unmanaged<nint, TextMetrics*, int>)Com.Vt(layout)[60])(layout, &m) >= 0 ? m.WidthWithTrailing : text.Length * 7f;
        }
        finally { Com.Release(ref layout); }
    }
}

/// <summary>
/// A D2D render target drawing into a 32bpp premultiplied DIB, which UpdateLayeredWindow
/// then hands to DWM as the window's per-pixel-alpha surface.
/// </summary>
public sealed unsafe class LayeredSurface : IDisposable
{
    private nint _rt, _brush, _memDc, _dib, _oldBmp;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public nint Target => _rt;
    public nint Brush => _brush;

    public LayeredSurface()
    {
        var props = new D2DRenderTargetProps
        {
            PixelFormat = new D2DPixelFormat { Format = 87 /* B8G8R8A8_UNORM */, AlphaMode = 1 /* premultiplied */ },
            DpiX = 96, DpiY = 96,
            Usage = 2, // GDI_COMPATIBLE
        };
        nint rt;
        Com.Check(((delegate* unmanaged<nint, D2DRenderTargetProps*, nint*, int>)Com.Vt(D2D.Factory)[16])(D2D.Factory, &props, &rt), "CreateDCRenderTarget");
        _rt = rt;
        ((delegate* unmanaged<nint, uint, void>)Com.Vt(_rt)[34])(_rt, 2); // grayscale text: ClearType can't do alpha
        var c = new D2DColor(1, 1, 1, 1);
        nint b;
        Com.Check(((delegate* unmanaged<nint, D2DColor*, void*, nint*, int>)Com.Vt(_rt)[8])(_rt, &c, null, &b), "CreateSolidColorBrush");
        _brush = b;
        _memDc = Win32.CreateCompatibleDC(0);
    }

    public void EnsureSize(int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        if (w == Width && h == Height) return;
        if (_dib != 0) { Win32.SelectObject(_memDc, _oldBmp); Win32.DeleteObject(_dib); }
        var bi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        void* bits;
        _dib = Win32.CreateDIBSection(_memDc, &bi, 0, &bits, 0, 0);
        _oldBmp = Win32.SelectObject(_memDc, _dib);
        Width = w; Height = h;
        var r = new RECT(0, 0, w, h);
        Com.Check(((delegate* unmanaged<nint, nint, RECT*, int>)Com.Vt(_rt)[57])(_rt, _memDc, &r), "BindDC");
    }

    public void Begin()
    {
        ((delegate* unmanaged<nint, void>)Com.Vt(_rt)[48])(_rt);
        var clear = new D2DColor(0, 0, 0, 0);
        ((delegate* unmanaged<nint, D2DColor*, void>)Com.Vt(_rt)[47])(_rt, &clear);
    }

    public void EndAndPresent(nint hwnd, int x, int y)
    {
        ulong t1, t2;
        ((delegate* unmanaged<nint, ulong*, ulong*, int>)Com.Vt(_rt)[49])(_rt, &t1, &t2);
        var pos = new POINT(x, y);
        var size = new SIZE(Width, Height);
        var src = new POINT(0, 0);
        var blend = new BLENDFUNCTION { BlendOp = Win32.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Win32.AC_SRC_ALPHA };
        Win32.UpdateLayeredWindow(hwnd, 0, &pos, &size, _memDc, &src, 0, &blend, Win32.ULW_ALPHA);
    }

    // ---- drawing helpers ----
    public void SetColor(D2DColor c) => ((delegate* unmanaged<nint, D2DColor*, void>)Com.Vt(_brush)[8])(_brush, &c);

    public void FillRoundRect(D2DRect r, float radius, D2DColor c) => FillRoundRect(r, radius, c, _brush, true);
    public void FillRoundRect(D2DRect r, float radius, D2DColor c, nint brush, bool setColor)
    {
        if (setColor) SetColor(c);
        var rr = new D2DRoundRect { Rect = r, RX = radius, RY = radius };
        ((delegate* unmanaged<nint, D2DRoundRect*, nint, void>)Com.Vt(_rt)[19])(_rt, &rr, brush);
    }
    public void DrawRoundRect(D2DRect r, float radius, D2DColor c, float width)
    {
        SetColor(c);
        var rr = new D2DRoundRect { Rect = r, RX = radius, RY = radius };
        ((delegate* unmanaged<nint, D2DRoundRect*, nint, float, nint, void>)Com.Vt(_rt)[18])(_rt, &rr, _brush, width, 0);
    }
    public void FillRect(D2DRect r, D2DColor c)
    {
        SetColor(c);
        ((delegate* unmanaged<nint, D2DRect*, nint, void>)Com.Vt(_rt)[17])(_rt, &r, _brush);
    }
    public void DrawLine(D2DPoint a, D2DPoint b, D2DColor c, float w)
    {
        SetColor(c);
        ((delegate* unmanaged<nint, D2DPoint, D2DPoint, nint, float, nint, void>)Com.Vt(_rt)[15])(_rt, a, b, _brush, w, 0);
    }
    public void FillEllipse(float x, float y, float rx, float ry, D2DColor c)
    {
        SetColor(c);
        var e = new D2DEllipse { X = x, Y = y, RX = rx, RY = ry };
        ((delegate* unmanaged<nint, D2DEllipse*, nint, void>)Com.Vt(_rt)[21])(_rt, &e, _brush);
    }
    public void DrawBitmap(nint bmp, D2DRect dest, float opacity, D2DRect? src = null)
    {
        if (bmp == 0) return;
        if (src is { } s)
            ((delegate* unmanaged<nint, nint, D2DRect*, float, uint, D2DRect*, void>)Com.Vt(_rt)[26])(_rt, bmp, &dest, opacity, 1, &s);
        else
            ((delegate* unmanaged<nint, nint, D2DRect*, float, uint, D2DRect*, void>)Com.Vt(_rt)[26])(_rt, bmp, &dest, opacity, 1, null);
    }
    public void DrawText(string text, nint format, D2DRect layout, D2DColor c)
    {
        SetColor(c);
        fixed (char* s = text)
            ((delegate* unmanaged<nint, char*, uint, nint, D2DRect*, nint, uint, uint, void>)Com.Vt(_rt)[27])(_rt, s, (uint)text.Length, format, &layout, _brush, 0, 0);
    }
    public void SetTransform(D2DMatrix m) => ((delegate* unmanaged<nint, D2DMatrix*, void>)Com.Vt(_rt)[30])(_rt, &m);
    public void PushClip(D2DRect r) => ((delegate* unmanaged<nint, D2DRect*, uint, void>)Com.Vt(_rt)[45])(_rt, &r, 0);
    public void PopClip() => ((delegate* unmanaged<nint, void>)Com.Vt(_rt)[46])(_rt);

    /// <summary>Vertical gradient brush from top to bottom of <paramref name="r"/>.</summary>
    public nint CreateVerticalGradient(ReadOnlySpan<D2DGradientStop> stops)
    {
        nint coll, brush;
        fixed (D2DGradientStop* p = stops)
            Com.Check(((delegate* unmanaged<nint, D2DGradientStop*, uint, uint, uint, nint*, int>)Com.Vt(_rt)[9])(_rt, p, (uint)stops.Length, 0, 0, &coll), "CreateGradientStopCollection");
        var props = new D2DLinearGradientProps { Start = new D2DPoint(0, 0), End = new D2DPoint(0, 1) };
        Com.Check(((delegate* unmanaged<nint, D2DLinearGradientProps*, void*, nint, nint*, int>)Com.Vt(_rt)[10])(_rt, &props, null, coll, &brush), "CreateLinearGradientBrush");
        Com.Release(ref coll);
        return brush;
    }
    public static void SetGradientLine(nint brush, D2DPoint a, D2DPoint b)
    {
        ((delegate* unmanaged<nint, D2DPoint, void>)Com.Vt(brush)[8])(brush, a);
        ((delegate* unmanaged<nint, D2DPoint, void>)Com.Vt(brush)[9])(brush, b);
    }

    /// <summary>Upload premultiplied BGRA pixels as a bitmap for this target.</summary>
    public nint CreateBitmap(byte[] bgra, int w, int h)
    {
        var props = new D2DBitmapProps { PixelFormat = new D2DPixelFormat { Format = 87, AlphaMode = 1 }, DpiX = 96, DpiY = 96 };
        var size = new D2DSizeU { W = (uint)w, H = (uint)h };
        nint bmp;
        fixed (byte* p = bgra)
        {
            var hr = ((delegate* unmanaged<nint, D2DSizeU, void*, uint, D2DBitmapProps*, nint*, int>)Com.Vt(_rt)[4])(_rt, size, p, (uint)(w * 4), &props, &bmp);
            return hr < 0 ? 0 : bmp;
        }
    }

    public void Dispose()
    {
        Com.Release(ref _brush);
        Com.Release(ref _rt);
        if (_dib != 0) { Win32.SelectObject(_memDc, _oldBmp); Win32.DeleteObject(_dib); _dib = 0; }
        if (_memDc != 0) { Win32.DeleteDC(_memDc); _memDc = 0; }
    }
}
