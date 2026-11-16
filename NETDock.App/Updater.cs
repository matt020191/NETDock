using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>
/// Updates from the project's GitHub releases (the code is push-mirrored there from the private Gitea). A release carries NETDock.exe (Authenticode-signed by
/// <see cref="Installer.Publisher"/>) and NETDock.exe.sha256; both are checked before the running exe is swapped.
/// Checks run on a worker thread and report back to the dock window with <see cref="WM_UPDATE_FOUND"/>.
/// </summary>
public static partial class Updater
{
    public const string ProjectUrl = "https://github.com/matt020191/NETDock";
    private const string LatestApi = "https://api.github.com/repos/matt020191/NETDock/releases/latest";
    public const uint WM_UPDATE_FOUND = Win32.WM_APP + 7;

    public sealed record Release(Version Version, string Tag, string Notes, string ExeUrl, string ShaUrl, string PageUrl);

    /// <summary>A newer release found by the last check, or null.</summary>
    public static Release? Available { get; private set; }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    static Updater() { Http.DefaultRequestHeaders.UserAgent.ParseAdd("NETDock-updater/" + Installer.Version); Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json"); }

    public static Version Current => typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>Check in the background; posts <see cref="WM_UPDATE_FOUND"/> (wParam 1 = newer found, 0 = up to date, -1 = failed).</summary>
    public static void CheckAsync(nint notify) => Task.Run(async () =>
    {
        nint result;
        try
        {
            Available = await Latest() is { } r && r.Version > Current ? r : null;
            result = Available is null ? 0 : 1;
        }
        catch (Exception ex) { App.Log(ex); result = -1; }
        Win32.PostMessage(notify, WM_UPDATE_FOUND, result, 0);
    });

    private static async Task<Release?> Latest()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(LatestApi));
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        string? exe = null, sha = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            var url = a.GetProperty("browser_download_url").GetString();
            if (string.Equals(name, "NETDock.exe", StringComparison.OrdinalIgnoreCase)) exe = url;
            else if (string.Equals(name, "NETDock.exe.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
        }
        if (exe is null || sha is null) return null;
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ProjectUrl : ProjectUrl;
        return new Release(new Version(version.Major, version.Minor, Math.Max(0, version.Build)), tag, notes, exe, sha, page);
    }

    /// <summary>Download, verify and swap in the new exe, then start it. True when this process should exit.</summary>
    public static bool Apply(Release r, nint owner)
    {
        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;
        var download = Path.Combine(dir, "NETDock.exe.new");
        try
        {
            var bytes = Http.GetByteArrayAsync(r.ExeUrl).GetAwaiter().GetResult();
            var shaText = Http.GetStringAsync(r.ShaUrl).GetAwaiter().GetResult();
            var expected = shaText.Trim().Split(' ', '\t', '\r', '\n')[0];
            var actual = Convert.ToHexString(SHA256.HashData(bytes));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L.T("The download doesn't match its checksum."));
            File.WriteAllBytes(download, bytes);
            if (!SignedByPublisher(download)) throw new InvalidDataException(L.T("The download isn't signed by the NETDock publisher."));

            // A running exe can be renamed but not overwritten: move it aside, put the new one in its place.
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            File.Move(download, exe);
            if (Installer.IsInstalled(dir)) Installer.Register(); // DisplayVersion is refreshed by the new copy too
            Process.Start(new ProcessStartInfo(exe, "--after-update") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            try { if (File.Exists(download)) File.Delete(download); } catch { /* best effort */ }
            Win32.MessageBox(owner, L.F("Couldn't update NETDock:\n{0}", ex.Message), "NETDock", Win32.MB_OK | Win32.MB_ICONWARNING);
            return false;
        }
    }

    /// <summary>Remove the previous exe left behind by an update (it was still running then).</summary>
    public static void CleanUp(string exeFolder)
    {
        try
        {
            var old = Path.Combine(exeFolder, "NETDock.exe.old");
            if (File.Exists(old)) File.Delete(old);
        }
        catch { /* the old copy is still exiting: next start removes it */ }
    }

    // ---- Authenticode: WinVerifyTrust for a valid chain, then the signer's name

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WINTRUST_FILE_INFO { public uint cbStruct; public char* pcwszFilePath; public nint hFile; public nint pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WINTRUST_DATA
    {
        public uint cbStruct; public nint pPolicyCallbackData, pSIPClientData; public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public WINTRUST_FILE_INFO* pFile; public uint dwStateAction; public nint hWVTStateData; public nint pwszURLReference;
        public uint dwProvFlags, dwUIContext; public nint pSignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static unsafe partial int WinVerifyTrust(nint hwnd, Guid* action, WINTRUST_DATA* data);

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static unsafe bool SignedByPublisher(string file)
    {
        fixed (char* path = file)
        {
            var info = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = path };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA), dwUIChoice = 2 /* WTD_UI_NONE */, fdwRevocationChecks = 1 /* WTD_REVOKE_WHOLECHAIN */,
                dwUnionChoice = 1 /* WTD_CHOICE_FILE */, pFile = &info, dwStateAction = 0,
            };
            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            if (WinVerifyTrust(-1 /* INVALID_HANDLE_VALUE: no UI */, &action, &data) != 0) return false;
        }
#pragma warning disable SYSLIB0057 // reading the signer of a signed file; the chain was verified above
        using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
        // Exact match on the Organization (O, 2.5.4.10) attribute: a substring test would accept a subject that merely
        // contains the publisher's name inside another attribute's value.
        var orgs = new System.Security.Cryptography.X509Certificates.X500DistinguishedName(cert.SubjectName.RawData)
            .EnumerateRelativeDistinguishedNames()
            .Where(r => !r.HasMultipleElements && r.GetSingleElementType().Value == "2.5.4.10")
            .Select(r => r.GetSingleElementValue())
            .ToList();
        return orgs.Count == 1 && string.Equals(orgs[0], Installer.Publisher, StringComparison.Ordinal);
    }
}
