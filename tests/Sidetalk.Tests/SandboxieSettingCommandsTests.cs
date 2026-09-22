using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class SandboxieSettingCommandsTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases =
    [
        ("SbieIni commands: multiple ordered values use separate set and append", MultipleValues),
        ("SbieIni commands: partial ordered prefix resumes without duplicate writes", PartialPrefix),
        ("SbieIni commands: reject newline injection and whole-setting deletion", RejectUnsafe)
    ];
    private static SetupRecoveryChange Change(string[] current, string[] desired) =>
        new("SideTalk_test", "WriteKeyPath", current, desired, SetupRecoveryChangeKind.AddOwnedBoxSetting);
    private static void MultipleValues()
    {
        var steps = SandboxieSettingCommands.Replace(Change([], ["first", "second", "third"]));
        Assert.Equal(3, steps.Count);
        Assert.SequenceEqual(new[] { "set", "append", "append" }, steps.Select(step => step.Arguments[0]));
        Assert.SequenceEqual(new[] { "first", "second", "third" }, steps[^1].ExpectedValues);
        Assert.False(steps.SelectMany(step => step.Arguments).Any(value => value.Contains('\n') || value.Contains('\r')));
    }
    private static void PartialPrefix()
    {
        var steps = SandboxieSettingCommands.Replace(Change(["first", "second"], ["first", "second", "third"]));
        Assert.Equal(1, steps.Count);
        Assert.Equal("append", steps[0].Arguments[0]);
        Assert.Equal("third", steps[0].Arguments[^1]);
        Assert.Equal(0, SandboxieSettingCommands.Replace(Change(["first"], ["first"])).Count);
    }
    private static void RejectUnsafe()
    {
        Assert.Throws<InvalidDataException>(() => SandboxieSettingCommands.Replace(Change([], ["first\nOther=value"])));
        Assert.Throws<InvalidDataException>(() => SandboxieSettingCommands.Replace(Change(["first"], [])));
    }
}