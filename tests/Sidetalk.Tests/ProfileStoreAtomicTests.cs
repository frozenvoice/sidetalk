using System.Text;
using System.Text.Json;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class ProfileStoreAtomicTests
{
    public static IEnumerable<(string Name, Action Run)> Cases =>
    [
        ("ProfileStore leaves a failed staged write for diagnosis and retry", FailedStageIsRetainedForRetry),
        ("ProfileStore does not overwrite a profile published by a race", PublishedRaceIsLoadedWithoutOverwrite),
        ("ProfileStore rejects a corrupt racing winner and preserves both files", CorruptRacingWinnerFailsClosed)
    ];

    private static void FailedStageIsRetainedForRetry()
    {
        var root = TestContext.CreateRunDirectory("profile-atomic-failure");
        var store = new ProfileStore(root, profileWriter: (stream, _) =>
        {
            var partial = Encoding.UTF8.GetBytes("{\"Schema\":1");
            stream.Write(partial, 0, partial.Length);
            throw new InvalidOperationException("simulated interrupted profile write");
        });

        Assert.Throws<InvalidOperationException>(() => store.Create());
        Assert.False(File.Exists(store.ProfilePath), "A failed staged write must never publish profile.json.");

        var staged = GetStagedFiles(root);
        Assert.Equal(1, staged.Length);
        Assert.Equal("{\"Schema\":1", File.ReadAllText(staged[0], Encoding.UTF8));

        var retry = new ProfileStore(root);
        var profile = retry.Create();
        Assert.Equal(profile, retry.Load());
        Assert.True(File.Exists(staged[0]), "Failed staged data must be retained for diagnosis.");
    }

    private static void PublishedRaceIsLoadedWithoutOverwrite()
    {
        var root = TestContext.CreateRunDirectory("profile-atomic-race");
        var competingName = "SideTalk_" + new string('a', 23);
        var competing = new BoxProfile(
            1,
            competingName,
            new string('b', 32),
            ProfileStore.CurrentSid(),
            Path.Combine(root, "Boxes", competingName));
        var finalPath = Path.Combine(root, "profile.json");
        var stagedWasComplete = false;
        var store = new ProfileStore(root, beforePublish: (stagedPath, generated) =>
        {
            var staged = JsonSerializer.Deserialize<BoxProfile>(File.ReadAllText(stagedPath));
            Assert.Equal(generated, staged);
            stagedWasComplete = true;

            using var stream = new FileStream(finalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, competing, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(true);
        });

        var result = store.Create();

        Assert.True(stagedWasComplete, "The race must happen after the staged profile was fully written.");
        Assert.Equal(competing, result);
        Assert.Equal(competing, store.Load());
        Assert.Equal(competing, JsonSerializer.Deserialize<BoxProfile>(File.ReadAllText(finalPath)));
        Assert.Equal(1, GetStagedFiles(root).Length);
    }

    private static void CorruptRacingWinnerFailsClosed()
    {
        var root = TestContext.CreateRunDirectory("profile-atomic-corrupt-race");
        var finalPath = Path.Combine(root, "profile.json");
        const string corrupt = "{\"Schema\":1,";
        var store = new ProfileStore(root, beforePublish: (_, _) =>
        {
            using var stream = new FileStream(finalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var bytes = Encoding.UTF8.GetBytes(corrupt);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        });

        Assert.Throws<Exception>(() => store.Create());
        Assert.Equal(corrupt, File.ReadAllText(finalPath, Encoding.UTF8));
        Assert.Equal(1, GetStagedFiles(root).Length);
    }

    private static string[] GetStagedFiles(string root) => Directory.GetFiles(root, ".profile.*.tmp");
}