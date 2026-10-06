using System.Reflection;

namespace Sidetalk.Core;

/// <summary>Product version from Directory.Build.props; never a persisted schema version.</summary>
public static class SidetalkBuild
{
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(SidetalkBuild).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) throw new InvalidOperationException("SIDETALK 제품 버전을 확인하지 못했습니다.");
        // The SDK appends "+<commit>" source metadata; the product version is the part before it.
        return informational.Split('+', 2)[0];
    }
}
