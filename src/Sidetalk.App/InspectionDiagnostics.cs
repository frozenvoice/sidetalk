using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sidetalk.Core;

namespace Sidetalk.App;

/// <summary>Best-effort UI inspection evidence kept separate from workflow authority.</summary>
public static class InspectionDiagnostics
{
    private const int MaximumInMemoryFailures = 64;
    private static readonly object Gate = new();
    private static readonly List<InspectionFailure> Failures = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Optional test/fixture destination. Production defaults to the local SIDETALK diagnostics folder.</summary>
    public static string? OutputDirectoryOverride { get; set; }

    public static IReadOnlyList<InspectionFailure> Snapshot()
    {
        lock (Gate)
        {
            return Failures.ToArray();
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Failures.Clear();
        }
    }

    public static void Record(string stage, Exception exception, bool persist)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(exception);
            Record(InspectionFailure.Capture(stage, exception), persist);
        }
        catch
        {
            // Diagnostics must never change the recovery or inspection result.
        }
    }

    public static void Record(InspectionFailure failure, bool persist)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(failure);
            lock (Gate)
            {
                if (Failures.Any(existing => IsSameFailure(existing, failure)))
                {
                    return;
                }

                if (Failures.Count >= MaximumInMemoryFailures)
                {
                    Failures.RemoveAt(0);
                }

                Failures.Add(failure);
            }

            if (persist)
            {
                TryWrite(failure);
            }
        }
        catch
        {
            // Diagnostics must never change the recovery or inspection result.
        }
    }

    /// <summary>
    /// Adds UI-local failures to an already-created controller diagnostic. A failed
    /// atomic replacement leaves the controller file intact and uses a new sidecar instead.
    /// </summary>
    public static void TryAttachToExport(string path)
    {
        string? temporaryPath = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var failures = Snapshot();
            if (failures.Count == 0 || !File.Exists(path))
            {
                return;
            }

            var fullPath = Path.GetFullPath(path);
            var json = File.ReadAllText(fullPath, Encoding.UTF8);
            if (JsonNode.Parse(json) is not JsonObject report)
            {
                TryWriteSidecar(fullPath, failures);
                return;
            }

            report["UiInspectionFailures"] = JsonSerializer.SerializeToNode(failures, JsonOptions);
            temporaryPath = fullPath + ".ui-tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporaryPath, report.ToJsonString(JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
            temporaryPath = null;
        }
        catch
        {
            try
            {
                TryWriteSidecar(path, Snapshot());
            }
            catch
            {
                // A diagnostic failure must not replace the selected export or surface a second error.
            }
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private static bool IsSameFailure(InspectionFailure left, InspectionFailure right) =>
        string.Equals(left.Stage, right.Stage, StringComparison.Ordinal) &&
        string.Equals(left.ExceptionType, right.ExceptionType, StringComparison.Ordinal) &&
        string.Equals(left.Message, right.Message, StringComparison.Ordinal) &&
        string.Equals(left.StackTrace, right.StackTrace, StringComparison.Ordinal);

    private static void TryWrite(InspectionFailure failure)
    {
        try
        {
            var directory = OutputDirectoryOverride;
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SIDETALK",
                    "diagnostics");
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"ui-inspection-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(failure, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Logging is deliberately best effort.
        }
    }

    private static void TryWriteSidecar(string path, IReadOnlyList<InspectionFailure> failures)
    {
        if (failures.Count == 0 || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var sidecar = path + ".ui-errors.json";
        try
        {
            using var stream = new FileStream(sidecar, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, failures, JsonOptions);
        }
        catch
        {
            // Never overwrite an existing sidecar or the original export.
        }
    }
}
