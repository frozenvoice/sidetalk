using System.Text.RegularExpressions;

namespace Sidetalk.Core;

/// <summary>Sanitized exception evidence; contains no process tokens or account data.</summary>
public sealed record InspectionFailure(string Stage, DateTimeOffset OccurredAtUtc, string ExceptionType,
    string Message, string? StackTrace, IReadOnlyList<InspectionFailure> InnerExceptions)
{
    public static InspectionFailure Capture(string stage, Exception exception) => Capture(stage, exception, 0);

    private static InspectionFailure Capture(string stage, Exception exception, int depth)
    {
        var inner = depth >= 16 ? Array.Empty<InspectionFailure>() :
            exception is AggregateException aggregate
                ? aggregate.InnerExceptions.Select(error => Capture(stage, error, depth + 1)).ToArray()
                : exception.InnerException is { } nested ? [Capture(stage, nested, depth + 1)] : Array.Empty<InspectionFailure>();
        return new(stage, DateTimeOffset.UtcNow, exception.GetType().FullName ?? exception.GetType().Name,
            Sanitize(exception.Message), exception.StackTrace is { } stack ? Sanitize(stack) : null, inner);
    }

    public static string Sanitize(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"(?i)[A-Z]:\\Users\\[^\\\r\n""<>]+", "%USERPROFILE%");
        text = Regex.Replace(text, @"\bS-1-\d+(?:-\d+){1,15}\b", "[user SID redacted]");
        text = Regex.Replace(text, @"(?i)SIDETALK:[^\s""<>]+", "SIDETALK:[redacted]");
        text = Regex.Replace(text, @"(?i)(OwnerToken\s*[=:]\s*)[^\s,""<>]+", "$1[redacted]");
        return text;
    }
}
