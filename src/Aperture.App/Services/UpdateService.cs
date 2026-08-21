using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Aperture.Core.Startup;
using Aperture.Core.Updates;

namespace Aperture.App.Services;

/// <summary>
/// Checks GitHub for newer releases, downloads the signed single-file exe, and
/// places it next to the running one (portable apply — Aperture has no installer).
///
/// The downloaded file must carry a valid Authenticode signature that chains to
/// Microsoft's identity-verification root before it is started. Publisher
/// Organization is not a fail condition: Azure Trusted Signing may emit
/// "Funcular Labs, Inc." while the csproj Company is "Funcular Labs", and a
/// mismatch must not block an otherwise-trusted binary.
/// </summary>
internal static class UpdateService
{
    private const string ReleasesApi =
        "https://api.github.com/repos/FuncularLabs/Aperture/releases?per_page=20";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Aperture-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Newest stable + newest prerelease, or null when offline/rate-limited.</summary>
    public static async Task<UpdateCheck?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var json = await Http.GetStringAsync(ReleasesApi, cts.Token);
            return ReleaseFeed.Select(json);
        }
        catch { return null; }
    }

    /// <summary>Download a release's exe to a temp path (verified separately).</summary>
    public static async Task<string?> DownloadAsync(ReleaseInfo release, CancellationToken cancellationToken = default)
    {
        if (release.AssetUrl is null || release.AssetName is null) return null;
        string? dest = null;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Aperture-update");
            Directory.CreateDirectory(dir);
            // AssetName comes from the release JSON — never let it steer the path.
            dest = Path.Combine(dir, Path.GetFileName(release.AssetName));
            if (File.Exists(dest)) File.Delete(dest);

            using var response = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var fs = File.Create(dest))
                await response.Content.CopyToAsync(fs, cancellationToken);

            if (release.AssetSize > 0 && new FileInfo(dest).Length != release.AssetSize)
            {
                File.Delete(dest);
                return null;
            }
            return dest;
        }
        catch
        {
            try { if (dest is not null && File.Exists(dest)) File.Delete(dest); } catch { /* leftover; startup sweep retries */ }
            return null;
        }
    }

    private const string TrustedRootThumbprint = "F40042E2E5F7E8EF8189FED15519AECE42C3BFA2"; // MS Identity Verification Root CA 2020

    /// <summary>
    /// True only when the file carries a valid embedded Authenticode signature
    /// (WinVerifyTrust: hash + trust chain), the chain roots at Microsoft's
    /// identity-verification root, and no chain cert is known-revoked.
    /// Organization mismatch does not fail the check.
    /// </summary>
    public static bool VerifySignature(string filePath, out string signer)
    {
        signer = "";
        try
        {
            if (WinVerifyTrustFile(filePath) != 0) return false;

            // CreateFromSignedFile is flagged SYSLIB0057, but X509CertificateLoader
            // only loads certificate *files* — there is no non-obsolete managed API
            // to pull a signer out of a signed PE.
#pragma warning disable SYSLIB0057
            var signed = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            using var leaf = X509CertificateLoader.LoadCertificate(signed.GetRawCertData());
            signer = leaf.Subject;

            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
            // WinVerifyTrust already established trust; here we only need the chain
            // to read the root and to surface a definitive revocation. Transient
            // CRL/OCSP unavailability must NOT block a legitimate update.
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown
                | X509VerificationFlags.IgnoreEndRevocationUnknown
                | X509VerificationFlags.IgnoreRootRevocationUnknown;
            chain.Build(leaf);

            var rootPinned = false;
            foreach (var el in chain.ChainElements)
            {
                foreach (var st in el.ChainElementStatus)
                    if (st.Status.HasFlag(X509ChainStatusFlags.Revoked)) return false;
                if (string.Equals(el.Certificate.Thumbprint, TrustedRootThumbprint, StringComparison.OrdinalIgnoreCase))
                    rootPinned = true;
            }
            return rootPinned;
        }
        catch { return false; }
    }

    /// <summary>Where the current process's exe lives (single-file publish safe).</summary>
    public static string CurrentExePath => Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("Cannot determine the running exe path.");

    /// <summary>
    /// The version of the exe sitting at <see cref="CurrentExePath"/> right now,
    /// which is NOT necessarily the version this process is running.
    /// </summary>
    public static UpdateVersion? VersionOnDisk()
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(CurrentExePath);
            return UpdateVersion.Parse(info.ProductVersion ?? info.FileVersion);
        }
        catch { return null; }
    }

    public static bool AlreadyUpdatedOnDisk(
        UpdateVersion wanted, UpdateVersion? running, out UpdateVersion? onDisk)
    {
        onDisk = VersionOnDisk();
        return UpdateOffer.NeedsRestartNotUpdate(onDisk, wanted, running);
    }

    /// <summary>
    /// Portable flow: place the (already verified) versioned exe next to the
    /// running one and start it. Returns the new exe path.
    /// </summary>
    public static string ApplyPortableAndRestart(string verifiedNewExe, string assetName)
    {
        var current = CurrentExePath;
        var dir = Path.GetDirectoryName(current)!;
        var safeName = Path.GetFileName(assetName);
        var dest = Path.Combine(dir, safeName);
        if (string.Equals(dest, current, StringComparison.OrdinalIgnoreCase))
            dest = Path.Combine(dir, Path.GetFileNameWithoutExtension(safeName) + ".new.exe");

        if (!SameFile(verifiedNewExe, dest))
        {
            try
            {
                File.Copy(verifiedNewExe, dest, overwrite: true);
            }
            catch (IOException) when (SameFile(verifiedNewExe, dest))
            {
                // Lost a race with a sibling writing the identical file. Nothing to do.
            }
            catch (IOException ex) when (File.Exists(dest) && IsSharingViolation(ex))
            {
                throw new InvalidOperationException(
                    $"{Path.GetFileName(dest)} is already here and in use — most likely " +
                    "another Aperture window is running it. Close that window and try again.", ex);
            }
        }

        var psi = new ProcessStartInfo(dest) { UseShellExecute = true };
        if (Process.Start(psi) is null)
            throw new InvalidOperationException(
                "Couldn't start the new version. The downloaded file is next to the current exe.");

        // If this exe was registered to start with Windows, point the Run key at
        // the new file so the next logon is the version just applied.
        try
        {
            var store = new CurrentUserRunKeyStore();
            if (WindowsStartup.IsEnabled(store, current))
                WindowsStartup.TrySetEnabled(store, dest, enabled: true);
        }
        catch { /* swap already landed; a Run-key refresh isn't fatal */ }

        return dest;
    }

    /// <summary>Same length and same SHA-256.</summary>
    internal static bool SameFile(string a, string b) => Compare(a, b) == FileMatch.Same;

    internal enum FileMatch { Same, Different, CouldNotRead }

    internal static FileMatch Compare(string a, string b)
    {
        try
        {
            if (!File.Exists(a) || !File.Exists(b)) return FileMatch.Different;
            if (new FileInfo(a).Length != new FileInfo(b).Length) return FileMatch.Different;
            using var sa = File.OpenRead(a);
            using var sb = File.OpenRead(b);
            return System.Security.Cryptography.SHA256.HashData(sa)
                .AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(sb))
                ? FileMatch.Same : FileMatch.Different;
        }
        catch { return FileMatch.CouldNotRead; }
    }

    /// <summary>ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33).</summary>
    internal static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    /// <summary>
    /// Best-effort sweep of leftover downloads in %TEMP%\Aperture-update. Never
    /// throws — this runs at startup.
    /// </summary>
    public static void CleanupOldBinaries()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Aperture-update");
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try { File.Delete(file); } catch { /* still in use — next launch */ }
            }
        }
        catch { /* best effort */ }
    }

    // ---- WinVerifyTrust (full Authenticode policy check) ----

    private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static int WinVerifyTrustFile(string filePath)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath,
        };
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = 2,           // WTD_UI_NONE
            fdwRevocationChecks = 0,  // WTD_REVOKE_NONE here; VerifySignature does explicit revocation
            dwUnionChoice = 1,        // WTD_CHOICE_FILE
            dwStateAction = 1,        // WTD_STATEACTION_VERIFY
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            data.pFile = fileInfoPtr;
            var action = ActionGenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.dwStateAction = 2;   // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result;
        }
        finally { Marshal.FreeHGlobal(fileInfoPtr); }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WINTRUST_DATA data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
