using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PCAnalyzer.Gaming;

public sealed record PresentMonSignatureInspection(PresentMonSignatureStatus Status, string? Publisher, string Detail);

/// <summary>Checks Authenticode locally before the selected executable is allowed to run, including --help.</summary>
public static class PresentMonSignatureVerifier
{
    public static PresentMonSignatureInspection Inspect(string path)
    {
        if (!OperatingSystem.IsWindows())
            return new(PresentMonSignatureStatus.Unavailable, null, "A assinatura Authenticode só pode ser verificada no Windows.");
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, memory, false);
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1,
                FileInfo = memory, StateAction = 1,
                // Verify chain/revocation using the local Windows cache; never block the UI on certificate downloads.
                ProviderFlags = 0x1000 | 0x80
            };
            int status;
            try { status = WinVerifyTrust(new IntPtr(-1), ref action, ref data); }
            finally
            {
                data.StateAction = 2; // WTD_STATEACTION_CLOSE
                _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
            if (status != 0)
                return new(status == unchecked((int)0x800B0100) ? PresentMonSignatureStatus.Unsigned : PresentMonSignatureStatus.Invalid,
                    null, $"O Windows não confirmou uma assinatura confiável (0x{status:X8}). O executável não foi iniciado; importe o CSV ou obtenha uma versão oficial assinada.");
            // X509CertificateLoader reads certificates, not Authenticode certificates embedded
            // in PE executables. Keep this extraction API scoped until .NET exposes a replacement;
            // WinVerifyTrust above performs trust validation before the publisher is inspected.
#pragma warning disable SYSLIB0057
            using var signed = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate = X509CertificateLoader.LoadCertificate(signed.Export(X509ContentType.Cert));
            var publisher = certificate.GetNameInfo(X509NameType.SimpleName, false);
            return IsExpectedPublisher(publisher)
                ? new(PresentMonSignatureStatus.TrustedPublisher, publisher, "Assinatura Authenticode e editor oficial reconhecidos pelo Windows: " + publisher + ".")
                : new(PresentMonSignatureStatus.UnrecognizedPublisher, publisher,
                    "A assinatura é válida, mas o editor não corresponde às distribuições reconhecidas de PresentMon (Intel/Microsoft). O executável não foi iniciado.");
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            return new(PresentMonSignatureStatus.Unavailable, null, "Não foi possível confirmar a assinatura; o executável não foi iniciado. " + ex.Message);
        }
        finally
        {
            Marshal.DestroyStructure<TrustFile>(memory);
            Marshal.FreeHGlobal(memory);
        }
    }

    public static bool IsExpectedPublisher(string? publisher) => publisher is not null &&
        (publisher.Equals("Intel Corporation", StringComparison.OrdinalIgnoreCase) ||
         publisher.Equals("Intel(R) Corporation", StringComparison.OrdinalIgnoreCase) ||
         publisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase));

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        // Native WINTRUST_DATA, including the optional Windows 8+ signature-settings pointer.
        // Natural pointer alignment is required: 88 bytes on x64, 52 bytes on x86.
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
