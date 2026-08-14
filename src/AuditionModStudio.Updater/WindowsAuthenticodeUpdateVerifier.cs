using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AuditionModStudio.Updater;

public sealed class WindowsAuthenticodeUpdateVerifier : IAuthenticodeUpdateVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public Task<AuthenticodeVerificationResult> VerifyAsync(string artifactPath,
        string expectedPublisherSubject, string expectedPublisherThumbprint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(artifactPath)
            || !File.Exists(artifactPath) || string.IsNullOrWhiteSpace(expectedPublisherSubject)
            || !ValidThumbprint(expectedPublisherThumbprint))
            return Task.FromResult(AuthenticodeVerificationResult.Failure("AUTHENTICODE_REQUEST_INVALID"));

        var trust = VerifyTrust(artifactPath);
        if (trust != 0)
            return Task.FromResult(AuthenticodeVerificationResult.Failure("AUTHENTICODE_TRUST_REJECTED"));
        try
        {
#pragma warning disable SYSLIB0057 // WinTrust validates first; this API extracts the embedded signer certificate from PE/MSI/MSIX.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(artifactPath));
#pragma warning restore SYSLIB0057
            var thumbprint = Normalize(certificate.Thumbprint);
            if (!string.Equals(certificate.Subject, expectedPublisherSubject, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(thumbprint, Normalize(expectedPublisherThumbprint), StringComparison.Ordinal))
                return Task.FromResult(AuthenticodeVerificationResult.Failure("AUTHENTICODE_PUBLISHER_MISMATCH"));
            return Task.FromResult(AuthenticodeVerificationResult.Success());
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return Task.FromResult(AuthenticodeVerificationResult.Failure("AUTHENTICODE_CERTIFICATE_INVALID"));
        }
    }

    private static int VerifyTrust(string path)
    {
        var fileInfo = new WinTrustFileInfo { StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = path };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var dataPointer = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfoPointer = filePointer,
                StateAction = 0,
                ProviderFlags = 0x00000080,
                UiContext = 0
            };
            dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, dataPointer, false);
            var action = GenericVerifyV2;
            return WinVerifyTrust(new IntPtr(-1), ref action, dataPointer);
        }
        finally
        {
            if (dataPointer != IntPtr.Zero) Marshal.FreeHGlobal(dataPointer);
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    private static bool ValidThumbprint(string value) => Normalize(value).Length == 40
        && Normalize(value).All(Uri.IsHexDigit);
    private static string Normalize(string value) => string.Concat(value.Where(Uri.IsHexDigit)).ToUpperInvariant();

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfoPointer;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
