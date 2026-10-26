using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BoothFreeCollector;

internal static class OfficialExecutableTrust
{
    // Verify before passing an ephemeral download capability to an executable.
    // Keep the file open without write/delete sharing through process creation.
    public static FileStream OpenVerified(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            VerifySignature(path, file.SafeFileHandle.DangerousGetHandle());
            // The loader accepts certificate formats, not signed PE files. This
            // API only extracts the signer after WinVerifyTrust has authenticated it.
#pragma warning disable SYSLIB0057
            using var signed = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate = X509CertificateLoader.LoadCertificate(signed.GetRawCertData());
            if (certificate.GetNameInfo(X509NameType.SimpleName, false) != "pixiv Inc.")
                throw new IOException("公式Library Managerの発行元を確認できません");
            return file;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            file.Dispose();
            throw new IOException("公式Library Managerの署名を確認できません。公式配布版を起動してください", ex);
        }
        catch { file.Dispose(); throw; }
    }
    static void VerifySignature(string path, nint handle)
    {
        nint name = Marshal.StringToCoTaskMemUni(path), info = 0;
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1,
            StateAction = 1, ProviderFlags = 0x1000 | 0x10 | 0x2000 };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = name, Handle = handle };
            info = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
            Marshal.StructureToPtr(file, info, false); data.File = info;
            // No UI or network requests. Windows verifies the embedded signature,
            // certificate chain, code-signing use and timestamp. Revocation is not
            // checked here; that limitation is documented rather than hidden.
            if (WinVerifyTrust(new nint(-1), ref action, ref data) != 0)
                throw new IOException("コード署名の信頼性を確認できません");
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new nint(-1), ref action, ref data);
            if (info != 0) Marshal.FreeHGlobal(info);
            Marshal.FreeCoTaskMem(name);
        }
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
    [StructLayout(LayoutKind.Sequential)]
    struct TrustFile { public uint Size; public nint Path, Handle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    struct TrustData
    {
        public uint Size;
        public nint PolicyCallback, SipClient;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public nint File;
        public uint StateAction;
        public nint State, UrlReference;
        public uint ProviderFlags, UiContext;
        public nint SignatureSettings;
    }
}
