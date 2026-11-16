using System.Runtime.InteropServices;
using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>
/// What the system-corner tiles show (Aero Dock's system corner): local time, battery, internet connectivity and
/// the master volume. Read on the UI thread once a second while any tile is on the dock. COM GUIDs and vtable slots
/// are from the Windows SDK headers (netlistmgr.h, endpointvolume.h).
/// </summary>
public static unsafe partial class SystemStatus
{
    public static string Time { get; private set; } = "";
    public static string Date { get; private set; } = "";
    public static string LongDate { get; private set; } = "";
    public static bool HasBattery { get; private set; }
    public static int BatteryPercent { get; private set; } = -1;
    public static bool Charging { get; private set; }
    public static bool Online { get; private set; } = true;
    public static float MasterVolume { get; private set; }
    public static bool MasterMuted { get; private set; }

    /// <summary>Re-read everything; true when something a tile shows has changed.</summary>
    public static bool Refresh()
    {
        var before = Signature();
        Time = FormatTime();
        Date = FormatDate("ddd d");
        LongDate = FormatDate(null);
        ReadBattery();
        Online = IsOnline();
        ReadVolume();
        return Signature() != before;
    }

    private static string Signature() => $"{Time}|{Date}|{HasBattery}|{BatteryPercent}|{Charging}|{Online}|{MathF.Round(MasterVolume * 100)}|{MasterMuted}";

    // ---- time and date in the user's own format (the app runs with invariant globalization)

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetTimeFormatEx(string? locale, uint flags, nint time, string? format, char* buf, int len);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetDateFormatEx(string? locale, uint flags, nint date, string? format, char* buf, int len, string? calendar);

    private static string FormatTime()
    {
        var buf = stackalloc char[64];
        var n = GetTimeFormatEx(null, 2 /* TIME_NOSECONDS */, 0, null, buf, 64);
        return n > 1 ? new string(buf, 0, n - 1) : DateTime.Now.ToString("HH:mm");
    }

    private static string FormatDate(string? format)
    {
        var buf = stackalloc char[128];
        var n = GetDateFormatEx(null, format is null ? 2u /* DATE_LONGDATE */ : 0u, 0, format, buf, 128, null);
        return n > 1 ? new string(buf, 0, n - 1) : DateTime.Now.ToString(format ?? "D");
    }

    // ---- battery

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(SYSTEM_POWER_STATUS* s);

    private static void ReadBattery()
    {
        SYSTEM_POWER_STATUS s;
        if (!GetSystemPowerStatus(&s)) { HasBattery = false; return; }
        HasBattery = (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255; // 128 = no system battery, 255 = unknown
        BatteryPercent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : -1;
        Charging = s.ACLineStatus == 1 || (s.BatteryFlag & 8) != 0;
    }

    // ---- internet (Network List Manager)

    private static readonly Guid CLSID_NetworkListManager = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
    private static readonly Guid IID_INetworkListManager = new("DCB00000-570F-4A9B-8D69-199FDBA5723B");
    private static nint _nlm;

    private static bool IsOnline()
    {
        if (_nlm == 0)
        {
            var c = CLSID_NetworkListManager;
            var i = IID_INetworkListManager;
            nint p;
            if (Win32.CoCreateInstance(&c, 0, 1, &i, &p) < 0) return true; // can't tell: don't claim offline
            _nlm = p;
        }
        short connected;
        // IDispatch has 7 slots; get_IsConnectedToInternet is the 5th INetworkListManager method (11).
        return ((delegate* unmanaged<nint, short*, int>)Com.Vt(_nlm)[11])(_nlm, &connected) < 0 || connected != 0;
    }

    // ---- master volume of the default output (IAudioEndpointVolume)

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    // The volume control is kept between the once-a-second reads (creating the enumerator, device and endpoint
    // each time is three COM activations a second). It is re-fetched every 10 s, or at once if a call fails, so a
    // change of default output device is picked up.
    private static nint _endpoint;
    private static long _endpointAt;

    /// <summary>The default output's volume control (owned by the cache: don't release), or 0.</summary>
    private static nint Endpoint()
    {
        var now = Environment.TickCount64;
        if (_endpoint != 0 && now - _endpointAt < 10_000) return _endpoint;
        DropEndpoint();
        nint en = 0, dev = 0, vol = 0;
        try
        {
            var c = CLSID_MMDeviceEnumerator;
            var i = IID_IMMDeviceEnumerator;
            if (Win32.CoCreateInstance(&c, 0, 1, &i, &en) < 0) return 0;
            if (((delegate* unmanaged<nint, int, int, nint*, int>)Com.Vt(en)[4])(en, 0, 1, &dev) < 0) return 0; // GetDefaultAudioEndpoint
            var v = IID_IAudioEndpointVolume;
            if (((delegate* unmanaged<nint, Guid*, uint, nint, nint*, int>)Com.Vt(dev)[3])(dev, &v, 1, 0, &vol) < 0) return 0; // Activate
            (_endpoint, _endpointAt, vol) = (vol, now, 0);
            return _endpoint;
        }
        finally
        {
            if (vol != 0) Com.Release(ref vol);
            if (dev != 0) Com.Release(ref dev);
            if (en != 0) Com.Release(ref en);
        }
    }

    private static void DropEndpoint()
    {
        if (_endpoint != 0) Com.Release(ref _endpoint);
        _endpoint = 0;
    }

    private static void ReadVolume()
    {
        var ep = Endpoint();
        if (ep == 0) return;
        float v; int m;
        if (((delegate* unmanaged<nint, float*, int>)Com.Vt(ep)[9])(ep, &v) >= 0) MasterVolume = v;   // GetMasterVolumeLevelScalar
        else { DropEndpoint(); return; } // device gone (unplugged): fetch the new default next time
        if (((delegate* unmanaged<nint, int*, int>)Com.Vt(ep)[15])(ep, &m) >= 0) MasterMuted = m != 0; // GetMute
    }

    public static void SetMasterVolume(float level)
    {
        var ep = Endpoint();
        if (ep == 0) return;
        var g = Guid.Empty;
        if (level > 0 && MasterMuted) ((delegate* unmanaged<nint, int, Guid*, int>)Com.Vt(ep)[14])(ep, 0, &g);
        if (((delegate* unmanaged<nint, float, Guid*, int>)Com.Vt(ep)[7])(ep, Math.Clamp(level, 0f, 1f), &g) < 0) DropEndpoint(); // SetMasterVolumeLevelScalar
        ReadVolume();
    }

    public static void ToggleMasterMute()
    {
        var ep = Endpoint();
        if (ep == 0) return;
        var g = Guid.Empty;
        if (((delegate* unmanaged<nint, int, Guid*, int>)Com.Vt(ep)[14])(ep, MasterMuted ? 0 : 1, &g) < 0) DropEndpoint(); // SetMute
        ReadVolume();
    }

    /// <summary>Hover text for a tile.</summary>
    public static string LabelFor(string target) => target switch
    {
        SpecialItems.Clock => $"{LongDate}  {Time}",
        SpecialItems.Battery => !HasBattery ? L.T("No battery") : Charging ? L.F("Battery {0}%, charging", BatteryPercent) : L.F("Battery {0}%", BatteryPercent),
        SpecialItems.Network => Online ? L.T("Connected to the internet") : L.T("No internet connection"),
        SpecialItems.Volume => MasterMuted ? L.F("Muted ({0}%)", (int)MathF.Round(MasterVolume * 100)) : L.F("Volume {0}%", (int)MathF.Round(MasterVolume * 100)),
        _ => "",
    };
}
