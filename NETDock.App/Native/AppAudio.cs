using NETDock.Core;
using NETDock.App;

namespace NETDock.App.Native;

/// <summary>
/// Per-app volume through Windows' audio sessions (what the Volume Mixer shows): every session on the default
/// output device belongs to a process; its exe gives match keys (see <see cref="RunningKeys"/>) so a dock icon
/// can find its app's sessions. Raw COM vtables, no interop assemblies. Call on an STA/OLE thread.
/// </summary>
public static unsafe class AppAudio
{
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid IID_IAudioSessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
    private static readonly Guid IID_ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

    /// <summary>One app's audio: its sessions' volume controls (all held, caller releases via <see cref="Dispose"/>).</summary>
    public sealed class Sessions : IDisposable
    {
        internal readonly List<nint> Volumes = [];
        public bool Any => Volumes.Count > 0;

        public float Volume
        {
            get
            {
                if (Volumes.Count == 0) return 0;
                float v;
                ((delegate* unmanaged<nint, float*, int>)Com.Vt(Volumes[0])[4])(Volumes[0], &v); // GetMasterVolume
                return v;
            }
            set
            {
                var g = Guid.Empty;
                foreach (var p in Volumes) ((delegate* unmanaged<nint, float, Guid*, int>)Com.Vt(p)[3])(p, Math.Clamp(value, 0f, 1f), &g);
            }
        }

        public bool Muted
        {
            get
            {
                if (Volumes.Count == 0) return false;
                int m;
                ((delegate* unmanaged<nint, int*, int>)Com.Vt(Volumes[0])[6])(Volumes[0], &m); // GetMute
                return m != 0;
            }
            set
            {
                var g = Guid.Empty;
                foreach (var p in Volumes) ((delegate* unmanaged<nint, int, Guid*, int>)Com.Vt(p)[5])(p, value ? 1 : 0, &g); // SetMute
            }
        }

        public void Dispose()
        {
            for (var i = 0; i < Volumes.Count; i++) { var p = Volumes[i]; Com.Release(ref p); }
            Volumes.Clear();
        }
    }

    /// <summary>The audio sessions whose process matches any of <paramref name="keys"/>.</summary>
    public static Sessions For(IReadOnlyCollection<string> keys)
    {
        var result = new Sessions();
        if (keys.Count == 0) return result;
        nint enumerator = 0, device = 0, manager = 0, sessions = 0;
        try
        {
            var clsid = CLSID_MMDeviceEnumerator;
            var iid = IID_IMMDeviceEnumerator;
            if (Win32.CoCreateInstance(&clsid, 0, 1 /* INPROC */, &iid, &enumerator) < 0) return result;
            // IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender, eMultimedia) (4)
            if (((delegate* unmanaged<nint, int, int, nint*, int>)Com.Vt(enumerator)[4])(enumerator, 0, 1, &device) < 0) return result;
            var miid = IID_IAudioSessionManager2;
            // IMMDevice::Activate (3)
            if (((delegate* unmanaged<nint, Guid*, uint, nint, nint*, int>)Com.Vt(device)[3])(device, &miid, 1, 0, &manager) < 0) return result;
            // IAudioSessionManager2::GetSessionEnumerator (5)
            if (((delegate* unmanaged<nint, nint*, int>)Com.Vt(manager)[5])(manager, &sessions) < 0) return result;
            int count;
            ((delegate* unmanaged<nint, int*, int>)Com.Vt(sessions)[3])(sessions, &count); // GetCount
            var pidKeys = new Dictionary<uint, bool>();
            for (var i = 0; i < count; i++)
            {
                nint control = 0, control2 = 0, volume = 0;
                try
                {
                    if (((delegate* unmanaged<nint, int, nint*, int>)Com.Vt(sessions)[4])(sessions, i, &control) < 0) continue; // GetSession
                    var c2 = IID_IAudioSessionControl2;
                    if (((delegate* unmanaged<nint, Guid*, nint*, int>)Com.Vt(control)[0])(control, &c2, &control2) < 0) continue;
                    uint pid;
                    if (((delegate* unmanaged<nint, uint*, int>)Com.Vt(control2)[14])(control2, &pid) < 0 || pid == 0) continue; // GetProcessId
                    if (!pidKeys.TryGetValue(pid, out var match))
                    {
                        var path = RunningApps.PathOfProcess(pid);
                        match = path is not null && RunningKeys.ForProcess(path).Any(k => keys.Contains(k, StringComparer.OrdinalIgnoreCase));
                        pidKeys[pid] = match;
                    }
                    if (!match) continue;
                    var viid = IID_ISimpleAudioVolume;
                    if (((delegate* unmanaged<nint, Guid*, nint*, int>)Com.Vt(control)[0])(control, &viid, &volume) >= 0 && volume != 0)
                    {
                        result.Volumes.Add(volume);
                        volume = 0; // owned by result now
                    }
                }
                finally
                {
                    if (volume != 0) Com.Release(ref volume);
                    if (control2 != 0) Com.Release(ref control2);
                    if (control != 0) Com.Release(ref control);
                }
            }
        }
        catch { /* no audio device or service: no sessions */ }
        finally
        {
            if (sessions != 0) Com.Release(ref sessions);
            if (manager != 0) Com.Release(ref manager);
            if (device != 0) Com.Release(ref device);
            if (enumerator != 0) Com.Release(ref enumerator);
        }
        return result;
    }
}
