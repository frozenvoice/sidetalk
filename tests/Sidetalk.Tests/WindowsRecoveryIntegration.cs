using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Sidetalk.Core;

namespace Sidetalk.Tests;

/// <summary>
/// Opt-in destructive-timing tests against the actual reviewed Sandboxie installation
/// in the authorized disposable Windows VM. Never registered in the synthetic suite.
/// </summary>
internal static class WindowsRecoveryIntegration
{
    private const string LabRoot = @"C:\SIDETALK-Lab";
    private const string MarkerText = "SIDETALK-LAB approved Sandboxie Plus 1.18.4 recovery test";
    private const string SentinelText = "SIDETALK native recovery preservation sentinel v1";
    private const string RegistrySentinel = @"Software\SIDETALK-LabRecovery";
    private static readonly string[] SixTemplates =
        ["WindowsRasMan", "OnScreenKeyboard", "WindowsLive", "Edge_Fix", "OfficeLicensing", "OfficeClickToRun"];

    // Full arguments: --windows-recovery-integration <phase> <new-report.json> [<immutable-baseline.json>]
    public static int Run(string[] args)
    {
        if (args.Length is not (3 or 4) || args[0] != "--windows-recovery-integration")
            throw new ArgumentException("Usage: --windows-recovery-integration <phase> C:\\SIDETALK-Lab\\reports\\new-report.json [<immutable-baseline.json>]");
        RequireLab();
        var reportPath = RequireReportPath(args[2]);
        var phase = args[1];
        var baselinePath = RequireBaselinePath(args.Length == 4 ? args[3] : "preservation-baseline.json");
        Require(!baselinePath.Equals(reportPath, StringComparison.OrdinalIgnoreCase), "Baseline and report paths must differ.");
        var report = new Dictionary<string, object?>
        {
            ["Phase"] = phase, ["WindowsBuild"] = Environment.OSVersion.Version.ToString(),
            ["Boundary"] = "Real Windows/Sandboxie file, RPC and process APIs",
            ["AccountLogin"] = "Not performed", ["Messaging"] = "Not performed",
            ["AccountDataAccess"] = "Not performed", ["InjectedTiming"] = "None",
            ["StartedUtc"] = DateTimeOffset.UtcNow, ["BaselinePath"] = baselinePath
        };
        try
        {
            Action<string, BoxProfile>? beforePublish = phase == "crash-profile" ? (temporary, candidate) =>
            {
                WriteNew(Path.Combine(LabRoot, "approved-candidate.json"), JsonSerializer.SerializeToUtf8Bytes(candidate));
                report["InjectedTiming"] = "Abrupt child exit after real profile temporary publication, before no-overwrite profile move";
                report["TemporaryProfileSha256"] = Hash(File.ReadAllBytes(temporary));
                report["ExpectedAbruptExit"] = 77;
                report["Completed"] = false;
                WriteReport(reportPath, report);
                Environment.Exit(77);
            } : null;
            var store = new ProfileStore(beforePublish: beforePublish);
            HostProfileContext.RequireUnredirected(store);
            var native = new WindowsSetupRecoveryEnvironment();
            ISetupRecoveryEnvironment environment = phase is "crash-change" or "crash-record"
                ? new CrashAfterRealChange(native, (plan, change) =>
                {
                    VerifyPreservationBaseline(native, store, baselinePath);
                    if (phase == "crash-record")
                    {
                        var staged = StageTruncatedVerifiedEvent(store, plan, change);
                        report["InjectedTiming"] = "Real Windows/SbieIni verified change followed by injected truncated ChangeVerified event bytes, then abrupt child exit";
                        report["CoverageLimit"] = "Partial event-file bytes and interruption timing are injected; setting mutation, file/API verification and later preservation/reconciliation use actual production boundaries.";
                        report["InterruptedOperationId"] = staged.OperationId;
                        report["ChangeIntentSequence"] = staged.IntentSequence;
                        report["NextEventSequence"] = staged.IntentSequence + 1;
                        report["StagedEventPath"] = staged.Path;
                        report["StagedEventSha256"] = staged.Sha256;
                        report["StagedEventLength"] = staged.Length;
                        report["StagedJsonIsComplete"] = false;
                    }
                    else report["InjectedTiming"] = "Abrupt child exit after real verified SbieIni change, before result event publication";
                    report["ExpectedAbruptExit"] = 77;
                    report["Completed"] = false;
                    report["Succeeded"] = false;
                    WriteReport(reportPath, report);
                    Environment.Exit(77);
                }) : native;
            SetupRecoveryEvidenceInspection? preservationApproval = null;
            BoxProfile? preservationProfile = null;
            var recovery = new SetupRecovery(store, environment,
                beforePreservationRename: phase == "crash-preserve" ? (original, preserved) =>
                {
                    Require(preservationApproval is not null && preservationProfile is not null && store.Load() == preservationProfile,
                        "The approved native preservation profile changed before rename.");
                    var item = preservationApproval!.Items.Single(candidate => candidate.OriginalPath.Equals(original, StringComparison.OrdinalIgnoreCase));
                    Require(item.PreservationPath is null && item.OperationId is not null && item.BlocksExecution,
                        "The rename boundary does not belong to the approved unfinished original evidence.");
                    var metadataPath = Path.Combine(Path.GetDirectoryName(preserved)!, "metadata.json");
                    ProfileStore.RejectReparseAncestors(metadataPath);
                    var metadataBytes = File.ReadAllBytes(metadataPath);
                    using var metadata = JsonDocument.Parse(metadataBytes);
                    Require(metadata.RootElement.GetProperty("OriginalPath").GetString() == item.OriginalPath &&
                        metadata.RootElement.GetProperty("PreservationPath").GetString() == preserved &&
                        metadata.RootElement.GetProperty("Sha256").GetString() == item.Sha256 &&
                        metadata.RootElement.GetProperty("Length").GetInt64() == item.Length &&
                        metadata.RootElement.GetProperty("OperationId").GetString() == item.OperationId,
                        "The published preservation manifest does not describe the approved original bytes and operation.");
                    Require(File.Exists(original) && !File.Exists(preserved), "The original was already moved before the injected rename interruption.");
                    // Production holds the verified original by its exclusive handle.
                    // Reopening that file here would introduce an artificial sharing failure.
                    VerifyPreservationBaseline(native, store, baselinePath);
                    report["InjectedTiming"] = "Abrupt child exit after immutable preservation metadata publication, immediately before original same-handle rename";
                    report["CoverageLimit"] = "The interruption hook is injected; actual file ownership, exclusive handle, metadata publication, native settings and restart recovery remain production operations.";
                    report["InterruptedOperationId"] = item.OperationId;
                    report["OriginalPath"] = original;
                    report["OriginalSha256"] = item.Sha256;
                    report["PreservationPath"] = preserved;
                    report["PreservationMetadataSha256"] = Hash(metadataBytes);
                    report["PreservationMetadataPublished"] = true;
                    report["OriginalRenamePerformed"] = false;
                    report["ExpectedAbruptExit"] = 77;
                    report["Completed"] = false;
                    report["Succeeded"] = false;
                    WriteReport(reportPath, report);
                    Environment.Exit(77);
                } : null,
                beforeEventPublish: phase == "crash-completed" ? (temporary, target) =>
                {
                    if (!target.EndsWith(".Completed.json", StringComparison.Ordinal)) return;
                    VerifyPreservationBaseline(native, store, baselinePath);
                    var staged = RecordCompletedInterruption(store, temporary, target, baselinePath);
                    report["InjectedTiming"] = "Abrupt child exit after production Completed JSON serialization and Flush(true), immediately before no-overwrite event publication";
                    report["CoverageLimit"] = "The exit timing is injected; Completed bytes, the durable prior journal, native SbieIni mutation, file/API verification and restart preservation/reconciliation use production boundaries. No power-loss behavior is claimed.";
                    report["InterruptedOperationId"] = staged.OperationId;
                    report["StagedEventPath"] = staged.TemporaryPath;
                    report["StagedEventSha256"] = staged.CompletedSha256;
                    report["StagedEventLength"] = staged.CompletedBytes.LongLength;
                    report["StagedJsonIsComplete"] = true;
                    report["FinalCompletedPublished"] = false;
                    report["FailedEventCount"] = 0;
                    report["ExpectedAbruptExit"] = 77;
                    report["Completed"] = false;
                    report["Succeeded"] = false;
                    WriteReport(reportPath, report);
                    Environment.Exit(77);
                } : null);
            var journal = new CommandJournal(store.Root);
            if (phase == "command-reconcile")
            {
                ReconcilePreviousBootCommand(native, store, journal, report, baselinePath);
            }
            else if (phase == "checkpoint-external-settings")
            {
                Require(args.Length == 4, "External settings checkpoint requires a new immutable baseline name or path as the fourth argument.");
                using var readLease = journal.TryAcquireInspectionLease(recoverMissingLock: false);
                CheckpointExternalSettings(native, store, baselinePath, report);
            }
            else if (phase == "inspect-completed")
            {
                InspectCompletedReconciliation(native, store, recovery, journal, report, baselinePath);
            }
            else if (phase is "inspect" or "inspect-foreign")
            {
                using var readLease = journal.TryAcquireInspectionLease(recoverMissingLock: false);
                var observation = native.Inspect(store, store.Load()).Normalize();
                var plan = recovery.Inspect();
                report["RecoveryState"] = plan.State.ToString();
                report["RecoveryBlockers"] = plan.Blockers;
                report["ForeignManagementCount"] = observation.Ui.ForeignManagementProcesses.Count;
                report["UnqueryableManagementCount"] = observation.Ui.UnqueryableManagementProcessIds.Count;
                if (phase == "inspect-foreign")
                {
                    Require(observation.Ui.ForeignManagementProcesses.Count != 0, "No verified foreign session management UI was observed.");
                    Require(!plan.CanApply, "Foreign management UI did not block shared INI repair.");
                    report["OtherUserProcessesTerminated"] = 0;
                }
            }
            else
            {
                using var lease = journal.AcquireLease();
                var profile = store.Load();
                if (profile is not null)
                {
                    var observed = native.Inspect(store, profile).Normalize();
                    Require(journal.ReadPending(profile, observed.Install.StartExePath) is null, "An unfinished launch/stop command prevents lab mutation.");
                    Require(observed.BoxProcessIds.Count == 0, "The owned box must be empty for lab recovery.");
                }
                switch (phase)
                {
                    case "first-setup":
                    case "crash-profile":
                        Require(profile is null, "Initial setup requires a fresh authorized test profile.");
                        Seed(native, store, SixTemplates);
                        var first = recovery.Inspect();
                        Require(first.IsFirstSetup && first.CanApply, "First setup was blocked: " + string.Join(" | ", first.Blockers));
                        ApplyAndVerify(recovery.Apply(first, true), native, store, report);
                        EnsurePreservationBaseline(native, store, baselinePath);
                        break;
                    case "seed-six":
                    case "seed-subset":
                        Require(profile is not null, "A prepared test profile is required.");
                        VerifyPreservationBaseline(native, store, baselinePath);
                        Seed(native, store, phase == "seed-six" ? SixTemplates : [SixTemplates[0], SixTemplates[3], SixTemplates[5]]);
                        var seeded = recovery.Inspect();
                        Require(seeded.State == SetupRecoveryState.AutomaticallyApplicable && seeded.AllowAutomatic,
                            "Previously granted scope did not cover recurrence: " + string.Join(" | ", seeded.Blockers));
                        report["RecoveryState"] = seeded.State.ToString();
                        report["SeededTemplates"] = phase == "seed-six" ? SixTemplates : [SixTemplates[0], SixTemplates[3], SixTemplates[5]];
                        VerifyPreservationBaseline(native, store, baselinePath);
                        break;
                    case "repair-auto":
                        VerifyPreservationBaseline(native, store, baselinePath);
                        var backupCount = CountBackups(store);
                        var alreadyHealthy = recovery.Inspect().IsHealthy;
                        ApplyAndVerify(recovery.TryAutomatic(), native, store, report);
                        if (alreadyHealthy) Require(CountBackups(store) == backupCount, "Healthy automatic inspection created an unnecessary backup.");
                        VerifyPreservationBaseline(native, store, baselinePath);
                        report["HealthyNoOp"] = alreadyHealthy;
                        break;
                    case "optional-setting":
                        VerifyPreservationBaseline(native, store, baselinePath);
                        Require(recovery.Inspect().IsHealthy, "Optional-setting observation requires initially healthy policy and preparation.");
                        Seed(native, store, []);
                        var optional = native.Inspect(store, profile).Normalize();
                        Require(profile is not null && IsolationPolicy.Validate(profile, optional.Configuration).Safe,
                            "The optional recurrence setting unexpectedly changed current isolation policy.");
                        Require(recovery.Inspect().Changes.All(change => change.Kind == SetupRecoveryChangeKind.SetUserSetting),
                            "The optional setting phase contains an actual policy repair.");
                        // The production controller acquires this same cross-process
                        // inspection lock. End the fixture mutation before observing it.
                        lease.Dispose();
                        using (var controller = new SidetalkController(readOnly: true))
                        {
                            var snapshot = controller.InspectAsync().GetAwaiter().GetResult();
                            report["ActualExecutionState"] = snapshot.State.ToString();
                            report["ActualCanLaunch"] = snapshot.CanLaunch;
                            report["ExecutionInterpretation"] = snapshot.Kakao.Available
                                ? "Actual signed installation and current policy were inspected; no app launch or account access performed."
                                : "Official Kakao installation unavailable: native launch permission is untested; current Sandboxie policy remains verified.";
                            if (snapshot.Kakao.Available) Require(snapshot.CanLaunch, "An optional setting alone blocked otherwise verified current launch readiness: " + snapshot.Detail);
                        }
                        using (journal.AcquireLease()) VerifyPreservationBaseline(native, store, baselinePath);
                        report["OptionalSettingOnly"] = true;
                        break;
                    case "seed-optional":
                        VerifyPreservationBaseline(native, store, baselinePath);
                        Require(profile is not null && !recovery.HasUnfinishedRecovery,
                            "Optional-only seeding requires the same prepared profile with no unfinished mutation.");
                        Seed(native, store, []);
                        var seededOptional = native.Inspect(store, profile).Normalize();
                        Require(IsolationPolicy.Validate(profile!, seededOptional.Configuration).Safe &&
                            Read(seededOptional, seededOptional.CurrentUserSettingsSection!, "SbieCtrl_AutoRunSoftCompat").SequenceEqual(["y"]),
                            "Optional-only seeding did not preserve policy and the actual current-user target.");
                        SandboxieIniSnapshot.RequireEquivalent(seededOptional.IniBytes, seededOptional.ExplicitSections);
                        VerifyPreservationBaseline(native, store, baselinePath);
                        report["OptionalSettingOnly"] = true;
                        report["SeededTemplates"] = Array.Empty<string>();
                        report["CurrentUserSettingsSection"] = seededOptional.CurrentUserSettingsSection;
                        report["RecurrenceOption"] = "y";
                        report["OtherSettingsPreserved"] = true;
                        report["FileApiEquivalent"] = true;
                        report["RecoveryApplied"] = false;
                        report["ResultKind"] = "Lab fixture optional-setting seed";
                        break;
                    case "crash-change":
                    case "crash-record":
                        VerifyPreservationBaseline(native, store, baselinePath);
                        var interrupted = recovery.Inspect();
                        Require(profile is not null && !recovery.HasUnfinishedRecovery && interrupted.CanApply && interrupted.Changes.Count != 0,
                            "Seed recurrence in the same prepared profile with no existing unfinished recovery before the abrupt change phase.");
                        recovery.Apply(interrupted, true);
                        throw new InvalidOperationException("The abrupt change timing boundary was not reached.");
                    case "crash-completed":
                        VerifyPreservationBaseline(native, store, baselinePath);
                        var completedPlan = recovery.Inspect();
                        Require(profile is not null && !recovery.HasUnfinishedRecovery && completedPlan.CanApply &&
                            completedPlan.Changes.Count == 1 && completedPlan.Changes.All(change => change.Kind == SetupRecoveryChangeKind.SetUserSetting),
                            "Run seed-optional in the same prepared profile before the Completed-only interruption; no global-template repair is permitted.");
                        recovery.Apply(completedPlan, true);
                        throw new InvalidOperationException("The flushed Completed-before-publication interruption boundary was not reached.");
                    case "reconcile-completed":
                        lease.Dispose();
                        ReconcileCompletedInterruption(native, store, recovery, journal, report, baselinePath);
                        break;
                    case "crash-preserve":
                        VerifyPreservationBaseline(native, store, baselinePath);
                        Require(profile is not null && recovery.HasUnfinishedRecovery, "An actual unfinished recovery in the same prepared profile is required.");
                        preservationProfile = profile;
                        preservationApproval = recovery.InspectInterruptedEvidence();
                        Require(preservationApproval.CanPreserve && preservationApproval.BlocksExecution &&
                            preservationApproval.Items.Any(item => item.PreservationPath is null && item.OperationId is not null &&
                                item.Kind == SetupRecoveryEvidenceKind.IncompleteRecord && item.BlocksExecution),
                            "The native partial event original is not available for approved preservation.");
                        recovery.ReconcileInterruptedEvidence(preservationApproval);
                        throw new InvalidOperationException("The metadata-before-rename interruption boundary was not reached.");
                    case "command-intent":
                        VerifyPreservationBaseline(native, store, baselinePath);
                        Require(profile is not null && recovery.Inspect().IsHealthy && !recovery.HasUnfinishedRecovery,
                            "Previous-boot command test requires the same prepared, healthy, empty owned box.");
                        var commandObservation = native.Inspect(store, profile).Normalize();
                        RuntimeVerifier.RequireComponent(commandObservation.Install.StartExePath);
                        var intent = journal.Prepare(profile!, SidetalkCommandAction.Launch, commandObservation.Install.StartExePath);
                        Require(intent.Process is null && intent.RecoveryContext?.Boot is not null &&
                            WindowsCommandEvidence.AuthenticatesBootEvidence(intent.OperationId, intent.RecoveryContext),
                            "The real PID-less intent lacks authenticated native monotonic boot evidence.");
                        Require(journal.ReadPending(profile!, commandObservation.Install.StartExePath) == intent,
                            "The real durable command intent differs from the prepared record.");
                        VerifyPreservationBaseline(native, store, baselinePath);
                        report["InterruptedOperationId"] = intent.OperationId;
                        report["CommandSchema"] = intent.Schema;
                        report["RecordedUptimeMilliseconds"] = intent.RecoveryContext!.Boot!.UptimeMilliseconds;
                        report["BootWitnessAuthenticated"] = true;
                        report["CommandProcessCreated"] = false;
                        report["PidRecorded"] = false;
                        report["ExitCode"] = null;
                        report["InjectedTiming"] = "Abrupt child exit after actual durable PID-less command intent publication; no Start process is created";
                        report["CoverageLimit"] = "Exercises actual command journal and native DPAPI/uptime boot evidence; it does not execute Start.exe, launch Kakao or test an account.";
                        report["ExpectedAbruptExit"] = 77;
                        report["Completed"] = false;
                        report["Succeeded"] = false;
                        WriteReport(reportPath, report);
                        Environment.Exit(77);
                        throw new InvalidOperationException("Expected abrupt command-intent exit did not occur.");
                    case "reconcile":
                        var evidence = recovery.InspectInterruptedEvidence();
                        report["PreservedOriginalCount"] = evidence.Items.Count;
                        if (evidence.CanPreserve)
                            Require(recovery.ReconcileInterruptedEvidence(evidence).Succeeded, "Original evidence preservation failed.");
                        var reconciliation = recovery.Inspect();
                        Require(reconciliation.State == SetupRecoveryState.Interrupted && reconciliation.IsReconciliation,
                            "The interrupted mutation was not exposed for explicit reconciliation: " + string.Join(" | ", reconciliation.Blockers));
                        ApplyAndVerify(recovery.Apply(reconciliation, true), native, store, report);
                        if (File.Exists(Path.Combine(LabRoot, "approved-candidate.json")))
                        {
                            var candidate = JsonSerializer.Deserialize<BoxProfile>(File.ReadAllBytes(Path.Combine(LabRoot, "approved-candidate.json")));
                            Require(candidate == store.Load(), "Restart replaced the originally approved profile candidate.");
                            report["SameApprovedCandidate"] = true;
                        }
                        EnsurePreservationBaseline(native, store, baselinePath);
                        VerifyPreservationBaseline(native, store, baselinePath);
                        break;
                    case "management-ui":
                        VerifyPreservationBaseline(native, store, baselinePath);
                        Seed(native, store, [SixTemplates[0]]);
                        var beforeManager = native.Inspect(store, profile).Normalize();
                        Require(beforeManager.Ui.ApprovedManagementProcesses.Count == 0 && beforeManager.Ui.Fresh,
                            "Management UI phase requires no preexisting management process.");
                        var managerPath = Path.Combine(beforeManager.Install.SandboxiePath, "SandMan.exe");
                        // SandMan carries the Plus version, whereas RequireComponent
                        // checks the core component version used by SbieIni/Start.
                        var managerDependency = DependencyDetector.Sandboxie();
                        Require(managerDependency.Available && managerDependency.Path is not null &&
                            Path.GetFullPath(managerDependency.Path).Equals(managerPath, StringComparison.OrdinalIgnoreCase) &&
                            RuntimeVerifier.MatchesVersion(managerDependency.Version, RuntimeVerifier.PlusVersion),
                            "The lab management UI must be the verified official Plus installation.");
                        using (var launched = Process.Start(new ProcessStartInfo(managerPath) { UseShellExecute = false })
                            ?? throw new InvalidOperationException("Cannot launch approved lab SandMan."))
                        {
                            SetupRecoveryObservation current = beforeManager;
                            for (var attempt = 0; attempt < 40 && current.Ui.ApprovedManagementProcesses.Count == 0; attempt++)
                            {
                                Thread.Sleep(250);
                                current = native.Inspect(store, profile).Normalize();
                            }
                            Require(current.Ui.ApprovedManagementProcesses.Count != 0 && current.Ui.Fresh, "A verified current-user management UI was not observed.");
                            report["ApprovedManagementPids"] = current.Ui.ApprovedManagementProcesses.Select(item => item.ProcessId).ToArray();
                            var approved = recovery.Inspect();
                            Require(approved.CanApply, "Approved manager recovery was blocked: " + string.Join(" | ", approved.Blockers));
                            ApplyAndVerify(recovery.Apply(approved, true), native, store, report);
                            Require(native.Inspect(store, store.Load()).Ui.ApprovedManagementProcesses.Count == 0, "Approved management UI remained active after repair.");
                        }
                        VerifyPreservationBaseline(native, store, baselinePath);
                        break;
                    default:
                        throw new ArgumentException("Unknown integration phase: " + phase);
                }
            }
            report["Completed"] = true;
            report["Succeeded"] = true;
            WriteReport(reportPath, report);
            return 0;
        }
        catch (Exception error)
        {
            report["Completed"] = false;
            report["Succeeded"] = false;
            report["Error"] = error.ToString();
            WriteReport(reportPath, report);
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void RequireLab()
    {
        Require(OperatingSystem.IsWindows() && Environment.MachineName.Equals("SIDETALK-LAB", StringComparison.OrdinalIgnoreCase),
            "Native mutation tests are allowed only on the explicitly named SIDETALK-LAB VM.");
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        Require((bios?.GetValue("SystemProductName") as string)?.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) == true,
            "The authorized VirtualBox hardware identity is required.");
        ProfileStore.RejectReparseAncestors(LabRoot);
        var marker = Path.Combine(LabRoot, "authorized-recovery-test.marker");
        ProfileStore.RejectReparseAncestors(marker);
        Require(File.Exists(marker) && File.ReadAllText(marker).Trim() == MarkerText, "The explicit VM lab authorization marker is missing.");
    }

    private static StagedPartialEvent StageTruncatedVerifiedEvent(ProfileStore store, SetupRecoveryPlan plan, SetupRecoveryChange change)
    {
        var operations = Path.Combine(store.Root, "setup-recovery", "operations");
        ProfileStore.RejectReparseAncestors(operations);
        var matches = Directory.EnumerateFiles(operations, "*.ChangeIntent.json", SearchOption.TopDirectoryOnly)
            .Select(path => ReadNativeEvent(path))
            .Where(entry => entry.PlanId == plan.PlanId && entry.ScopeFingerprint == plan.ScopeFingerprint &&
                entry.Kind == "ChangeIntent" && entry.Detail == change.Identity).ToArray();
        Require(matches.Length == 1, "The actual current plan and verified change do not identify exactly one durable ChangeIntent.");
        var intent = matches[0];
        Require(Guid.TryParseExact(intent.OperationId, "N", out _) && intent.Sequence >= 2,
            "The current intent operation identity or sequence is invalid.");
        var sourceEvents = Directory.EnumerateFiles(operations, intent.OperationId + ".*.json", SearchOption.TopDirectoryOnly)
            .Select(path => ReadNativeEvent(path)).OrderBy(entry => entry.Sequence).ToArray();
        Require(sourceEvents.Length == intent.Sequence + 1 && sourceEvents.Select(entry => entry.Sequence).SequenceEqual(Enumerable.Range(0, intent.Sequence + 1)) &&
            sourceEvents.All(entry => entry.OperationId == intent.OperationId && entry.PlanId == plan.PlanId && entry.ScopeFingerprint == plan.ScopeFingerprint) &&
            sourceEvents[0].Kind == "Started" && sourceEvents[^1] == intent,
            "The verified change is not linked to the last contiguous event of the actual unfinished operation.");
        var nextSequence = checked(intent.Sequence + 1);
        var target = Path.Combine(operations, $"{intent.OperationId}.{nextSequence:D6}.ChangeVerified.json");
        ProfileStore.RejectReparseAncestors(target);
        Require(!File.Exists(target) && !Directory.EnumerateFiles(operations, Path.GetFileName(target) + ".*.tmp").Any(),
            "The next event already has a published or staged original; it must not be replaced.");
        byte[] partial;
        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("OperationId", intent.OperationId);
                writer.WriteNumber("Sequence", nextSequence);
                writer.WriteString("Kind", "ChangeVerified");
                writer.WriteString("PlanId", intent.PlanId);
                writer.WriteString("ScopeFingerprint", intent.ScopeFingerprint);
                writer.WriteString("Detail", intent.Detail);
                // Stop while the next value is being written. No complete fabricated
                // recovery event or completion declaration exists even in memory.
                writer.WritePropertyName("RecordedAtUtc");
                writer.Flush();
            }
            partial = buffer.ToArray();
        }
        var incomplete = false;
        try { using var parsed = JsonDocument.Parse(partial); }
        catch (JsonException) { incomplete = true; }
        Require(incomplete, "The injected event must contain actually truncated JSON bytes.");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        WriteNew(temporary, partial);
        Require(Hash(File.ReadAllBytes(temporary)) == Hash(partial), "The flushed partial event bytes differ from the injected prefix.");
        return new(intent.OperationId, intent.Sequence, temporary, Hash(partial), partial.LongLength);
    }

    private static NativeEvent ReadNativeEvent(string path, string? finalTarget = null)
    {
        ProfileStore.RejectReparseAncestors(path);
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var value = document.RootElement;
        var entry = new NativeEvent(
            value.GetProperty("OperationId").GetString() ?? throw new InvalidDataException("Native event has no operation identity."),
            value.GetProperty("Sequence").GetInt32(),
            value.GetProperty("Kind").GetString() ?? throw new InvalidDataException("Native event has no phase."),
            value.GetProperty("PlanId").GetString() ?? throw new InvalidDataException("Native event has no approved plan."),
            value.GetProperty("ScopeFingerprint").GetString() ?? throw new InvalidDataException("Native event has no approved scope."),
            value.GetProperty("Detail").GetString() ?? throw new InvalidDataException("Native event has no change identity."),
            value.TryGetProperty("RelatedOperationId", out var related) ? related.GetString() : null);
        Require(Path.GetFileName(finalTarget ?? path) == $"{entry.OperationId}.{entry.Sequence:D6}.{entry.Kind}.json",
            "Native event contents disagree with their immutable filename.");
        return entry;
    }

    private static NativeCompletedInterruption RecordCompletedInterruption(ProfileStore store, string temporary, string target, string baselinePath)
    {
        ProfileStore.RejectReparseAncestors(temporary);
        var completedBytes = File.ReadAllBytes(temporary);
        var completed = ReadNativeEvent(temporary, target);
        Require(completed.Kind == "Completed" && completed.RelatedOperationId is null && !File.Exists(target),
            "The interruption must occur after complete production Completed JSON flush and before final publication.");
        var prefix = ReadOperationEvents(store, completed.OperationId);
        Require(prefix.Length >= 4 && prefix.Length == completed.Sequence && prefix.Select(entry => entry.Sequence).SequenceEqual(Enumerable.Range(0, completed.Sequence)) &&
            prefix[0].Kind == "Started" && prefix[1].Kind == "BackupCreated" && prefix[^1].Kind == "ChangeVerified" &&
            prefix.All(entry => entry.PlanId == completed.PlanId && entry.ScopeFingerprint == completed.ScopeFingerprint &&
                entry.Kind is not "Completed" and not "Failed" and not "Reconciled"),
            "The native Interrupted fixture must have the complete verified prefix, no final Completed, and no Failed event.");
        using var parsed = JsonDocument.Parse(completedBytes);
        var hashes = CaptureRecoveryFiles(store);
        Require(hashes.Remove(temporary), "The flushed Completed temporary file was absent from the interruption snapshot.");
        var staged = new NativeCompletedInterruption(store.Root, baselinePath, completed.OperationId, temporary, target,
            completedBytes, Hash(completedBytes), parsed.RootElement.GetProperty("RecordedAtUtc").GetDateTimeOffset(),
            hashes, CountBackups(store), CountOperationStarts(store));
        var path = CompletedFixturePath(completed.OperationId, reconciled: false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(staged));
        return staged;
    }

    private static NativeCompletedInterruption ReadCompletedInterruption(ProfileStore store, string baselinePath)
    {
        var directory = Path.Combine(LabRoot, "completed-interruptions");
        ProfileStore.RejectReparseAncestors(directory);
        Require(Directory.Exists(directory), "No native Completed interruption witness exists.");
        var staged = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Select(path =>
        {
            ProfileStore.RejectReparseAncestors(path);
            return JsonSerializer.Deserialize<NativeCompletedInterruption>(File.ReadAllBytes(path))
                ?? throw new InvalidDataException("The native Completed interruption witness is empty.");
        }).Where(item => item.StoreRoot == store.Root && item.BaselinePath == baselinePath)
            .OrderByDescending(item => item.RecordedAtUtc).FirstOrDefault()
            ?? throw new InvalidDataException("No native Completed witness belongs to the current store and immutable baseline.");
        Require(Guid.TryParseExact(staged.OperationId, "N", out _) && Hash(staged.CompletedBytes) == staged.CompletedSha256 &&
            Path.GetDirectoryName(staged.FinalPath) == Path.Combine(store.Root, "setup-recovery", "operations") &&
            staged.TemporaryPath.StartsWith(staged.FinalPath + ".", StringComparison.Ordinal) && staged.TemporaryPath.EndsWith(".tmp", StringComparison.Ordinal),
            "The immutable native Completed witness has an invalid identity, location or hash.");
        return staged;
    }

    private static void ReconcileCompletedInterruption(WindowsSetupRecoveryEnvironment native, ProfileStore store, SetupRecovery recovery,
        CommandJournal journal, Dictionary<string, object?> report, string baselinePath)
    {
        var staged = ReadCompletedInterruption(store, baselinePath);
        Require(File.Exists(staged.TemporaryPath) && File.ReadAllBytes(staged.TemporaryPath).SequenceEqual(staged.CompletedBytes),
            "The exact complete Completed original is missing or changed before native preservation.");
        RequireNoOldCompletion(store, staged);
        RequireFileHashes(staged.OriginalFileHashes);
        VerifyCompletedReadOnly(native, store, journal, report, baselinePath, expectBlocked: true);
        string preserved;
        string metadata;
        byte[] metadataBytes;
        string newOperation;
        using (journal.AcquireLease())
        {
            VerifyPreservationBaseline(native, store, baselinePath);
            var profile = store.Load() ?? throw new InvalidDataException("The native Completed fixture profile disappeared.");
            var observation = native.Inspect(store, profile).Normalize();
            Require(journal.ReadPending(profile, observation.Install.StartExePath) is null && observation.BoxProcessIds.Count == 0,
                "Native Completed reconciliation requires no pending command and an empty owned box.");
            var evidence = recovery.InspectInterruptedEvidence();
            var original = evidence.Items.Single(item => item.OriginalPath == staged.TemporaryPath);
            Require(evidence.CanPreserve && evidence.BlocksExecution && original.Kind == SetupRecoveryEvidenceKind.CompleteRecord &&
                original.OperationId == staged.OperationId && original.Sha256 == staged.CompletedSha256 &&
                original.Length == staged.CompletedBytes.LongLength && original.PreservationPath is null,
                "The exact complete Completed original was not classified for approved native preservation.");
            Require(recovery.ReconcileInterruptedEvidence(evidence).Succeeded, "Native Completed original preservation failed.");
            var afterPreserve = new SetupRecovery(store, native).InspectInterruptedEvidence();
            var archive = afterPreserve.Items.Single(item => item.OriginalPath == staged.TemporaryPath);
            Require(afterPreserve.BlocksExecution && archive.PreservationPath is not null && !archive.NeedsPublication &&
                !File.Exists(staged.TemporaryPath), "Preservation alone incorrectly completed the old operation or requested Completed publication.");
            preserved = archive.PreservationPath!;
            metadata = Path.Combine(Path.GetDirectoryName(preserved)!, "metadata.json");
            metadataBytes = File.ReadAllBytes(metadata);
            Require(File.ReadAllBytes(preserved).SequenceEqual(staged.CompletedBytes), "Native preservation changed the complete Completed original bytes.");
            using (var document = JsonDocument.Parse(metadataBytes))
            {
                var value = document.RootElement;
                Require(value.GetProperty("OriginalPath").GetString() == staged.TemporaryPath &&
                    value.GetProperty("PreservationPath").GetString() == preserved && value.GetProperty("OperationId").GetString() == staged.OperationId &&
                    value.GetProperty("Sha256").GetString() == staged.CompletedSha256 && value.GetProperty("Length").GetInt64() == staged.CompletedBytes.LongLength,
                    "Native preservation metadata does not match the exact Completed origin, bytes and operation.");
            }
            RequireNoOldCompletion(store, staged);
            RequireFileHashes(staged.OriginalFileHashes);
            // This fresh service reads the already-preserved original before earning
            // a new operation; the historical Completed cannot authorize completion.
            var restarted = new SetupRecovery(store, native);
            var plan = restarted.Inspect();
            Require(plan.State == SetupRecoveryState.Interrupted && plan.IsReconciliation && plan.InterruptedOperationId == staged.OperationId,
                "The preserved Completed did not require a freshly inspected native reconciliation.");
            var result = restarted.Apply(plan, true);
            ApplyAndVerify(result, native, store, report);
            newOperation = result.OperationId;
            Require(newOperation != staged.OperationId && CountOperationStarts(store) == staged.StartedCount + 1 && CountBackups(store) == staged.BackupCount + 1,
                "Fresh Completed reconciliation must create exactly one new operation and backup.");
            RequireCompletedReference(store, staged, newOperation);
            RequireFileHashes(staged.OriginalFileHashes);
            Require(File.ReadAllBytes(preserved).SequenceEqual(staged.CompletedBytes) && File.ReadAllBytes(metadata).SequenceEqual(metadataBytes),
                "Fresh native reconciliation changed the preserved original or immutable metadata.");
            VerifyPreservationBaseline(native, store, baselinePath);
        }
        var witness = new NativeCompletedReconciliation(staged.OperationId, newOperation, preserved, metadata,
            Hash(metadataBytes), CaptureRecoveryFiles(store), CountBackups(store), CountOperationStarts(store));
        var witnessPath = CompletedFixturePath(staged.OperationId, reconciled: true);
        Directory.CreateDirectory(Path.GetDirectoryName(witnessPath)!);
        WriteNew(witnessPath, JsonSerializer.SerializeToUtf8Bytes(witness));
        InspectCompletedReconciliation(native, store, new SetupRecovery(store, native), journal, report, baselinePath);
        report["FreshOperationId"] = newOperation;
        report["NativeCompletedPreservationAndReconciliation"] = true;
        report["OriginalBytesAndMetadataPreserved"] = true;
        report["OriginalCompletedRepublished"] = 0;
        report["NewReconciliationOperations"] = 1;
        report["NewReconciliationBackups"] = 1;
        report["CoverageLimit"] = "Actual native settings, complete production Completed temporary bytes, abrupt injected process exit, same-handle preservation and a new native revalidation operation. The interruption timing is injected; no account operations or power-loss behavior are claimed.";
    }

    private static void InspectCompletedReconciliation(WindowsSetupRecoveryEnvironment native, ProfileStore store, SetupRecovery recovery,
        CommandJournal journal, Dictionary<string, object?> report, string baselinePath)
    {
        var staged = ReadCompletedInterruption(store, baselinePath);
        var path = CompletedFixturePath(staged.OperationId, reconciled: true);
        ProfileStore.RejectReparseAncestors(path);
        var witness = JsonSerializer.Deserialize<NativeCompletedReconciliation>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("Native Completed reconciliation witness is missing.");
        Require(witness.OldOperationId == staged.OperationId && File.ReadAllBytes(witness.PreservedPath).SequenceEqual(staged.CompletedBytes) &&
            Hash(File.ReadAllBytes(witness.MetadataPath)) == witness.MetadataSha256,
            "Native Completed preserved original or metadata changed across processes.");
        RequireCompletedReference(store, staged, witness.NewOperationId);
        RequireFileSnapshot(store, witness.FileHashes);
        using (journal.TryAcquireInspectionLease(recoverMissingLock: false))
            Require(!recovery.HasUnfinishedRecovery && recovery.Inspect().IsHealthy, "A fresh process still rejects the reconciled native Completed original.");
        VerifyCompletedReadOnly(native, store, journal, report, baselinePath, expectBlocked: false);
        RequireFileSnapshot(store, witness.FileHashes);
        Require(CountBackups(store) == witness.BackupCount && CountOperationStarts(store) == witness.StartedCount,
            "Repeated native read-only inspection created another operation or backup.");
        RequireNoOldCompletion(store, staged);
        report["InterruptedOperationId"] = staged.OperationId;
        report["FreshOperationId"] = witness.NewOperationId;
        report["PreservedOriginalSha256"] = staged.CompletedSha256;
        report["PreservedMetadataSha256"] = witness.MetadataSha256;
        report["OriginalCompletedRepublished"] = 0;
        report["RepeatedOperationIncrease"] = 0;
        report["RepeatedBackupIncrease"] = 0;
        report["OriginalBytesAndMetadataPreserved"] = true;
    }

    private static void VerifyCompletedReadOnly(WindowsSetupRecoveryEnvironment native, ProfileStore store, CommandJournal journal,
        Dictionary<string, object?> report, string baselinePath, bool expectBlocked)
    {
        var files = CaptureRecoveryFiles(store);
        var profile = store.Load() ?? throw new InvalidDataException("Native read-only fixture profile is missing.");
        var before = native.Inspect(store, profile).Normalize();
        Require(before.BoxProcessIds.Count == 0 && before.Ui.Fresh && before.Ui.ApprovedManagementProcesses.Count == 0,
            "Native read-only assertions require the existing empty fixture with no active current-user management UI.");
        var diagnostics = new List<string>();
        for (var repeat = 0; repeat < 3; repeat++)
        {
            using var controller = new SidetalkController(readOnly: true);
            var snapshot = controller.InspectAsync().GetAwaiter().GetResult();
            var evidence = controller.InspectInterruptedEvidenceAsync().GetAwaiter().GetResult();
            Require(snapshot.InspectionFailure is null && evidence.BlocksExecution == expectBlocked,
                "A new native controller failed Completed evidence inspection: " + snapshot.Detail + " | " + evidence.Detail);
            if (expectBlocked) Require(!snapshot.CanLaunch, "Read-only pre-preservation inspection allowed an unfinished native operation.");
            else if (snapshot.Kakao.Available) Require(snapshot.CanLaunch && snapshot.CurrentConfigurationVerified,
                "The reconciled original blocked verified native launch readiness: " + snapshot.Detail);
            var output = Path.Combine(LabRoot, "reports", "completed-readonly-" + Guid.NewGuid().ToString("N") + ".json");
            controller.ExportDiagnosticsAsync(output).GetAwaiter().GetResult();
            diagnostics.Add(output);
            report["ActualExecutionState"] = snapshot.State.ToString();
            report["ActualCanLaunch"] = snapshot.CanLaunch;
            report["ExecutionInterpretation"] = snapshot.Kakao.Available
                ? "Actual signed installation and current launch permission inspected; no Kakao launch or account access performed."
                : "Official Kakao installation unavailable: native launch permission untested; Completed evidence and current Sandboxie policy verified.";
            RequireFileSnapshot(store, files);
        }
        using (journal.TryAcquireInspectionLease(recoverMissingLock: false))
        {
            var after = native.Inspect(store, store.Load()).Normalize();
            Require(before.IniBytes.SequenceEqual(after.IniBytes) && before.CurrentUserSettingsSection == after.CurrentUserSettingsSection &&
                after.BoxProcessIds.Count == 0 && after.Ui.Fresh && after.Ui.ApprovedManagementProcesses.Count == 0,
                "Native read-only diagnostics changed settings or observed a changed fixture process state.");
            VerifyPreservationBaseline(native, store, baselinePath);
        }
        report[expectBlocked ? "BeforePreservationReadOnly" : "AfterReconciliationReadOnly"] = new
        {
            FreshControllers = 3, DiagnosticsPaths = diagnostics, SettingsWrites = 0, ProcessTerminationActions = 0,
            OriginalMoves = 0, EventPublications = 0, NewCompletionRecords = 0, OtherFixtureFileChanges = 0
        };
    }

    private static NativeEvent[] ReadOperationEvents(ProfileStore store, string operationId) =>
        Directory.EnumerateFiles(Path.Combine(store.Root, "setup-recovery", "operations"), operationId + ".*.json", SearchOption.TopDirectoryOnly)
            .Select(path => ReadNativeEvent(path)).OrderBy(entry => entry.Sequence).ToArray();

    private static void RequireNoOldCompletion(ProfileStore store, NativeCompletedInterruption staged) =>
        Require(!File.Exists(staged.FinalPath) && ReadOperationEvents(store, staged.OperationId).All(entry => entry.Kind is not "Completed" and not "Failed"),
            "The old Completed was republished or a Failed event was added to mask the exact interruption.");

    private static void RequireCompletedReference(ProfileStore store, NativeCompletedInterruption staged, string newOperation)
    {
        RequireNoOldCompletion(store, staged);
        var old = ReadOperationEvents(store, staged.OperationId);
        var related = ReadOperationEvents(store, newOperation);
        Require(old[^1].Kind == "Reconciled" && old[^1].RelatedOperationId == newOperation && related.Count(entry => entry.Kind == "Completed") == 1 &&
            related[^1].Kind is "Completed" or "ConsentFailed" && old[^1].PlanId == related[^1].PlanId && old[^1].ScopeFingerprint == related[^1].ScopeFingerprint,
            "The old native operation does not reference the actual newly completed revalidation operation and approval scope.");
    }

    private static string CompletedFixturePath(string operationId, bool reconciled) =>
        Path.Combine(LabRoot, reconciled ? "completed-reconciliations" : "completed-interruptions", operationId + ".json");

    private static Dictionary<string, string> CaptureRecoveryFiles(ProfileStore store)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[] { "setup-recovery", "recovery-evidence", "command-journal" }.Select(name => Path.Combine(store.Root, name)))
        {
            ProfileStore.RejectReparseAncestors(directory);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(path) is "command.lock" or "recovery.lock") continue;
                ProfileStore.RejectReparseAncestors(path);
                result.Add(path, Hash(File.ReadAllBytes(path)));
            }
        }
        if (File.Exists(store.ProfilePath)) result.Add(store.ProfilePath, Hash(File.ReadAllBytes(store.ProfilePath)));
        return result;
    }

    private static void RequireFileHashes(IReadOnlyDictionary<string, string> expected)
    {
        foreach (var pair in expected)
        {
            ProfileStore.RejectReparseAncestors(pair.Key);
            Require(File.Exists(pair.Key) && Hash(File.ReadAllBytes(pair.Key)) == pair.Value, "An existing native journal, profile or preservation file changed: " + pair.Key);
        }
    }

    private static void RequireFileSnapshot(ProfileStore store, IReadOnlyDictionary<string, string> expected)
    {
        var actual = CaptureRecoveryFiles(store);
        Require(actual.Count == expected.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value),
            "Read-only native inspection changed, added or removed a fixture file.");
    }

    private static int CountOperationStarts(ProfileStore store) =>
        Directory.EnumerateFiles(Path.Combine(store.Root, "setup-recovery", "operations"), "*.Started.json", SearchOption.TopDirectoryOnly).Count();

    private static void ReconcilePreviousBootCommand(WindowsSetupRecoveryEnvironment native, ProfileStore store,
        CommandJournal journal, Dictionary<string, object?> report, string baselinePath)
    {
        PendingCommandRecord pending;
        BoxProfile profile;
        string startExe;
        using (journal.TryAcquireInspectionLease(recoverMissingLock: false))
        {
            VerifyPreservationBaseline(native, store, baselinePath);
            profile = store.Load() ?? throw new InvalidDataException("The approved profile disappeared before command reconciliation.");
            var observation = native.Inspect(store, profile).Normalize();
            Require(IsolationPolicy.Validate(profile, observation.Configuration).Safe && observation.BoxProcessIds.Count == 0,
                "The current native owned box is not verified and empty.");
            startExe = observation.Install.StartExePath;
            pending = journal.ReadPending(profile, startExe) ?? throw new InvalidDataException("The durable pre-reboot intent is missing.");
            Require(pending.Process is null && pending.RecoveryContext?.Boot is not null &&
                pending.RecoveryContext.UserSid == profile.UserSid &&
                WindowsCommandEvidence.AuthenticatesBootEvidence(pending.OperationId, pending.RecoveryContext),
                "The PID-less command does not retain authenticated evidence from the approved user and profile.");
            var currentUptime = WindowsCommandEvidence.ReadUptimeMilliseconds();
            Require(currentUptime < pending.RecoveryContext!.Boot!.UptimeMilliseconds &&
                WindowsCommandEvidence.ProvesPreviousBoot(pending.OperationId, pending.RecoveryContext),
                "No actual previous boot was proved. Reboot the test VM and run promptly; wall-clock time is not evidence.");
            using var recovered = new ProcessCommandRecovery().Recover(pending);
            Require(recovered.RecoveryState == CommandRecoveryState.PreviousBoot && recovered.HasExited,
                "The production process recovery did not distinguish native previous-boot evidence.");
            int? exitCode = null;
            try { exitCode = recovered.ExitCode; }
            catch (InvalidOperationException) { }
            Require(exitCode is null, "A PID-less intent must not acquire a fabricated successful exit code.");
            report["InterruptedOperationId"] = pending.OperationId;
            report["CommandSchema"] = pending.Schema;
            report["RecordedUptimeMilliseconds"] = pending.RecoveryContext!.Boot!.UptimeMilliseconds;
            report["CurrentUptimeMilliseconds"] = currentUptime;
            report["BootWitnessAuthenticated"] = true;
            report["PreviousBootProved"] = true;
            report["CommandRecoveryState"] = recovered.RecoveryState.ToString();
            report["CommandProcessCreated"] = false;
            report["PidRecorded"] = false;
            report["ExitCode"] = null;
        }
        // Do not hold an outer journal lease: production must earn its own cross-
        // process permission and freshly observe the owned box before completion.
        using (var controller = new SidetalkController())
        {
            var snapshot = controller.InspectAsync().GetAwaiter().GetResult();
            Require(snapshot.InspectionFailure is null && snapshot.CurrentConfigurationVerified &&
                snapshot.ProcessPolicy == ProcessPolicyStatus.NoProcesses && snapshot.BoxProcessIds.Count == 0 && snapshot.CanLaunch,
                "Production writable inspection did not freshly verify and reconcile the owned box: " + snapshot.Detail);
            report["ActualExecutionState"] = snapshot.State.ToString();
            report["FreshOwnedBoxProcesses"] = snapshot.BoxProcessIds.Count;
            report["ActualCanLaunch"] = snapshot.CanLaunch;
        }
        using (journal.TryAcquireInspectionLease(recoverMissingLock: false))
        {
            Require(store.Load() == profile && journal.IsCompleted(pending) && journal.ReadPending(profile, startExe) is null,
                "The production controller did not publish a completed command after native previous-boot and fresh-box evidence.");
            VerifyPreservationBaseline(native, store, baselinePath);
        }
        report["CommandJournalReconciled"] = true;
        report["CoverageLimit"] = "Actual VM reboot, authenticated DPAPI/monotonic uptime, production process recovery and fresh-box command journal reconciliation; no Start.exe process, Kakao launch, exit-code success or account operation is claimed.";
    }

    private static string RequireReportPath(string path)
    {
        var full = Path.GetFullPath(path);
        Require(full.StartsWith(LabRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            full.EndsWith(".json", StringComparison.OrdinalIgnoreCase), "Reports must be new JSON files below C:\\SIDETALK-Lab.");
        ProfileStore.RejectReparseAncestors(full);
        Require(!File.Exists(full), "Existing integration reports are never overwritten.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    private static string RequireBaselinePath(string path)
    {
        var full = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(LabRoot, path));
        Require(full.StartsWith(LabRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            full.EndsWith(".json", StringComparison.OrdinalIgnoreCase), "Immutable baselines must be JSON files below C:\\SIDETALK-Lab.");
        ProfileStore.RejectReparseAncestors(full);
        return full;
    }

    private static void CheckpointExternalSettings(WindowsSetupRecoveryEnvironment native, ProfileStore store,
        string newBaselinePath, Dictionary<string, object?> report)
    {
        var originalPath = RequireBaselinePath("preservation-baseline.json");
        Require(!newBaselinePath.Equals(originalPath, StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(newBaselinePath) && !Directory.Exists(newBaselinePath), "A checkpoint needs a new baseline; existing baselines are never replaced.");
        using var original = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var originalBytes = new byte[checked((int)original.Length)];
        original.ReadExactly(originalBytes);
        var baseline = JsonSerializer.Deserialize<PreservationBaseline>(originalBytes)
            ?? throw new InvalidDataException("The original immutable baseline is missing.");
        // Only this explicitly labeled external checkpoint adopts OtherSettingsSha256.
        // Original profile, ownership, file sentinel and HKCU sentinel remain mandatory.
        VerifyPreservationBaseline(native, store, originalPath, verifyOtherSettings: false);
        var profile = store.Load() ?? throw new InvalidDataException("The checkpoint profile disappeared.");
        var observation = native.Inspect(store, profile).Normalize();
        Require(IsolationPolicy.Validate(profile, observation.Configuration).Safe && observation.BoxProcessIds.Count == 0 &&
            observation.Ui.Fresh && observation.Ui.ApprovedManagementProcesses.Count == 0,
            "External checkpoint requires currently verified policy, an empty owned box and no active management tool.");
        SandboxieIniSnapshot.RequireEquivalent(observation.IniBytes, observation.ExplicitSections);
        var currentOtherHash = HashOtherSettings(observation, profile);
        Require(currentOtherHash != baseline.OtherSettingsSha256, "No external settings difference was observed to checkpoint.");
        var updated = baseline with { OtherSettingsSha256 = currentOtherHash };
        ProfileStore.RejectReparseAncestors(newBaselinePath);
        Directory.CreateDirectory(Path.GetDirectoryName(newBaselinePath)!);
        WriteNew(newBaselinePath, JsonSerializer.SerializeToUtf8Bytes(updated));
        VerifyPreservationBaseline(native, store, newBaselinePath);
        Require(Hash(File.ReadAllBytes(originalPath)) == Hash(originalBytes), "The original baseline bytes changed during checkpoint.");
        report["ResultKind"] = "External-change checkpoint";
        report["CoverageLimit"] = "Adopts observed external management-UI changes as a new immutable lab fixture segment. It does not repair settings, complete an interrupted operation, replace the original baseline or remove another box.";
        report["RecoveryApplied"] = false;
        report["RecoverySucceeded"] = false;
        report["SettingsWritesPerformed"] = 0;
        report["OriginalBaselinePath"] = originalPath;
        report["OriginalBaselineBytesBase64"] = Convert.ToBase64String(originalBytes);
        report["OriginalBaselineSha256"] = Hash(originalBytes);
        report["OriginalBaselinePreserved"] = true;
        report["OriginalOtherSettingsSha256"] = baseline.OtherSettingsSha256;
        report["ObservedOtherSettingsSha256"] = currentOtherHash;
        report["NewBaselinePath"] = newBaselinePath;
        report["NewBaselineSha256"] = Hash(File.ReadAllBytes(newBaselinePath));
        report["ObservedIniSha256"] = Hash(observation.IniBytes);
        report["ObservedOtherSettings"] = observation.ExplicitSections.Where(section => section.Key != profile.BoxName)
            .ToDictionary(section => section.Key, section => section.Value.Where(setting =>
                !(section.Key.Equals("GlobalSettings", StringComparison.OrdinalIgnoreCase) && setting.Key.Equals("Template", StringComparison.OrdinalIgnoreCase)) &&
                !(section.Key == observation.CurrentUserSettingsSection && setting.Key.Equals("SbieCtrl_AutoRunSoftCompat", StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(setting => setting.Key, setting => setting.Value.ToArray(), StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        report["ObservedOtherBoxNames"] = observation.OtherBoxNames;
        report["CurrentPolicySafe"] = true;
        report["FreshOwnedBoxProcesses"] = 0;
        report["FileApiEquivalent"] = true;
    }

    private static void Seed(WindowsSetupRecoveryEnvironment native, ProfileStore store, IReadOnlyList<string> templates)
    {
        var profile = store.Load();
        var observation = native.Inspect(store, profile).Normalize();
        Require((templates.Count == 0 || observation.OtherBoxNames.Count == 0) && observation.BoxProcessIds.Count == 0 && observation.Ui.Fresh &&
            observation.Ui.ApprovedManagementProcesses.Count == 0 && observation.UnknownTemplateNames.Count == 0,
            "Lab-only recurrence seeding requires no active box processes, management tools or unknown templates; global-template seeding also requires no other boxes.");
        if (templates.Count == 0)
            Require(profile is not null && IsolationPolicy.Validate(profile, observation.Configuration).Safe,
                "Optional-only seeding requires currently verified owned-box policy.");
        var tool = Path.Combine(observation.Install.SandboxiePath, "SbieIni.exe");
        foreach (var template in templates)
        {
            Require(SixTemplates.Contains(template, StringComparer.Ordinal), "Only the six officially reviewed templates may be seeded.");
            var current = native.Inspect(store, store.Load()).Normalize();
            if (!Read(current, "GlobalSettings", "Template").Contains(template, StringComparer.Ordinal))
                RunTool(tool, ["append", "GlobalSettings", "Template", template]);
            var after = native.Inspect(store, store.Load()).Normalize();
            Require(Read(after, "GlobalSettings", "Template").Contains(template, StringComparer.Ordinal), "SbieIni append was not reflected in the actual file/API capture.");
        }
        var user = native.Inspect(store, store.Load()).Normalize();
        Require(user.CurrentUserSettingsSection is not null, "The actual current-user RPC section is missing.");
        RunTool(tool, ["set", user.CurrentUserSettingsSection!, "SbieCtrl_AutoRunSoftCompat", "y"]);
        var final = native.Inspect(store, store.Load()).Normalize();
        Require(Read(final, user.CurrentUserSettingsSection!, "SbieCtrl_AutoRunSoftCompat").SequenceEqual(["y"]),
            "The real current-user setting did not match the seeded recurrence option.");
        if (templates.Count == 0)
        {
            Require(store.Load() == profile && final.ConfigPath == observation.ConfigPath &&
                final.CurrentUserSettingsSection == observation.CurrentUserSettingsSection &&
                IsolationPolicy.Validate(profile!, final.Configuration).Safe && final.BoxProcessIds.Count == 0 && final.Ui.Fresh &&
                final.Ui.ApprovedManagementProcesses.Count == 0 && HashExceptCurrentRecurrenceOption(final) == HashExceptCurrentRecurrenceOption(observation),
                "Optional-only seeding changed another setting, profile, scope or process state.");
            SandboxieIniSnapshot.RequireEquivalent(final.IniBytes, final.ExplicitSections);
        }
    }

    private static void RunTool(string path, IReadOnlyList<string> arguments)
    {
        RuntimeVerifier.RequireComponent(path);
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(path)! };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start lab SbieIni command.");
        Require(process.WaitForExit(15000), "Lab SbieIni command exit is unconfirmed; its process remains intact.");
        Require(process.ExitCode == 0, "Lab SbieIni command failed with exit code " + process.ExitCode);
    }

    private static void ApplyAndVerify(SetupRecoveryApplyResult result, WindowsSetupRecoveryEnvironment native,
        ProfileStore store, Dictionary<string, object?> report)
    {
        Require(result.Succeeded, result.Detail);
        var profile = store.Load() ?? throw new InvalidOperationException("The approved profile was not published.");
        var fresh = native.Inspect(store, profile).Normalize();
        Require(IsolationPolicy.Validate(profile, fresh.Configuration).Safe && fresh.BoxProcessIds.Count == 0, "Fresh actual policy or owned-box process verification failed.");
        Require(Read(fresh, fresh.CurrentUserSettingsSection!, "SbieCtrl_AutoRunSoftCompat").SequenceEqual(["n"]), "The actual current-user recurrence option is not n.");
        Require(!Read(fresh, "GlobalSettings", "Template").Any(item => SixTemplates.Contains(item, StringComparer.Ordinal)), "A reviewed conflicting global selector remained.");
        SandboxieIniSnapshot.RequireEquivalent(fresh.IniBytes, fresh.ExplicitSections);
        report["OperationId"] = result.OperationId;
        report["FileApiEquivalent"] = true;
        report["CurrentPolicySafe"] = true;
        report["IniSha256"] = Hash(fresh.IniBytes);
        report["ProfileSha256"] = Hash(File.ReadAllBytes(store.ProfilePath));
    }

    private static IReadOnlyList<string> Read(SetupRecoveryObservation observation, string section, string key) =>
        observation.ExplicitSections.TryGetValue(section, out var values) && values.TryGetValue(key, out var found) ? found : [];

    private static void EnsurePreservationBaseline(WindowsSetupRecoveryEnvironment native, ProfileStore store, string baselinePath)
    {
        if (File.Exists(baselinePath))
        {
            VerifyPreservationBaseline(native, store, baselinePath);
            return;
        }
        var profile = store.Load() ?? throw new InvalidOperationException("No prepared profile for preservation baseline.");
        ProfileStore.RejectReparseAncestors(profile.StoragePath);
        Directory.CreateDirectory(profile.StoragePath);
        var sentinelPath = Path.Combine(profile.StoragePath, "SIDETALK-LAB-sentinel.bin");
        var sentinelBytes = Encoding.UTF8.GetBytes(SentinelText);
        ProfileStore.RejectReparseAncestors(sentinelPath);
        if (File.Exists(sentinelPath))
            Require(File.ReadAllBytes(sentinelPath).SequenceEqual(sentinelBytes), "Existing file sentinel differs from the interrupted lab fixture.");
        else WriteNew(sentinelPath, sentinelBytes);
        using var sentinel = Registry.CurrentUser.CreateSubKey(RegistrySentinel);
        var existingRegistryValue = sentinel.GetValue("Sentinel");
        Require(existingRegistryValue is null || existingRegistryValue is string text && text == SentinelText,
            "Existing registry sentinel is never replaced.");
        if (existingRegistryValue is null) sentinel.SetValue("Sentinel", SentinelText, RegistryValueKind.String);
        var observation = native.Inspect(store, profile).Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
        WriteNew(baselinePath, JsonSerializer.SerializeToUtf8Bytes(new PreservationBaseline(
            Hash(File.ReadAllBytes(store.ProfilePath)), profile.BoxName, profile.OwnerToken, profile.StoragePath,
            Hash(File.ReadAllBytes(sentinelPath)), HashOtherSettings(observation, profile), SentinelText)));
    }

    private static void VerifyPreservationBaseline(WindowsSetupRecoveryEnvironment native, ProfileStore store, string baselinePath, bool verifyOtherSettings = true)
    {
        ProfileStore.RejectReparseAncestors(baselinePath);
        var baseline = JsonSerializer.Deserialize<PreservationBaseline>(File.ReadAllBytes(baselinePath))
            ?? throw new InvalidDataException("The immutable lab preservation baseline is missing.");
        var profile = store.Load() ?? throw new InvalidDataException("Prepared test profile disappeared.");
        Require(Hash(File.ReadAllBytes(store.ProfilePath)) == baseline.ProfileSha256 && profile.BoxName == baseline.BoxName &&
            profile.OwnerToken == baseline.OwnerToken && profile.StoragePath == baseline.StoragePath, "Profile bytes or owned identity changed during recovery.");
        Require(Hash(File.ReadAllBytes(Path.Combine(profile.StoragePath, "SIDETALK-LAB-sentinel.bin"))) == baseline.SentinelSha256, "Owned-box file sentinel changed.");
        using var sentinel = Registry.CurrentUser.OpenSubKey(RegistrySentinel);
        Require((sentinel?.GetValue("Sentinel") as string) == baseline.RegistrySentinel, "Unrelated current-user registry data changed.");
        if (verifyOtherSettings) Require(HashOtherSettings(native.Inspect(store, profile).Normalize(), profile) == baseline.OtherSettingsSha256, "Unrelated Sandboxie sections or settings changed.");
    }

    private static string HashOtherSettings(SetupRecoveryObservation observation, BoxProfile profile)
    {
        var text = new StringBuilder();
        foreach (var section in observation.ExplicitSections.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (section.Key.Equals(profile.BoxName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var setting in section.Value.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (section.Key.Equals("GlobalSettings", StringComparison.OrdinalIgnoreCase) && setting.Key.Equals("Template", StringComparison.OrdinalIgnoreCase)) continue;
                if (section.Key == observation.CurrentUserSettingsSection && setting.Key.Equals("SbieCtrl_AutoRunSoftCompat", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var value in setting.Value) text.Append(section.Key).Append('\0').Append(setting.Key).Append('\0').Append(value).Append('\n');
            }
        }
        return Hash(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static string HashExceptCurrentRecurrenceOption(SetupRecoveryObservation observation)
    {
        var text = new StringBuilder();
        foreach (var section in observation.ExplicitSections.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var settings = section.Value.Where(setting =>
                !(section.Key == observation.CurrentUserSettingsSection && setting.Key.Equals("SbieCtrl_AutoRunSoftCompat", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            if (section.Key == observation.CurrentUserSettingsSection && settings.Length == 0) continue;
            text.Append("section\n").Append(section.Key).Append('\n');
            foreach (var setting in settings)
            {
                foreach (var value in setting.Value) text.Append(section.Key).Append('\0').Append(setting.Key).Append('\0').Append(value).Append('\n');
            }
        }
        return Hash(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static int CountBackups(ProfileStore store) => Directory.Exists(Path.Combine(store.Root, "setup-recovery", "history"))
        ? Directory.EnumerateFiles(Path.Combine(store.Root, "setup-recovery", "history"), "*.backup", SearchOption.AllDirectories).Count() : 0;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    private static void WriteReport(string path, Dictionary<string, object?> report) => WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true }));
    private static void WriteNew(string path, byte[] bytes)
    {
        ProfileStore.RejectReparseAncestors(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private sealed record PreservationBaseline(string ProfileSha256, string BoxName, string OwnerToken,
        string StoragePath, string SentinelSha256, string OtherSettingsSha256, string RegistrySentinel);

    private sealed record StagedPartialEvent(string OperationId, int IntentSequence, string Path, string Sha256, long Length);
    private sealed record NativeEvent(string OperationId, int Sequence, string Kind, string PlanId, string ScopeFingerprint, string Detail,
        string? RelatedOperationId);
    private sealed record NativeCompletedInterruption(string StoreRoot, string BaselinePath, string OperationId, string TemporaryPath,
        string FinalPath, byte[] CompletedBytes, string CompletedSha256, DateTimeOffset RecordedAtUtc,
        Dictionary<string, string> OriginalFileHashes, int BackupCount, int StartedCount);
    private sealed record NativeCompletedReconciliation(string OldOperationId, string NewOperationId, string PreservedPath,
        string MetadataPath, string MetadataSha256, Dictionary<string, string> FileHashes, int BackupCount, int StartedCount);

    private sealed class CrashAfterRealChange(WindowsSetupRecoveryEnvironment native, Action<SetupRecoveryPlan, SetupRecoveryChange> abruptExit) : ISetupRecoveryEnvironment
    {
        public SetupRecoveryObservation Inspect(ProfileStore store, BoxProfile? profile, CancellationToken cancellationToken = default) => native.Inspect(store, profile, cancellationToken);
        public SetupRecoveryUiResult QuiesceManagementUi(SetupRecoveryObservation observation, SetupRecoveryPlan plan, CancellationToken cancellationToken = default) => native.QuiesceManagementUi(observation, plan, cancellationToken);
        public SetupRecoveryChangeResult ApplyChange(SetupRecoveryObservation observation, SetupRecoveryPlan plan, SetupRecoveryChange change, CancellationToken cancellationToken = default)
        {
            var result = native.ApplyChange(observation, plan, change, cancellationToken);
            if (result.CommandSucceeded && result.Verified) abruptExit(plan, change);
            return result;
        }
    }
}
