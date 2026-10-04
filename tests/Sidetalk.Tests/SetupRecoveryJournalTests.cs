using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Sidetalk.Core;

namespace Sidetalk.Tests;

internal static class SetupRecoveryJournalTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases =
    [
        ("Setup recovery blocks an orphan completed journal event", OrphanCompletedBlocks),
        ("Setup recovery blocks changed journal approval metadata", ChangedJournalMetadataBlocks),
        ("Setup recovery blocks duplicate journal sequence numbers", DuplicateJournalSequenceBlocks),
        ("Setup recovery revoke wins after a future-dated grant", RevokeWinsAfterFutureDatedGrant),
        ("Setup consent staging is optional and never grants approval", ConsentStagingIsOptional),
        ("Setup complete staged event reaches fresh reconciliation", CompleteStagedEventReconciles),
        ("Setup preserved completed event without failure survives fresh reconciliation", CompletedEvidenceWithoutFailureReconciles),
        ("Setup preserved Completed rejects malformed original bytes", () => CompletedArchiveCorruptionBlocks("original")),
        ("Setup preserved Completed rejects changed archive hash", () => CompletedArchiveCorruptionBlocks("hash")),
        ("Setup preserved Completed rejects changed archive length", () => CompletedArchiveCorruptionBlocks("length")),
        ("Setup preserved Completed rejects changed operation identity", () => CompletedArchiveCorruptionBlocks("operation")),
        ("Setup preserved Completed rejects changed sequence", () => CompletedArchiveCorruptionBlocks("sequence")),
        ("Setup preserved Completed rejects changed original event name", () => CompletedArchiveCorruptionBlocks("name")),
        ("Setup preserved Completed rejects changed approval PlanId", () => CompletedArchiveCorruptionBlocks("plan")),
        ("Setup preserved Completed rejects changed approval scope", () => CompletedArchiveCorruptionBlocks("scope")),
        ("Setup preserved Completed rejects original path outside recovery", () => CompletedArchiveCorruptionBlocks("path")),
        ("Setup preserved Completed rejects missing reconciliation target", () => CompletedReferenceBlocks("missing")),
        ("Setup preserved Completed rejects self reconciliation target", () => CompletedReferenceBlocks("self")),
        ("Setup preserved Completed rejects incomplete target chain", () => CompletedReferenceBlocks("incomplete")),
        ("Setup preserved Completed rejects failed target operation", () => CompletedReferenceBlocks("failed")),
        ("Setup preserved Completed rejects target without Completed", () => CompletedReferenceBlocks("no-completed")),
        ("Setup preserved Completed rejects reconciliation PlanId mismatch", () => CompletedReferenceBlocks("plan")),
        ("Setup preserved Completed rejects reconciliation scope mismatch", () => CompletedReferenceBlocks("scope")),
        ("Setup preserved Completed cannot hide another orphan Completed", () => CompletedWithMalformedOtherChainBlocks("orphan")),
        ("Setup preserved Completed cannot hide another duplicate sequence", () => CompletedWithMalformedOtherChainBlocks("duplicate")),
        ("Setup preserved Completed cannot hide another unverified change", () => CompletedWithMalformedOtherChainBlocks("unverified")),
        ("Setup preserved Completed restarts before fresh reconciliation", () => CompletedCheckpointResumes("preserved")),
        ("Setup preserved Completed restarts before B Completed publication", () => CompletedCheckpointResumes("before-completed")),
        ("Setup preserved Completed restarts after B Completed before A Reconciled", () => CompletedCheckpointResumes("before-reconciled")),
        ("Setup preserved Completed accepts verified B then optional ConsentFailed", CompletedTargetConsentFailedRemainsHealthy),
        ("Setup preserved Completed with an older Failed still reconciles", CompletedWithOlderFailureReconciles),
        ("Setup truncated related event preserves original and rechecks", TruncatedRelatedEventReconciles),
        ("Setup conflicting temporary record remains unresolved", ConflictingRecordPreservesBoth),
        ("Setup unknown temporary source remains blocked after preservation", UnknownSourceRemainsBlocked),
        ("Setup evidence preservation rejects changed file snapshot", ChangedEvidenceRefusesPreservation),
        ("Setup preservation honors shared recovery lock", PreservationHonorsLock),
        ("Setup interrupted evidence publication resumes on restart", InterruptedPublicationResumes),
        ("Setup manifest before original move resumes without overwrite", InterruptedMoveResumes),
        ("Setup archive directory creation interruption resumes safely", EmptyArchiveDirectoryResumes),
        ("Setup partial archive metadata stays preserved on retry", PartialArchiveMetadataResumes),
        ("Setup complete staged archive metadata stays preserved on retry", CompleteArchiveMetadataResumes),
        ("Setup archive metadata rejects canonical path escape", ArchiveMetadataRejectsPathEscape),
        ("Setup preserved failed revoke cannot revive an earlier automatic grant", PreservedRevokeDoesNotReviveGrant),
        ("Windows evidence rename denies atomic source replacement after hashing", NativeAtomicReplacementIsDenied),
        ("Windows evidence rename abort keeps original and replacement intact", NativeReplacementAbortPreservesBoth),
        ("Windows evidence rename never overwrites a preservation destination", NativeDestinationCollisionPreservesBoth)
    ];

    private static string StageEvent(Harness harness, string id, int sequence, string kind, string planId, string scope)
    {
        WriteEvent(harness.Root, id, sequence, kind, planId, scope);
        var target = Path.Combine(harness.Root, "setup-recovery", "operations", $"{id}.{sequence:D6}.{kind}.json");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.Move(target, temporary);
        return temporary;
    }

    private static void ConsentStagingIsOptional()
    {
        var harness = Harness.Create(conflict: false);
        var directory = Path.Combine(harness.Root, "setup-recovery", "consents");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(path, "{\"Granted\":true,");
        Assert.False(harness.Service.HasUnfinishedRecovery);
        var inspection = harness.Service.InspectInterruptedEvidence();
        Assert.True(inspection.CanPreserve);
        Assert.False(inspection.BlocksExecution);
        Assert.Equal(SetupRecoveryEvidenceKind.ConsentRecord, inspection.Items.Single().Kind);
        Assert.True(harness.Service.ReconcileInterruptedEvidence(inspection).Succeeded);
        Assert.False(harness.Service.HasUnfinishedRecovery);
        Assert.False(harness.Service.Inspect().AllowAutomatic);
        Assert.Equal("{\"Granted\":true,", File.ReadAllText(harness.Service.InspectInterruptedEvidence().Items.Single().PreservationPath!));
    }

    private static void PreservedRevokeDoesNotReviveGrant()
    {
        var harness = Harness.Create(conflict: true);
        Assert.True(harness.Service.Apply(harness.Service.Inspect(), true).Succeeded);
        var consentRoot = Path.Combine(harness.Root, "setup-recovery", "consents");
        var grantPath = Directory.EnumerateFiles(consentRoot, "*.json").Single();
        var grantBytes = File.ReadAllBytes(grantPath);
        var revoked = JsonNode.Parse(grantBytes)!.AsObject();
        var id = Guid.NewGuid().ToString("N");
        revoked["Id"] = id;
        revoked["Sequence"] = 2;
        revoked["Granted"] = false;
        var path = Path.Combine(consentRoot, id + ".json." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(path, revoked.ToJsonString(RecoveryJson.Options));
        harness.Environment.ReintroduceConflict();
        Assert.False(harness.Service.HasUnfinishedRecovery);
        Assert.True(harness.Service.ReconcileInterruptedEvidence(harness.Service.InspectInterruptedEvidence()).Succeeded);
        var requiresApproval = harness.Service.Inspect();
        Assert.Equal(SetupRecoveryState.ApprovalRequired, requiresApproval.State);
        Assert.False(requiresApproval.AllowAutomatic);
        Assert.SequenceEqual(grantBytes, File.ReadAllBytes(grantPath));
        Assert.True(harness.Service.Apply(requiresApproval, true).Succeeded);
        Assert.True(harness.Service.Inspect().AllowAutomatic);
        var latestSequence = Directory.EnumerateFiles(consentRoot, "*.json")
            .Select(file => JsonNode.Parse(File.ReadAllText(file))!["Sequence"]!.GetValue<long>()).Max();
        Assert.Equal(3L, latestSequence);
        Assert.False(harness.Service.HasUnfinishedRecovery);
    }

    private static void NativeAtomicReplacementIsDenied() => NativeReplacementAttempt(abort: false);
    private static void NativeReplacementAbortPreservesBoth() => NativeReplacementAttempt(abort: true);

    private static void NativeReplacementAttempt(bool abort)
    {
        var harness = Harness.Create(conflict: false);
        var recoveryRoot = Path.Combine(harness.Root, "setup-recovery");
        Directory.CreateDirectory(recoveryRoot);
        var original = Path.Combine(recoveryRoot, "unknown.tmp");
        var replacement = Path.Combine(harness.Root, "external-replacement.bin");
        var originalBytes = new byte[] { 0, 3, 7, 255 };
        var replacementBytes = new byte[] { 2, 5, 11, 254 };
        File.WriteAllBytes(original, originalBytes);
        File.WriteAllBytes(replacement, replacementBytes);
        var attempted = false;
        var service = new SetupRecovery(new ProfileStore(harness.Root), harness.Environment, (source, _) =>
        {
            // Actual Windows file sharing/rename behavior, not an environment fake:
            // attempt atomic replacement precisely after hash+manifest publication.
            attempted = true;
            Assert.Throws<IOException>(() => Task.Run(() => File.Replace(replacement, source, null)).GetAwaiter().GetResult());
            Assert.Throws<IOException>(() => File.Delete(source));
            Assert.Throws<IOException>(() => File.Move(source, Path.Combine(harness.Root, "external-stolen.bin")));
            Assert.SequenceEqual(replacementBytes, File.ReadAllBytes(replacement));
            if (abort) throw new IOException("interrupt immediately before same-handle rename");
        });
        var result = service.ReconcileInterruptedEvidence(service.InspectInterruptedEvidence());
        Assert.True(attempted);
        Assert.SequenceEqual(replacementBytes, File.ReadAllBytes(replacement));
        if (abort)
        {
            Assert.False(result.Succeeded);
            Assert.SequenceEqual(originalBytes, File.ReadAllBytes(original));
            Assert.True(harness.Service.ReconcileInterruptedEvidence(harness.Service.InspectInterruptedEvidence()).Succeeded);
        }
        else Assert.True(result.Succeeded, result.Detail);
        var archived = harness.Service.InspectInterruptedEvidence().Items.Single();
        Assert.SequenceEqual(originalBytes, File.ReadAllBytes(archived.PreservationPath!));
        Assert.False(File.Exists(original));
        Assert.False(File.Exists(Path.Combine(harness.Root, "external-stolen.bin")));
    }

    private static void NativeDestinationCollisionPreservesBoth()
    {
        var harness = Harness.Create(conflict: false);
        var recoveryRoot = Path.Combine(harness.Root, "setup-recovery");
        Directory.CreateDirectory(recoveryRoot);
        var original = Path.Combine(recoveryRoot, "unknown.tmp");
        File.WriteAllText(original, "original");
        string? destination = null;
        var service = new SetupRecovery(new ProfileStore(harness.Root), harness.Environment, (_, target) =>
        {
            destination = target;
            File.WriteAllText(target, "different preserved bytes");
        });
        var result = service.ReconcileInterruptedEvidence(service.InspectInterruptedEvidence());
        Assert.False(result.Succeeded);
        Assert.Equal("original", File.ReadAllText(original));
        Assert.Equal("different preserved bytes", File.ReadAllText(destination!));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(destination!)!, "metadata.json")));
    }

    private static void CompleteStagedEventReconciles()
    {
        var harness = Harness.Create(conflict: false);
        var id = Guid.NewGuid().ToString("N");
        var temporary = StageEvent(harness, id, 0, "Started", Guid.NewGuid().ToString("N"), "scope-a");
        var bytes = File.ReadAllBytes(temporary);
        var inspection = harness.Service.InspectInterruptedEvidence();
        Assert.Equal(SetupRecoveryEvidenceKind.CompleteRecord, inspection.Items.Single().Kind);
        Assert.Equal(SetupRecoveryState.Blocked, harness.Service.Inspect().State);
        Assert.True(harness.Service.ReconcileInterruptedEvidence(inspection).Succeeded);
        Assert.False(File.Exists(temporary));
        Assert.SequenceEqual(bytes, File.ReadAllBytes(harness.Service.InspectInterruptedEvidence().Items.Single().PreservationPath!));
        Assert.True(harness.Service.HasUnfinishedRecovery, "Moving evidence cannot complete a mutation.");
        var plan = harness.Service.Inspect();
        Assert.Equal(SetupRecoveryState.Interrupted, plan.State);
        Assert.True(harness.Service.Apply(plan, false).Succeeded);
        Assert.False(harness.Service.HasUnfinishedRecovery);
    }

    private static void CompletedEvidenceWithoutFailureReconciles()
    {
        var harness = Harness.Create(conflict: true);
        var applied = harness.Service.Apply(harness.Service.Inspect(), false);
        Assert.True(applied.Succeeded, applied.Detail);
        var operations = Path.Combine(harness.Root, "setup-recovery", "operations");
        var completed = Directory.EnumerateFiles(operations, applied.OperationId + ".*.Completed.json").Single();
        var temporary = completed + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var originalBytes = File.ReadAllBytes(completed);
        File.Move(completed, temporary);
        Assert.False(Directory.EnumerateFiles(operations, applied.OperationId + ".*.Failed.json").Any());
        var originalJournal = Directory.EnumerateFiles(operations, "*.json").ToDictionary(path => path, File.ReadAllBytes);
        var evidence = harness.Service.InspectInterruptedEvidence();
        Assert.True(evidence.CanPreserve, evidence.Detail);
        Assert.Equal(SetupRecoveryEvidenceKind.CompleteRecord, evidence.Items.Single().Kind);
        Assert.True(harness.Service.ReconcileInterruptedEvidence(evidence).Succeeded);
        Assert.True(harness.Service.HasUnfinishedRecovery, "A preserved Completed cannot prove current completion.");
        var preserved = harness.Service.InspectInterruptedEvidence().Items.Single();
        var metadata = Path.Combine(Path.GetDirectoryName(preserved.PreservationPath!)!, "metadata.json");
        var metadataBytes = File.ReadAllBytes(metadata);
        var plan = harness.Service.Inspect();
        Assert.True(plan.IsReconciliation);
        Assert.Equal(applied.OperationId, plan.InterruptedOperationId);
        var reconciled = harness.Service.Apply(plan, false);
        Assert.True(reconciled.Succeeded, reconciled.Detail);
        Assert.True(reconciled.OperationId != applied.OperationId);
        var reference = JsonNode.Parse(File.ReadAllBytes(
            Path.Combine(operations, applied.OperationId + ".1000000.Reconciled.json")))!;
        Assert.Equal(reconciled.OperationId, reference["RelatedOperationId"]!.GetValue<string>());
        Assert.True(Directory.EnumerateFiles(operations, reconciled.OperationId + ".*.Completed.json").Any());
        Assert.False(Directory.EnumerateFiles(operations, applied.OperationId + ".*.Failed.json").Any());
        var fileCount = Directory.EnumerateFiles(harness.Root, "*", SearchOption.AllDirectories).Count();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var restarted = new SetupRecovery(new ProfileStore(harness.Root), harness.Environment);
            Assert.False(restarted.HasUnfinishedRecovery);
            Assert.True(restarted.Inspect().IsHealthy);
            var current = restarted.InspectInterruptedEvidence();
            Assert.False(current.BlocksExecution, current.Detail);
            Assert.False(current.CanPreserve);
            Assert.False(current.Items.Single().NeedsPublication);
            Assert.SequenceEqual(originalBytes, File.ReadAllBytes(preserved.PreservationPath!));
            Assert.SequenceEqual(metadataBytes, File.ReadAllBytes(metadata));
            foreach (var record in originalJournal) Assert.SequenceEqual(record.Value, File.ReadAllBytes(record.Key));
            Assert.False(File.Exists(completed));
            Assert.Equal(fileCount, Directory.EnumerateFiles(harness.Root, "*", SearchOption.AllDirectories).Count());
        }
    }

    private static void CompletedArchiveCorruptionBlocks(string fault)
    {
        var fixture = CompletedFixture.Create(reconcile: true);
        var metadata = JsonNode.Parse(File.ReadAllBytes(fixture.MetadataPath))!.AsObject();
        var entry = JsonNode.Parse(File.ReadAllBytes(fixture.OriginalPath))!.AsObject();
        switch (fault)
        {
            case "original":
                File.WriteAllText(fixture.OriginalPath, "{\"OperationId\":");
                UpdateArchiveContentMetadata(fixture, metadata);
                break;
            case "hash": metadata["Sha256"] = new string('F', 64); break;
            case "length": metadata["Length"] = metadata["Length"]!.GetValue<long>() + 1; break;
            case "operation": metadata["OperationId"] = Guid.NewGuid().ToString("N"); break;
            case "sequence":
                entry["Sequence"] = entry["Sequence"]!.GetValue<int>() + 1;
                metadata["OriginalPath"] = Path.Combine(fixture.Operations,
                    $"{fixture.OperationA}.{entry["Sequence"]!.GetValue<int>():D6}.Completed.json.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(fixture.OriginalPath, entry.ToJsonString(RecoveryJson.Options));
                UpdateArchiveContentMetadata(fixture, metadata);
                break;
            case "name":
                metadata["OriginalPath"] = metadata["OriginalPath"]!.GetValue<string>().Replace(".Completed.json.", ".Started.json.", StringComparison.Ordinal);
                break;
            case "plan":
            case "scope":
                entry[fault == "plan" ? "PlanId" : "ScopeFingerprint"] = fault == "plan" ? Guid.NewGuid().ToString("N") : "different-approved-scope";
                File.WriteAllText(fixture.OriginalPath, entry.ToJsonString(RecoveryJson.Options));
                UpdateArchiveContentMetadata(fixture, metadata);
                break;
            case "path": metadata["OriginalPath"] = Path.Combine(fixture.Harness.Root, "outside-operations.tmp"); break;
            default: throw new InvalidOperationException(fault);
        }
        // Keep the archive's ID and content hash internally consistent for semantic
        // tampering, so event-name/chain/approval validation must reject the bytes.
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            metadata["OriginalPath"]!.GetValue<string>() + "|" + metadata["Sha256"]!.GetValue<string>()))).ToLowerInvariant()[..32];
        var archive = Path.Combine(fixture.Harness.Root, "recovery-evidence", id);
        if (archive != Path.GetDirectoryName(fixture.MetadataPath))
            Directory.Move(Path.GetDirectoryName(fixture.MetadataPath)!, archive);
        metadata["PreservationPath"] = Path.Combine(archive, "original.bin");
        File.WriteAllText(Path.Combine(archive, "metadata.json"), metadata.ToJsonString(RecoveryJson.Options));
        AssertBlockedWithoutWrites(fixture.Harness);
    }

    private static void UpdateArchiveContentMetadata(CompletedFixture fixture, JsonObject metadata)
    {
        var bytes = File.ReadAllBytes(fixture.OriginalPath);
        metadata["Length"] = bytes.LongLength;
        metadata["Sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void CompletedReferenceBlocks(string fault)
    {
        var fixture = CompletedFixture.Create(reconcile: true);
        var referencePath = Path.Combine(fixture.Operations, fixture.OperationA + ".1000000.Reconciled.json");
        var reference = JsonNode.Parse(File.ReadAllBytes(referencePath))!.AsObject();
        switch (fault)
        {
            case "missing": reference["RelatedOperationId"] = Guid.NewGuid().ToString("N"); break;
            case "self": reference["RelatedOperationId"] = fixture.OperationA; break;
            case "incomplete": SuspendRecord(fixture, EventPath(fixture, fixture.OperationB!, "BackupCreated")); break;
            case "failed":
            case "no-completed":
                SuspendRecord(fixture, EventPath(fixture, fixture.OperationB!, "Completed"));
                if (fault == "failed") AppendFromStarted(fixture, fixture.OperationB!, 999999, "Failed");
                break;
            case "plan": reference["PlanId"] = Guid.NewGuid().ToString("N"); break;
            case "scope": reference["ScopeFingerprint"] = "different-approved-scope"; break;
            default: throw new InvalidOperationException(fault);
        }
        File.WriteAllText(referencePath, reference.ToJsonString(RecoveryJson.Options));
        AssertBlockedWithoutWrites(fixture.Harness);
        fixture.AssertArchiveUnchanged();
    }

    private static void CompletedWithMalformedOtherChainBlocks(string fault)
    {
        var fixture = CompletedFixture.Create(reconcile: true);
        var id = Guid.NewGuid().ToString("N");
        var plan = Guid.NewGuid().ToString("N");
        if (fault != "orphan")
        {
            WriteEvent(fixture.Harness.Root, id, 0, "Started", plan, "scope-a");
            WriteEvent(fixture.Harness.Root, id, 1, "BackupCreated", plan, "scope-a");
        }
        if (fault == "duplicate") WriteEvent(fixture.Harness.Root, id, 1, "ChangeIntent", plan, "scope-a");
        else
        {
            if (fault == "unverified") WriteEvent(fixture.Harness.Root, id, 2, "ChangeIntent", plan, "scope-a");
            WriteEvent(fixture.Harness.Root, id, fault == "unverified" ? 3 : 2, "Completed", plan, "scope-a");
        }
        AssertBlockedWithoutWrites(fixture.Harness);
        fixture.AssertArchiveUnchanged();
    }

    private static void CompletedCheckpointResumes(string checkpoint)
    {
        var fixture = CompletedFixture.Create(reconcile: checkpoint != "preserved");
        if (checkpoint != "preserved")
        {
            SuspendRecord(fixture, Path.Combine(fixture.Operations, fixture.OperationA + ".1000000.Reconciled.json"));
            if (checkpoint == "before-completed") SuspendRecord(fixture, EventPath(fixture, fixture.OperationB!, "Completed"));
        }
        for (var restart = 0; restart < 2; restart++)
        {
            var service = new SetupRecovery(new ProfileStore(fixture.Harness.Root), fixture.Harness.Environment);
            var snapshot = SnapshotFiles(fixture.Harness.Root);
            Assert.True(service.HasUnfinishedRecovery);
            var plan = service.Inspect();
            Assert.Equal(SetupRecoveryState.Interrupted, plan.State);
            Assert.True(plan.IsReconciliation);
            Assert.Equal(fixture.OperationA, plan.InterruptedOperationId);
            Assert.False(plan.AllowAutomatic);
            var evidence = service.InspectInterruptedEvidence();
            Assert.True(evidence.BlocksExecution, "An unfinished operation still requires fresh approval.");
            Assert.False(evidence.Items.Single().BlocksExecution, "The valid archived Completed itself is preserved evidence, not a corrupt record.");
            Assert.False(service.TryAutomatic().Succeeded);
            AssertFilesUnchanged(fixture.Harness.Root, snapshot);
        }
        var restarted = new SetupRecovery(new ProfileStore(fixture.Harness.Root), fixture.Harness.Environment);
        // Both A and an interrupted B need their own freshly inspected approval.
        var continuations = checkpoint == "before-completed" ? 2 : 1;
        for (var index = 0; index < continuations; index++)
        {
            var plan = restarted.Inspect();
            Assert.Equal(index == 0 ? fixture.OperationA : fixture.OperationB, plan.InterruptedOperationId);
            var result = restarted.Apply(plan, false);
            Assert.True(result.Succeeded, result.Detail);
            fixture.AssertArchiveUnchanged();
        }
        Assert.False(restarted.HasUnfinishedRecovery);
        Assert.True(restarted.Inspect().IsHealthy);
        var healthySnapshot = SnapshotFiles(fixture.Harness.Root);
        var finalRestart = new SetupRecovery(new ProfileStore(fixture.Harness.Root), fixture.Harness.Environment);
        Assert.False(finalRestart.HasUnfinishedRecovery);
        Assert.True(finalRestart.Inspect().IsHealthy);
        Assert.False(finalRestart.InspectInterruptedEvidence().CanPreserve);
        AssertFilesUnchanged(fixture.Harness.Root, healthySnapshot);
    }

    private static void CompletedTargetConsentFailedRemainsHealthy()
    {
        var fixture = CompletedFixture.Create(reconcile: true);
        var completed = JsonNode.Parse(File.ReadAllBytes(EventPath(fixture, fixture.OperationB!, "Completed")))!;
        AppendFromStarted(fixture, fixture.OperationB!, completed["Sequence"]!.GetValue<int>() + 1, "ConsentFailed");
        var snapshot = SnapshotFiles(fixture.Harness.Root);
        var restarted = new SetupRecovery(new ProfileStore(fixture.Harness.Root), fixture.Harness.Environment);
        Assert.False(restarted.HasUnfinishedRecovery);
        Assert.True(restarted.Inspect().IsHealthy);
        Assert.False(restarted.InspectInterruptedEvidence().BlocksExecution);
        AssertFilesUnchanged(fixture.Harness.Root, snapshot);
        fixture.AssertArchiveUnchanged();
    }

    private static void CompletedWithOlderFailureReconciles()
    {
        var fixture = CompletedFixture.Create(reconcile: false, failCompletedPublication: true);
        var failure = EventPath(fixture, fixture.OperationA, "Failed");
        var failureBytes = File.ReadAllBytes(failure);
        var plan = fixture.Harness.Service.Inspect();
        Assert.Equal(SetupRecoveryState.Interrupted, plan.State);
        Assert.Equal(fixture.OperationA, plan.InterruptedOperationId);
        var applied = fixture.Harness.Service.Apply(plan, false);
        Assert.True(applied.Succeeded, applied.Detail);
        var restarted = new SetupRecovery(new ProfileStore(fixture.Harness.Root), fixture.Harness.Environment);
        Assert.False(restarted.HasUnfinishedRecovery);
        Assert.True(restarted.Inspect().IsHealthy);
        Assert.SequenceEqual(failureBytes, File.ReadAllBytes(failure));
        fixture.AssertArchiveUnchanged();
    }

    private static string EventPath(CompletedFixture fixture, string operation, string kind) =>
        Directory.EnumerateFiles(fixture.Operations, operation + ".*." + kind + ".json").Single();

    private static void AppendFromStarted(CompletedFixture fixture, string operation, int sequence, string kind)
    {
        var started = JsonNode.Parse(File.ReadAllBytes(EventPath(fixture, operation, "Started")))!.AsObject();
        started["Sequence"] = sequence;
        started["Kind"] = kind;
        started["Detail"] = kind;
        File.WriteAllText(Path.Combine(fixture.Operations, $"{operation}.{sequence:D6}.{kind}.json"), started.ToJsonString(RecoveryJson.Options));
    }

    private static void SuspendRecord(CompletedFixture fixture, string path) =>
        File.Move(path, Path.Combine(fixture.Harness.Root, "checkpoint-" + Path.GetFileName(path) + ".bin"));

    private static void AssertBlockedWithoutWrites(Harness harness)
    {
        var snapshot = SnapshotFiles(harness.Root);
        for (var restart = 0; restart < 2; restart++)
        {
            var service = new SetupRecovery(new ProfileStore(harness.Root), harness.Environment);
            var plan = service.Inspect();
            Assert.Equal(SetupRecoveryState.Blocked, plan.State);
            service.InspectInterruptedEvidence();
            Assert.False(service.Apply(plan, false).Succeeded);
            AssertFilesUnchanged(harness.Root, snapshot);
        }
    }

    private static Dictionary<string, (byte[] Bytes, DateTime Written)> SnapshotFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)));

    private static void AssertFilesUnchanged(string root, Dictionary<string, (byte[] Bytes, DateTime Written)> snapshot)
    {
        Assert.SequenceEqual(snapshot.Keys.Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        foreach (var (path, before) in snapshot)
        {
            Assert.SequenceEqual(before.Bytes, File.ReadAllBytes(path));
            Assert.Equal(before.Written, File.GetLastWriteTimeUtc(path));
        }
    }

    private sealed class CompletedFixture
    {
        private CompletedFixture(Harness harness, string operationA, string originalPath, string metadataPath, string absentCompleted)
        {
            Harness = harness;
            OperationA = operationA;
            OriginalPath = originalPath;
            MetadataPath = metadataPath;
            AbsentCompleted = absentCompleted;
            OriginalBytes = File.ReadAllBytes(originalPath);
            MetadataBytes = File.ReadAllBytes(metadataPath);
            JournalPrefix = Directory.EnumerateFiles(Operations, "*.json").ToDictionary(path => path, File.ReadAllBytes);
        }

        public Harness Harness { get; }
        public string OperationA { get; }
        public string? OperationB { get; private set; }
        public string OriginalPath { get; }
        public string MetadataPath { get; }
        private string AbsentCompleted { get; }
        private byte[] OriginalBytes { get; }
        private byte[] MetadataBytes { get; }
        private Dictionary<string, byte[]> JournalPrefix { get; }
        public string Operations => Path.Combine(Harness.Root, "setup-recovery", "operations");

        public static CompletedFixture Create(bool reconcile, bool failCompletedPublication = false)
        {
            var harness = Harness.Create(conflict: true);
            string? unpublishedCompleted = null;
            var service = failCompletedPublication
                ? new SetupRecovery(new ProfileStore(harness.Root), harness.Environment, beforeEventPublish: (_, target) =>
                {
                    if (!target.EndsWith(".Completed.json", StringComparison.Ordinal)) return;
                    unpublishedCompleted = target;
                    throw new IOException("interrupt A Completed publication after verified changes");
                })
                : harness.Service;
            var resultA = service.Apply(service.Inspect(), false);
            Assert.Equal(!failCompletedPublication, resultA.Succeeded);
            var operations = Path.Combine(harness.Root, "setup-recovery", "operations");
            var completed = failCompletedPublication ? unpublishedCompleted!
                : Directory.EnumerateFiles(operations, resultA.OperationId + ".*.Completed.json").Single();
            if (!failCompletedPublication) File.Move(completed, completed + "." + Guid.NewGuid().ToString("N") + ".tmp");
            var evidence = harness.Service.InspectInterruptedEvidence();
            Assert.Equal(SetupRecoveryEvidenceKind.CompleteRecord, evidence.Items.Single().Kind);
            Assert.True(harness.Service.ReconcileInterruptedEvidence(evidence).Succeeded);
            var original = harness.Service.InspectInterruptedEvidence().Items.Single().PreservationPath!;
            var fixture = new CompletedFixture(harness, resultA.OperationId, original,
                Path.Combine(Path.GetDirectoryName(original)!, "metadata.json"), completed);
            Assert.Equal(failCompletedPublication, Directory.EnumerateFiles(operations, resultA.OperationId + ".*.Failed.json").Any());
            if (reconcile)
            {
                var resultB = harness.Service.Apply(harness.Service.Inspect(), false);
                Assert.True(resultB.Succeeded, resultB.Detail);
                fixture.OperationB = resultB.OperationId;
                Assert.True(resultB.OperationId != resultA.OperationId);
                fixture.AssertArchiveUnchanged();
                Assert.False(Directory.EnumerateFiles(operations, resultA.OperationId + ".*.Failed.json").Any());
            }
            return fixture;
        }

        public void AssertArchiveUnchanged()
        {
            Assert.SequenceEqual(OriginalBytes, File.ReadAllBytes(OriginalPath));
            Assert.SequenceEqual(MetadataBytes, File.ReadAllBytes(MetadataPath));
            Assert.False(File.Exists(AbsentCompleted));
            foreach (var record in JournalPrefix) Assert.SequenceEqual(record.Value, File.ReadAllBytes(record.Key));
        }
    }

    private static void TruncatedRelatedEventReconciles()
    {
        var harness = Harness.Create(conflict: false);
        var id = Guid.NewGuid().ToString("N");
        var plan = Guid.NewGuid().ToString("N");
        WriteEvent(harness.Root, id, 0, "Started", plan, "scope-a");
        WriteEvent(harness.Root, id, 1, "BackupCreated", plan, "scope-a");
        var temporary = StageEvent(harness, id, 2, "ChangeIntent", plan, "scope-a");
        File.WriteAllText(temporary, "{\"OperationId\":");
        var inspection = harness.Service.InspectInterruptedEvidence();
        Assert.Equal(SetupRecoveryEvidenceKind.IncompleteRecord, inspection.Items.Single().Kind);
        Assert.True(harness.Service.ReconcileInterruptedEvidence(inspection).Succeeded);
        Assert.True(harness.Service.HasUnfinishedRecovery);
        Assert.True(harness.Service.Apply(harness.Service.Inspect(), false).Succeeded);
        Assert.False(harness.Service.HasUnfinishedRecovery);
        Assert.False(Directory.EnumerateFiles(Path.Combine(harness.Root, "setup-recovery"), "*.tmp", SearchOption.AllDirectories).Any());
    }

    private static void ConflictingRecordPreservesBoth()
    {
        var harness = Harness.Create(conflict: false);
        var id = Guid.NewGuid().ToString("N");
        var plan = Guid.NewGuid().ToString("N");
        var temporary = StageEvent(harness, id, 0, "Started", plan, "scope-a");
        WriteEvent(harness.Root, id, 0, "Started", plan, "different-scope");
        var inspection = harness.Service.InspectInterruptedEvidence();
        Assert.Equal(SetupRecoveryEvidenceKind.ConflictingRecord, inspection.Items.Single().Kind);
        Assert.True(harness.Service.ReconcileInterruptedEvidence(inspection).Succeeded);
        Assert.True(harness.Service.HasUnfinishedRecovery);
        Assert.True(harness.Service.InspectInterruptedEvidence().Items.Single().BlocksExecution);
    }

    private static void UnknownSourceRemainsBlocked()
    {
        var harness = Harness.Create(conflict: false);
        var directory = Path.Combine(harness.Root, "setup-recovery");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "unrelated.tmp");
        File.WriteAllBytes(path, [0, 1, 2, 255]);
        var inspection = harness.Service.InspectInterruptedEvidence();
        Assert.True(inspection.CanPreserve);
        Assert.True(harness.Service.ReconcileInterruptedEvidence(inspection).Succeeded);
        Assert.True(harness.Service.HasUnfinishedRecovery);
        var preserved = harness.Service.InspectInterruptedEvidence().Items.Single();
        Assert.Equal(SetupRecoveryEvidenceKind.UnknownSource, preserved.Kind);
        Assert.SequenceEqual(new byte[] { 0, 1, 2, 255 }, File.ReadAllBytes(preserved.PreservationPath!));
        Assert.Equal(SetupRecoveryState.Blocked, harness.Service.Inspect().State);
    }

    private static void ChangedEvidenceRefusesPreservation()
    {
        var harness = Harness.Create(conflict: false);
        Directory.CreateDirectory(Path.Combine(harness.Root, "setup-recovery"));
        var path = Path.Combine(harness.Root, "setup-recovery", "unknown.tmp");
        File.WriteAllText(path, "before");
        var inspection = harness.Service.InspectInterruptedEvidence();
        File.WriteAllText(path, "after");
        Assert.False(harness.Service.ReconcileInterruptedEvidence(inspection).Succeeded);
        Assert.Equal("after", File.ReadAllText(path));
        Assert.False(Directory.Exists(Path.Combine(harness.Root, "recovery-evidence")));
    }

    private static void PreservationHonorsLock()
    {
        var harness = Harness.Create(conflict: false);
        var directory = Path.Combine(harness.Root, "setup-recovery");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "unknown.tmp");
        File.WriteAllText(path, "original");
        var inspection = harness.Service.InspectInterruptedEvidence();
        using var lease = new FileStream(Path.Combine(directory, "recovery.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.False(harness.Service.ReconcileInterruptedEvidence(inspection).Succeeded);
        Assert.Equal("original", File.ReadAllText(path));
    }

    private static void InterruptedPublicationResumes()
    {
        var harness = Harness.Create(conflict: false);
        var id = Guid.NewGuid().ToString("N");
        StageEvent(harness, id, 0, "Started", Guid.NewGuid().ToString("N"), "scope-a");
        Assert.True(harness.Service.ReconcileInterruptedEvidence(harness.Service.InspectInterruptedEvidence()).Succeeded);
        File.Delete(Path.Combine(harness.Root, "setup-recovery", "operations", id + ".000000.Started.json"));
        var restarted = new SetupRecovery(new ProfileStore(harness.Root), harness.Environment);
        var inspection = restarted.InspectInterruptedEvidence();
        Assert.True(inspection.CanPreserve);
        Assert.True(inspection.Items.Single().NeedsPublication);
        Assert.True(restarted.ReconcileInterruptedEvidence(inspection).Succeeded);
        Assert.Equal(SetupRecoveryState.Interrupted, restarted.Inspect().State);
    }

    private static void InterruptedMoveResumes()
    {
        var harness = Harness.Create(conflict: false);
        var directory = Path.Combine(harness.Root, "setup-recovery");
        Directory.CreateDirectory(directory);
        var original = Path.Combine(directory, "unknown.tmp");
        File.WriteAllText(original, "immutable");
        Assert.True(harness.Service.ReconcileInterruptedEvidence(harness.Service.InspectInterruptedEvidence()).Succeeded);
        var archived = harness.Service.InspectInterruptedEvidence().Items.Single();
        File.Move(archived.PreservationPath!, original);
        var inspection = harness.Service.InspectInterruptedEvidence();
        Assert.True(inspection.CanPreserve);
        Assert.True(harness.Service.ReconcileInterruptedEvidence(inspection).Succeeded);
        Assert.Equal("immutable", File.ReadAllText(harness.Service.InspectInterruptedEvidence().Items.Single().PreservationPath!));
    }

    private static void EmptyArchiveDirectoryResumes() => ArchiveMetadataStageResumes(null);
    private static void PartialArchiveMetadataResumes() => ArchiveMetadataStageResumes("{\"Schema\":1,");
    private static void CompleteArchiveMetadataResumes() => ArchiveMetadataStageResumes("{\"Schema\":1,\"Unresolved\":false}");

    private static void ArchiveMetadataStageResumes(string? stageBytes)
    {
        var harness = Harness.Create(conflict: false);
        var recoveryRoot = Path.Combine(harness.Root, "setup-recovery");
        Directory.CreateDirectory(recoveryRoot);
        var original = Path.Combine(recoveryRoot, "unknown.tmp");
        File.WriteAllText(original, "original bytes");
        var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)));
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original + "|" + sourceHash))).ToLowerInvariant()[..32];
        var archive = Path.Combine(harness.Root, "recovery-evidence", id);
        Directory.CreateDirectory(archive);
        string? stagingPath = null;
        if (stageBytes is not null)
        {
            stagingPath = Path.Combine(archive, "metadata.json." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(stagingPath, stageBytes);
        }
        var inspection = harness.Service.InspectInterruptedEvidence();
        Assert.True(inspection.CanPreserve, inspection.Detail);
        Assert.True(harness.Service.ReconcileInterruptedEvidence(inspection).Succeeded);
        Assert.Equal("original bytes", File.ReadAllText(Path.Combine(archive, "original.bin")));
        if (stagingPath is not null)
        {
            Assert.Equal(stageBytes, File.ReadAllText(stagingPath));
            var metadata = File.ReadAllText(Path.Combine(archive, "metadata.json"));
            Assert.Contains(Path.GetFileName(stagingPath), metadata);
        }
        Assert.False(File.Exists(original));
        Assert.True(harness.Service.HasUnfinishedRecovery);
        Assert.True(harness.Service.InspectInterruptedEvidence().Items.Single().BlocksExecution);
    }

    private static void ArchiveMetadataRejectsPathEscape()
    {
        var harness = Harness.Create(conflict: false);
        var recoveryRoot = Path.Combine(harness.Root, "setup-recovery");
        Directory.CreateDirectory(recoveryRoot);
        var original = Path.Combine(recoveryRoot, "unknown.tmp");
        File.WriteAllText(original, "original bytes");
        Assert.True(harness.Service.ReconcileInterruptedEvidence(harness.Service.InspectInterruptedEvidence()).Succeeded);
        var item = harness.Service.InspectInterruptedEvidence().Items.Single();
        var metadataPath = Path.Combine(Path.GetDirectoryName(item.PreservationPath!)!, "metadata.json");
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
        metadata["OriginalPath"] = Path.Combine(harness.Root, "..", "escaped.tmp");
        File.WriteAllText(metadataPath, metadata.ToJsonString(RecoveryJson.Options));
        var inspection = harness.Service.InspectInterruptedEvidence();
        Assert.False(inspection.CanPreserve);
        Assert.True(inspection.BlocksExecution);
        Assert.False(harness.Service.ReconcileInterruptedEvidence(inspection).Succeeded);
        Assert.Equal("original bytes", File.ReadAllText(item.PreservationPath!));
    }

    private static void OrphanCompletedBlocks()
    {
        var harness = Harness.Create(conflict: false);
        var operationId = Guid.NewGuid().ToString("N");
        WriteEvent(harness.Root, operationId, 2, "Completed", Guid.NewGuid().ToString("N"), "scope-a");

        Assert.Equal(SetupRecoveryState.Blocked, harness.Service.Inspect().State);
    }

    private static void ChangedJournalMetadataBlocks()
    {
        var harness = Harness.Create(conflict: false);
        var operationId = Guid.NewGuid().ToString("N");
        var planId = Guid.NewGuid().ToString("N");
        WriteEvent(harness.Root, operationId, 0, "Started", planId, "scope-a");
        WriteEvent(harness.Root, operationId, 1, "BackupCreated", planId, "scope-a");
        WriteEvent(harness.Root, operationId, 2, "Completed", planId, "scope-b");

        Assert.Equal(SetupRecoveryState.Blocked, harness.Service.Inspect().State);
    }

    private static void DuplicateJournalSequenceBlocks()
    {
        var harness = Harness.Create(conflict: false);
        var operationId = Guid.NewGuid().ToString("N");
        var planId = Guid.NewGuid().ToString("N");
        WriteEvent(harness.Root, operationId, 0, "Started", planId, "scope-a");
        WriteEvent(harness.Root, operationId, 1, "BackupCreated", planId, "scope-a");
        WriteEvent(harness.Root, operationId, 1, "ChangeIntent", planId, "scope-a");

        Assert.Equal(SetupRecoveryState.Blocked, harness.Service.Inspect().State);
    }

    private static void RevokeWinsAfterFutureDatedGrant()
    {
        var harness = Harness.Create(conflict: true);
        var applied = harness.Service.Apply(harness.Service.Inspect(), allowAutomatic: true);
        Assert.True(applied.Succeeded, applied.Detail);

        var consentRoot = Path.Combine(harness.Root, "setup-recovery", "consents");
        var grantPath = Directory.EnumerateFiles(consentRoot, "*.json", SearchOption.TopDirectoryOnly).Single();
        var grant = JsonNode.Parse(File.ReadAllText(grantPath))!.AsObject();
        grant["RecordedAtUtc"] = "2100-01-01T00:00:00+00:00";
        File.WriteAllText(grantPath, grant.ToJsonString(RecoveryJson.Options), new UTF8Encoding(false));

        harness.Environment.ReintroduceConflict();
        harness.Service.RevokeAutomatic();

        var sequences = Directory.EnumerateFiles(consentRoot, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => JsonNode.Parse(File.ReadAllText(path))!["Sequence"]!.GetValue<long>())
            .OrderBy(sequence => sequence)
            .ToArray();
        Assert.SequenceEqual(new long[] { 1, 2 }, sequences);
        var plan = harness.Service.Inspect();
        Assert.Equal(SetupRecoveryState.ApprovalRequired, plan.State);
        Assert.False(plan.AllowAutomatic);
    }

    private static void WriteEvent(string root, string operationId, int sequence, string kind, string planId, string scope)
    {
        var operationsRoot = Path.Combine(root, "setup-recovery", "operations");
        Directory.CreateDirectory(operationsRoot);
        var record = new
        {
            OperationId = operationId,
            Sequence = sequence,
            Kind = kind,
            PlanId = planId,
            ScopeFingerprint = scope,
            Detail = kind,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            RelatedOperationId = (string?)null
        };
        File.WriteAllText(Path.Combine(operationsRoot, $"{operationId}.{sequence:D6}.{kind}.json"),
            JsonSerializer.Serialize(record, RecoveryJson.Options), new UTF8Encoding(false));
    }

    private sealed class Harness
    {
        private Harness(string root, ProfileStore store, Environment environment)
        {
            Root = root;
            Environment = environment;
            Service = new SetupRecovery(store, environment);
        }

        public string Root { get; }
        public Environment Environment { get; }
        public SetupRecovery Service { get; }

        public static Harness Create(bool conflict)
        {
            var root = TestContext.CreateRunDirectory("setup-recovery-journal");
            var store = new ProfileStore(root);
            var profile = store.Create();
            return new Harness(root, store, new Environment(profile, Path.Combine(root, "Sandboxie.ini"), conflict));
        }
    }

    private sealed class Environment : ISetupRecoveryEnvironment
    {
        private const string UserSection = "UserSettings_TEST";
        private readonly BoxProfile profile;
        private readonly string configPath;
        private readonly Dictionary<string, string[]> box;
        private Dictionary<string, string[]> globals = new(StringComparer.OrdinalIgnoreCase);
        private byte[] iniBytes = Encoding.UTF8.GetBytes("clean");

        public Environment(BoxProfile profile, string configPath, bool conflict)
        {
            this.profile = profile;
            this.configPath = configPath;
            box = IsolationPolicy.Expected(profile);
            if (conflict) ReintroduceConflict();
        }

        public SetupRecoveryObservation Inspect(ProfileStore store, BoxProfile? inspectedProfile,
            CancellationToken cancellationToken = default)
        {
            var explicitBox = Copy(box);
            var explicitGlobals = Copy(globals);
            var configuration = new ConfigurationLayers(explicitBox, Copy(box), explicitGlobals,
                Copy(RecoveryTemplateRules.ExpectedGlobals(globals)));
            var sections = new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase)
            {
                [profile.BoxName] = Copy(box),
                ["GlobalSettings"] = Copy(globals),
                [UserSection] = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            };
            IReadOnlyList<SetupRecoveryChange> changes = globals.TryGetValue("Template", out var templates)
                ? [new SetupRecoveryChange("GlobalSettings", "Template", templates.ToArray(), Array.Empty<string>(),
                    SetupRecoveryChangeKind.RemoveGlobalTemplate, "호환성 템플릿 복구")]
                : Array.Empty<SetupRecoveryChange>();
            return new SetupRecoveryObservation(
                configPath, iniBytes.ToArray(), configuration, sections, changes, profile.BoxName, profile.UserSid,
                profile.StoragePath, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<int>(), true,
                new SetupRecoveryInstallContext(Path.Combine(TestContext.WorkspaceRoot, "Sandboxie.exe"),
                    Path.Combine(TestContext.WorkspaceRoot, "Start.exe"), "1.18.4", true),
                SetupRecoveryUiState.Empty, true, true, true, string.Empty, DateTimeOffset.UtcNow)
            {
                CurrentUserSettingsSection = UserSection
            };
        }

        public SetupRecoveryUiResult QuiesceManagementUi(SetupRecoveryObservation observation, SetupRecoveryPlan plan,
            CancellationToken cancellationToken = default) => SetupRecoveryUiResult.NoUi;

        public SetupRecoveryChangeResult ApplyChange(SetupRecoveryObservation observation, SetupRecoveryPlan plan,
            SetupRecoveryChange change, CancellationToken cancellationToken = default)
        {
            globals.Remove(change.Key);
            iniBytes = Encoding.UTF8.GetBytes("changed");
            return new SetupRecoveryChangeResult(true, true);
        }

        public void ReintroduceConflict()
        {
            globals["Template"] = ["WindowsRasMan"];
            iniBytes = Encoding.UTF8.GetBytes("conflict");
        }

        private static Dictionary<string, string[]> Copy(IReadOnlyDictionary<string, string[]> source) =>
            source.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }
}
