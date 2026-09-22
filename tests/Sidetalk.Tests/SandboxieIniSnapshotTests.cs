using System.Text;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class SandboxieIniSnapshotTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases =
    [
        ("INI snapshot: UTF16 and UTF8 preserve ordered duplicate values and user sections", Encodings),
        ("INI snapshot: physical file and API must agree even after successful command", Mismatch),
        ("INI snapshot: ambiguous syntax and encoding fail closed", Ambiguous)
    ];
    private const string Content = "# preserved comment\r\n[GlobalSettings]\r\nTemplate=WindowsRasMan\r\nTemplate=WindowsLive\r\n[UserSettings_TEST]\r\nSetting=first=second\r\n[Empty]\r\n";
    private static void Encodings()
    {
        var utf8 = Encoding.UTF8.GetBytes(Content);
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Content)).ToArray();
        var parsed = SandboxieIniSnapshot.Parse(utf8);
        SandboxieIniSnapshot.RequireEquivalent(utf16, parsed);
        Assert.SequenceEqual(new[] { "WindowsRasMan", "WindowsLive" }, parsed["GlobalSettings"]["Template"]);
        Assert.Equal("first=second", parsed["UserSettings_TEST"]["Setting"][0]);
    }
    private static void Mismatch()
    {
        var original = SandboxieIniSnapshot.Parse(Encoding.UTF8.GetBytes(Content));
        Assert.Throws<InvalidDataException>(() => SandboxieIniSnapshot.RequireEquivalent(
            Encoding.UTF8.GetBytes(Content.Replace("Template=WindowsLive\r\n", "")), original));
        Assert.Throws<InvalidDataException>(() => SandboxieIniSnapshot.RequireEquivalent(
            Encoding.UTF8.GetBytes(Content.Replace("Template=WindowsRasMan\r\nTemplate=WindowsLive", "Template=WindowsLive\r\nTemplate=WindowsRasMan")), original));
    }
    private static void Ambiguous()
    {
        foreach (var invalid in new[] { "[One]\r\nValue=x\r\n[one]\r\nValue=y", "Value=without-section", "[One]\r\nnot a setting", "[One]\r\nValue=\0" })
            Assert.Throws<InvalidDataException>(() => SandboxieIniSnapshot.Parse(Encoding.UTF8.GetBytes(invalid)));
        Assert.Throws<InvalidDataException>(() => SandboxieIniSnapshot.Parse([0xc3, 0x28]));
    }
}