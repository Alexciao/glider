using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Glider.Core.Services;

namespace Glider.Core.Game;

/// <summary>
/// Real <see cref="IPublisherCheck"/>: a valid Authenticode signature (WinVerifyTrust, no UI, revocation from cache only so
/// it never blocks on the network) whose signer is Epic Games / Easy Anti-Cheat. Used before Glider asks Windows to run
/// something elevated from the game folder, which Legendary created unelevated and any program of this user can write.
/// </summary>
public sealed class AuthenticodePublisherCheck : IPublisherCheck
{
    private static readonly string[] TrustedPublishers = ["Epic Games", "Easy Anti-Cheat"];

    private readonly bool _anyPublisher;

    public AuthenticodePublisherCheck()
    {
    }

    private AuthenticodePublisherCheck(bool anyPublisher) => _anyPublisher = anyPublisher;

    /// <summary>
    /// A valid, intact Authenticode signature from any publisher (redistributables are Microsoft's, middleware vendors' or the game's own).
    /// Still refuses unsigned and tampered files, which is what a swapped file in the user-writable game folder would be.
    /// </summary>
    public static AuthenticodePublisherCheck AnyValidSignature() => new(anyPublisher: true);

    public string? WhyUntrusted(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "signatures can only be checked on Windows";
        }

        return Check(path, _anyPublisher);
    }

    [SupportedOSPlatform("windows")]
    private static string? Check(string path, bool anyPublisher)
    {
        var status = WinTrust.Verify(path);
        if (status != 0)
        {
            return $"it has no valid digital signature (WinVerifyTrust 0x{status:X8})";
        }

        if (anyPublisher)
        {
            return null;
        }

        string subject;
        try
        {
#pragma warning disable SYSLIB0057 // the signer of a signed file; X509CertificateLoader has no equivalent
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            subject = cert.Subject;
        }
        catch (System.Security.Cryptography.CryptographicException e)
        {
            return $"its signer can't be read ({e.Message})";
        }

        return TrustedPublishers.Any(p => subject.Contains(p, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"it is signed by \"{subject}\", not by Epic Games";
    }

    [SupportedOSPlatform("windows")]
    private static class WinTrust
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint UiNone = 2;
        private const uint RevokeNone = 0;
        private const uint ChoiceFile = 1;
        private const uint StateVerify = 1;
        private const uint StateClose = 2;
        private const uint CacheOnlyUrlRetrieval = 0x00001000;

        public static int Verify(string path)
        {
            var file = new FileInfoStruct
            {
                cbStruct = (uint)Marshal.SizeOf<FileInfoStruct>(),
                pcwszFilePath = path,
            };
            var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoStruct>());
            try
            {
                Marshal.StructureToPtr(file, filePtr, false);
                var data = new Data
                {
                    cbStruct = (uint)Marshal.SizeOf<Data>(),
                    dwUIChoice = UiNone,
                    fdwRevocationChecks = RevokeNone,
                    dwUnionChoice = ChoiceFile,
                    pFile = filePtr,
                    dwStateAction = StateVerify,
                    dwProvFlags = CacheOnlyUrlRetrieval,
                };
                var action = GenericVerifyV2;
                var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                data.dwStateAction = StateClose;
                _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return result;
            }
            finally
            {
                Marshal.DestroyStructure<FileInfoStruct>(filePtr);
                Marshal.FreeHGlobal(filePtr);
            }
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref Data data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfoStruct
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Data
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
}
