using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Sidetalk.Core;

public static class Authenticode
{
    public static void RequirePublisher(string path, params string[] allowedPublishers)
    {
        ProfileStore.RejectReparseAncestors(path);
        if (!File.Exists(path)) throw new FileNotFoundException("프로그램 파일이 없습니다.", path);
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, pointer, false);
        try
        {
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = pointer, StateAction = 1, ProviderFlags = 0x1000 };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            data.StateAction = 2;
            _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (result != 0) throw new InvalidDataException($"공식 서명을 검증하지 못했습니다 (0x{result:X8}): {Path.GetFileName(path)}");
            // X509CertificateLoader does not extract Authenticode certificates.
            // Limit the legacy importer to signed executables, never PFX/key material.
            if (X509Certificate2.GetCertContentType(path) != X509ContentType.Authenticode)
                throw new InvalidDataException("실행 파일의 Authenticode 인증서를 확인하지 못했습니다.");
#pragma warning disable SYSLIB0057 // Authenticode extraction has no X509CertificateLoader replacement.
            using var certificate = new X509Certificate2(path);
#pragma warning restore SYSLIB0057
            var publisher = certificate.GetNameInfo(X509NameType.SimpleName, false);
            if (!allowedPublishers.Any(allowed => publisher.Equals(allowed, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"확인되지 않은 배포자입니다: {publisher}");
        }
        finally { Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr File; public IntPtr KnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size; public IntPtr PolicyCallback; public IntPtr SipClient; public uint UiChoice;
        public uint RevocationChecks; public uint UnionChoice; public IntPtr File; public uint StateAction;
        public IntPtr StateData; public IntPtr Url; public uint ProviderFlags; public uint UiContext; public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);
}
