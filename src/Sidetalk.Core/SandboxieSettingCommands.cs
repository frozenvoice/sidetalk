namespace Sidetalk.Core;

internal sealed record SandboxieSettingCommand(IReadOnlyList<string> Arguments, IReadOnlyList<string> ExpectedValues);

internal static class SandboxieSettingCommands
{
    public static IReadOnlyList<SandboxieSettingCommand> Replace(SetupRecoveryChange change)
    {
        change = change.Normalize();
        if (change.DesiredValues.Count == 0) throw new InvalidDataException("설정 전체 삭제는 이 복구 명령 범위에 없습니다.");
        var steps = new List<SandboxieSettingCommand>();
        var actual = change.CurrentValues.ToArray();
        var start = actual.Length <= change.DesiredValues.Count && actual.SequenceEqual(change.DesiredValues.Take(actual.Length), StringComparer.Ordinal) ? actual.Length : 0;
        for (var index = start; index < change.DesiredValues.Count; index++)
        {
            var expected = change.DesiredValues.Take(index + 1).ToArray();
            if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
                steps.Add(new SandboxieSettingCommand([index == 0 ? "set" : "append", change.Section, change.Key, change.DesiredValues[index]], expected));
            actual = expected;
        }
        return steps;
    }
}