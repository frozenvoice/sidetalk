using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class AuthenticodeCompatibilityTests
{
    public static IEnumerable<(string Name, Action Run)> Cases =>
    [
        ("Authenticode accepts a signed runtime copy only for its publisher", SignedRuntimeCopy),
        ("Authenticode rejects a modified signed runtime copy", ModifiedRuntimeCopy),
        ("Authenticode rejects certificates and PFX disguised as executables", CertificateFiles)
    ];

    private static string CopyRuntime()
    {
        // The installed runtime is only read. Never use or modify a real application installation.
        var source = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");
        var target = Path.Combine(TestContext.CreateRunDirectory("서명 검사"), "signed runtime.dll");
        File.Copy(source, target);
        return target;
    }

    private static void SignedRuntimeCopy()
    {
        var path = CopyRuntime();
        var original = SHA256.HashData(File.ReadAllBytes(path));
        Authenticode.RequirePublisher(path, ".NET", "Microsoft Corporation");
        var error = Assert.Throws<InvalidDataException>(() => Authenticode.RequirePublisher(path, "SIDETALK test publisher"));
        Assert.Contains("배포자", error.Message);
        Assert.SequenceEqual(original, SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static void ModifiedRuntimeCopy()
    {
        var path = CopyRuntime();
        var bytes = File.ReadAllBytes(path);
        // Change the DOS stub (covered by Authenticode), leaving the PE structure intact.
        bytes[0x40] ^= 1;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => Authenticode.RequirePublisher(path, ".NET", "Microsoft Corporation"));
        Assert.SequenceEqual(bytes, File.ReadAllBytes(path));
    }

    private static void CertificateFiles()
    {
        var root = TestContext.CreateRunDirectory("certificate-not-executable");
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=SIDETALK test publisher", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        foreach (var type in new[] { X509ContentType.Cert, X509ContentType.Pfx })
        {
            var path = Path.Combine(root, type + ".exe");
            var bytes = certificate.Export(type);
            File.WriteAllBytes(path, bytes);
            Assert.Throws<InvalidDataException>(() => Authenticode.RequirePublisher(path, "SIDETALK test publisher"));
            Assert.SequenceEqual(bytes, File.ReadAllBytes(path));
        }
    }
}
