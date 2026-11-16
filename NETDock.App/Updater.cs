using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>
/// Updates from the project's GitHub releases (the code is push-mirrored there from the private Gitea). A release
/// carries NETDock.exe (Authenticode-signed by <see cref="Installer.Publisher"/>) and NETDock.exe.sha256; both are
/// checked before the running exe is swapped. Network work runs on a worker thread and reports back to the dock
/// window: <see cref="WM_UPDATE_FOUND"/> after a check, <see cref="WM_UPDATE_APPLIED"/> after an update attempt.
/// </summary>
public static partial class Updater
{
    public const string ProjectUrl = "https://github.com/matt020191/NETDock";
    private const string LatestApi = "https://api.github.com/repos/matt020191/NETDock/releases/latest";
    /// <summary>wParam: 1 = newer release, 0 = up to date, -1 = failed. lParam: 1 when the user asked for the check.</summary>
    public const uint WM_UPDATE_FOUND = Win32.WM_APP + 7;
    /// <summary>wParam: 1 = new exe in place and started (exit now), 0 = failed (<see cref="LastError"/> says why).</summary>
    public const uint WM_UPDATE_APPLIED = Win32.WM_APP + 8;

    public sealed record Release(Version Version, string Tag, string Notes, string ExeUrl, string ShaUrl, string PageUrl);

    /// <summary>A newer release found by the last check, or null.</summary>
    public static Release? Available { get; private set; }

    /// <summary>Why the last update attempt failed (shown to the user).</summary>
    public static string? LastError { get; private set; }

    private static int _applying;
    public static bool Applying => Volatile.Read(ref _applying) == 1;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NETDock-updater/" + AppVersion.Text);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Check in the background; posts <see cref="WM_UPDATE_FOUND"/>.</summary>
    public static void CheckAsync(nint notify, bool manual) => Task.Run(async () =>
    {
        nint result;
        try
        {
            Available = await Latest() is { } r && r.Version > AppVersion.Current ? r : null;
            result = Available is null ? 0 : 1;
        }
        catch (Exception ex) { App.Log(ex); result = -1; }
        Win32.PostMessage(notify, WM_UPDATE_FOUND, result, manual ? 1 : 0);
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
        return new Release(AppVersion.Normalize(version), tag, notes, exe, sha, page);
    }

    /// <summary>Download, verify and swap in the new exe on a worker thread, then start it; posts <see cref="WM_UPDATE_APPLIED"/>.</summary>
    public static void ApplyAsync(Release r, nint notify)
    {
        if (Interlocked.Exchange(ref _applying, 1) == 1) return;
        Task.Run(async () =>
        {
            var ok = false;
            try { await Apply(r); ok = true; LastError = null; }
            catch (Exception ex) { App.Log(ex); LastError = ex.Message; }
            finally { if (!ok) Volatile.Write(ref _applying, 0); }
            Win32.PostMessage(notify, WM_UPDATE_APPLIED, ok ? 1 : 0, 0);
        });
    }

    private static async Task Apply(Release r)
    {
        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;
        var download = Path.Combine(dir, "NETDock.exe.new");
        var old = exe + ".old";
        try
        {
            var bytes = await Http.GetByteArrayAsync(r.ExeUrl);
            var shaText = await Http.GetStringAsync(r.ShaUrl);
            var expected = shaText.Trim().Split(' ', '\t', '\r', '\n')[0];
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.T("The download doesn't match its checksum."));
            await File.WriteAllBytesAsync(download, bytes);
            switch (VerifySignature(download))
            {
                case SignatureCheck.Valid: break;
                case SignatureCheck.RevocationUnavailable:
                    throw new InvalidDataException(L.T("Couldn't check the download's certificate with its issuer (no connection to the revocation server). Try again later."));
                default:
                    throw new InvalidDataException(L.T("The download isn't signed by the NETDock publisher."));
            }

            // A running exe can be renamed but not overwritten: move it aside, put the new one in its place.
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { await MoveWithRetry(download, exe); }
            catch
            {
                File.Move(old, exe); // never leave the folder without NETDock.exe
                throw;
            }
            Process.Start(new ProcessStartInfo(exe, "--after-update") { UseShellExecute = false });
        }
        finally
        {
            try { if (File.Exists(download)) File.Delete(download); } catch { /* best effort */ }
        }
    }

    /// <summary>A just-written exe is often held open for a moment by antivirus: retry the move briefly.</summary>
    private static async Task MoveWithRetry(string from, string to)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(from, to); return; }
            catch (IOException) when (attempt < 10) { await Task.Delay(300); }
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

    // ---- Authenticode: WinVerifyTrust for a valid chain, then the signer's organization

    public enum SignatureCheck { Valid, NotSigned, WrongPublisher, RevocationUnavailable }

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
    private const int CRYPT_E_REVOCATION_OFFLINE = unchecked((int)0x80092013), CERT_E_REVOCATION_FAILURE = unchecked((int)0x800B010E);

    public static bool SignedByPublisher(string file) => VerifySignature(file) == SignatureCheck.Valid;

    public static unsafe SignatureCheck VerifySignature(string file)
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
            var hr = WinVerifyTrust(-1 /* INVALID_HANDLE_VALUE: no UI */, &action, &data);
            if (hr is CRYPT_E_REVOCATION_OFFLINE or CERT_E_REVOCATION_FAILURE) return SignatureCheck.RevocationUnavailable;
            if (hr != 0) return SignatureCheck.NotSigned;
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
        return orgs.Count == 1 && string.Equals(orgs[0], Installer.Publisher, StringComparison.Ordinal) ? SignatureCheck.Valid : SignatureCheck.WrongPublisher;
    }
}

/// <summary>The app's version, from VERSION via the assembly version, always as major.minor.patch.</summary>
public static class AppVersion
{
    public static Version Current { get; } = Normalize(typeof(AppVersion).Assembly.GetName().Version ?? new Version(0, 0, 0));
    public static string Text => Current.ToString(3);
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
