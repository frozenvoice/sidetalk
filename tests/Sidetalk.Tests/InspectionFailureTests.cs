using System.Text.Json;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class InspectionFailureTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases =
    [
        ("Inspection failure retains stage, exception chain and stack with redaction", CapturesSanitizedChain)
    ];

    private static void CapturesSanitizedChain()
    {
        Exception caught;
        try
        {
            try { throw new MissingMethodException(".ctor"); }
            catch (Exception inner)
            {
                throw new InvalidOperationException(@"C:\Users\ExampleUser\file.json SIDETALK:example-owner S-1-5-21-123-456-789-1001", inner);
            }
        }
        catch (Exception error) { caught = error; }
        var failure = InspectionFailure.Capture("복구 상태 조회", caught);
        var json = JsonSerializer.Serialize(failure);
        Assert.Equal("복구 상태 조회", failure.Stage);
        Assert.Equal("System.MissingMethodException", failure.InnerExceptions.Single().ExceptionType);
        Assert.True(failure.StackTrace?.Contains(nameof(CapturesSanitizedChain), StringComparison.Ordinal) == true);
        Assert.True(failure.InnerExceptions.Single().StackTrace is not null);
        Assert.False(json.Contains("ExampleUser", StringComparison.Ordinal));
        Assert.False(json.Contains("example-owner", StringComparison.Ordinal));
        Assert.False(json.Contains("S-1-5-21-123-456-789-1001", StringComparison.Ordinal));
    }
}
