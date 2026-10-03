using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sidetalk.Core;

internal sealed partial class SetupRecovery
{
    private string EvidenceRoot => Path.Combine(store.Root, "recovery-evidence");
    private static readonly Regex ImmutableTemporaryName = new(@"\A(?<target>.+\.json)\.[a-f0-9]{32}\.tmp\z", RegexOptions.CultureInvariant);

    /// <summary>Read-only diagnosis, independent of permission to edit Sandboxie settings.</summary>
    public SetupRecoveryEvidenceInspection InspectInterruptedEvidence(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var items = DiscoverInterruptedEvidence();
            var scope = items.Count == 0 ? string.Empty : EvidenceScope(cancellationToken);
            return new(items, items.Any(item => item.PreservationPath is null || item.NeedsPublication),
                items.Any(item => item.BlocksExecution) || ReadInterruptedOperation() is not null,
                items.Count == 0 ? "보존할 중단 파일이 없습니다." : "원본과 관련 작업을 보존한 뒤 현재 설정을 다시 확인할 수 있습니다.",
                EvidenceRoot, EvidenceSnapshot(items), scope);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return new(Array.Empty<SetupRecoveryEvidenceItem>(), false, true, error.Message, EvidenceRoot, string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Caller holds the shared CommandJournal lease and has checked pending commands.
    /// This action preserves evidence; it never completes an INI mutation or grants consent.
    /// </summary>
    public SetupRecoveryApplyResult ReconcileInterruptedEvidence(SetupRecoveryEvidenceInspection approved,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approved);
        var operationId = Guid.NewGuid().ToString("N");
        try
        {
            using var lease = AcquireRecoveryLease();
            var fresh = InspectInterruptedEvidence(cancellationToken);
            if (!approved.CanPreserve || !fresh.CanPreserve || approved.SnapshotFingerprint != fresh.SnapshotFingerprint ||
                approved.ScopeFingerprint != fresh.ScopeFingerprint)
                return Failed(operationId, "파일·프로필·설정이 변경되었거나 보존 조건을 확인할 수 없습니다. 다시 확인하세요.", null) with { HistoryPath = EvidenceRoot };

            foreach (var item in fresh.Items.Where(item => item.PreservationPath is null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (EvidenceScope(cancellationToken) != fresh.ScopeFingerprint)
                    throw new InvalidDataException("보존 중 프로필 또는 설정이 변경되었습니다. 기존 원본을 유지합니다.");
                progress?.Report("중단 원본 보존: " + Path.GetFileName(item.OriginalPath));
                PreserveEvidence(item);
            }
            foreach (var directory in Directory.Exists(EvidenceRoot) ? Directory.EnumerateDirectories(EvidenceRoot) : Array.Empty<string>())
            {
                var manifest = ReadEvidenceManifest(directory);
                if (File.Exists(manifest.PreservationPath)) ResumePublishedEvidence(manifest, manifest.PreservationPath, inspectOnly: false);
            }
            // A successful preservation is deliberately not a Completed recovery event.
            // The existing Interrupted/Apply route performs the fresh INI/API verification.
            var after = InspectInterruptedEvidence(cancellationToken);
            var detail = after.BlocksExecution
                ? "원본을 보존했습니다. 미완료 변경 또는 출처를 확인할 수 없는 근거가 남아 실행을 계속 차단합니다. 준비 상태를 다시 확인하세요."
                : "원본을 보존하고 다시 확인했습니다. 설정 변경이나 자동 복구 동의 완료로 처리하지 않았습니다.";
            return new(operationId, SetupRecoveryState.Applied, detail, true, null, EvidenceRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or OperationCanceledException or JsonException)
        {
            return Failed(operationId, "보존 복구를 중단했습니다. 원본과 이미 저장한 근거를 유지합니다: " + error.Message, null) with { HistoryPath = EvidenceRoot };
        }
    }

    private string EvidenceScope(CancellationToken cancellationToken)
    {
        ProfileStore.RejectReparseAncestors(store.ProfilePath);
        var profileBytes = File.Exists(store.ProfilePath) ? File.ReadAllBytes(store.ProfilePath) : Array.Empty<byte>();
        string observation;
        try
        {
            var profile = store.Load();
            observation = SetupRecoveryFingerprint.FromObservation(environment.Inspect(store, profile, cancellationToken).Normalize(), profile).ScopeKey;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or TimeoutException or JsonException)
        {
            // Archiving bytes alone is safe even when repair cannot inspect the native
            // boundary. The failure stays visible and never authorizes INI changes.
            observation = "unavailable:" + error.GetType().Name + ":" + error.Message;
        }
        return HashBytes(Encoding.UTF8.GetBytes(HashBytes(profileBytes) + "|" + observation));
    }

    private IReadOnlyList<SetupRecoveryEvidenceItem> DiscoverInterruptedEvidence()
    {
        var items = new List<SetupRecoveryEvidenceItem>();
        ProfileStore.RejectReparseAncestors(store.Root);
        if (Directory.Exists(recoveryRoot))
            foreach (var path in EnumerateSafeFiles(recoveryRoot))
            {
                if (Path.GetExtension(path).Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                    items.Add(ClassifyEvidence(path));
                else if (Path.GetDirectoryName(path) == operationsRoot && Path.GetExtension(path) == ".json")
                {
                    var entry = DeserializeEvidence<RecoveryEvent>(File.ReadAllBytes(path));
                    if (entry is null || !EventMatchesTarget(entry, path)) items.Add(ClassifyEvidence(path));
                }
                else if (Path.GetDirectoryName(path) == consentRoot && Path.GetExtension(path) == ".json" &&
                    !ValidConsentEvidence(path))
                    items.Add(ClassifyEvidence(path));
            }
        if (Directory.Exists(operationsRoot))
        {
            var finals = Directory.EnumerateFiles(operationsRoot, "*.json").Select(path =>
                (Path: path, Entry: DeserializeEvidence<RecoveryEvent>(File.ReadAllBytes(path))))
                .Where(pair => pair.Entry is not null).ToArray();
            foreach (var group in finals.GroupBy(pair => pair.Entry!.OperationId, StringComparer.Ordinal))
            {
                try { ValidateOperation(group.Select(pair => pair.Entry!).ToArray()); }
                catch (InvalidDataException)
                {
                    foreach (var pair in group.Where(pair => !items.Any(item => item.OriginalPath == pair.Path)))
                        items.Add(ClassifyEvidence(pair.Path) with { OperationId = pair.Entry!.OperationId });
                }
            }
        }
        if (Directory.Exists(consentRoot))
        {
            var finals = Directory.EnumerateFiles(consentRoot, "*.json").Select(path =>
                (Path: path, Entry: DeserializeEvidence<RecoveryConsent>(File.ReadAllBytes(path))))
                .Where(pair => pair.Entry is not null).ToArray();
            foreach (var duplicate in finals.GroupBy(pair => pair.Entry!.Sequence).Where(group => group.Count() > 1))
                foreach (var pair in duplicate.Where(pair => !items.Any(item => item.OriginalPath == pair.Path)))
                    items.Add(ClassifyEvidence(pair.Path));
        }
        if (Directory.Exists(store.Root))
            foreach (var path in Directory.EnumerateFiles(store.Root, ".profile.*.tmp", SearchOption.TopDirectoryOnly))
                items.Add(ClassifyEvidence(path));
        if (Directory.Exists(EvidenceRoot))
        {
            ProfileStore.RejectReparseAncestors(EvidenceRoot);
            foreach (var directory in Directory.EnumerateFileSystemEntries(EvidenceRoot))
            {
                ProfileStore.RejectReparseAncestors(directory);
                if (!Directory.Exists(directory) || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
                    throw new InvalidDataException("보존 폴더에 확인할 수 없는 항목이 있습니다: " + directory);
                if (!File.Exists(Path.Combine(directory, "metadata.json")))
                {
                    var matchingSource = items.SingleOrDefault(item => EvidenceArchiveId(item.OriginalPath, item.Sha256) == Path.GetFileName(directory));
                    if (matchingSource is null)
                        throw new InvalidDataException("중단된 보존 폴더에 대응하는 원본을 확인할 수 없습니다: " + directory);
                    foreach (var stage in ReadMetadataStages(directory))
                        items.Add(new(Path.Combine(directory, stage.Name), stage.Sha256, stage.Length,
                            SetupRecoveryEvidenceKind.IncompleteRecord, matchingSource.OperationId, false,
                            "중단된 보존 메타데이터 원본입니다. 설정 변경 완료 근거로 사용하지 않습니다.", Path.Combine(directory, stage.Name)));
                    continue;
                }
                var manifest = ReadEvidenceManifest(directory);
                var preserved = Path.Combine(directory, "original.bin");
                if (File.Exists(preserved))
                {
                    var preservedBytes = File.ReadAllBytes(preserved);
                    if (preservedBytes.LongLength != manifest.Length || HashBytes(preservedBytes) != manifest.Sha256)
                        throw new InvalidDataException("보존 원본의 해시가 기록과 다릅니다: " + directory);
                    ResumePublishedEvidence(manifest, preserved, inspectOnly: true);
                    var needsPublication = EvidenceNeedsPublication(manifest);
                    var optionalConsentEvent = manifest.Kind == SetupRecoveryEvidenceKind.CompleteRecord &&
                        DeserializeEvidence<RecoveryEvent>(preservedBytes)?.Kind == "ConsentFailed";
                    items.Add(new(manifest.OriginalPath, manifest.Sha256, manifest.Length, manifest.Kind,
                        manifest.OperationId, manifest.Unresolved || needsPublication && !optionalConsentEvent, manifest.Detail, preserved, needsPublication));
                }
                else if (!File.Exists(manifest.OriginalPath) || HashBytes(File.ReadAllBytes(manifest.OriginalPath)) != manifest.Sha256)
                    throw new InvalidDataException("중단된 보존 기록의 원본 또는 보존본을 확인할 수 없습니다: " + directory);
            }
        }
        return items.OrderBy(item => item.OriginalPath, StringComparer.Ordinal).ThenBy(item => item.PreservationPath, StringComparer.Ordinal).ToArray();
    }

    private SetupRecoveryEvidenceItem ClassifyEvidence(string path)
    {
        ProfileStore.RejectReparseAncestors(path);
        var bytes = File.ReadAllBytes(path);
        var hash = HashBytes(bytes);
        var kind = SetupRecoveryEvidenceKind.UnknownSource;
        string? operationId = null;
        var detail = "파일 흔적의 출처 또는 승인된 작업과의 관계를 확인할 수 없습니다.";
        var parent = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        var match = ImmutableTemporaryName.Match(name);
        var target = match.Success ? Path.Combine(parent, match.Groups["target"].Value) : null;
        var blocks = true;
        if (parent.Equals(consentRoot, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(Path.GetFileNameWithoutExtension(target ?? path), "N", out _))
        {
            kind = SetupRecoveryEvidenceKind.ConsentRecord;
            blocks = false;
            detail = "자동 복구 동의 저장 흔적입니다. 설정 변경이나 실행 권한으로 사용하지 않습니다.";
        }
        else if (parent.Equals(operationsRoot, StringComparison.OrdinalIgnoreCase) && target is not null)
        {
            var parts = Path.GetFileNameWithoutExtension(target).Split('.');
            if (parts.Length == 3 && Guid.TryParseExact(parts[0], "N", out _))
            {
                operationId = parts[0];
                var entry = DeserializeEvidence<RecoveryEvent>(bytes);
                if (entry is not null && EventMatchesTarget(entry, target) && IsEventOriginValid(entry))
                {
                    kind = SetupRecoveryEvidenceKind.CompleteRecord;
                    detail = "완전한 복구 이벤트입니다. 완료 이벤트는 게시하지 않고 현재 상태 재조정을 요구합니다.";
                    if (entry.Kind == "ConsentFailed")
                    {
                        blocks = false;
                        detail = "검증 완료 뒤 선택적 자동 복구 동의 저장 실패 기록입니다. 실행을 차단하지 않습니다.";
                    }
                }
                else if (File.Exists(Path.Combine(operationsRoot, operationId + ".000000.Started.json")))
                {
                    kind = SetupRecoveryEvidenceKind.IncompleteRecord;
                    detail = "기존 미완료 작업에 속한 잘린 기록입니다. 원본을 보존하고 기존 작업을 재조정합니다.";
                }
            }
        }
        else if (parent.Equals(store.Root, StringComparison.OrdinalIgnoreCase) && name.StartsWith(".profile.", StringComparison.Ordinal))
        {
            var candidate = DeserializeEvidence<BoxProfile>(bytes);
            candidate ??= ApprovedCandidateForStagedName(name, out operationId);
            if (candidate is not null && TryFindApprovedCandidate(candidate, out operationId))
            {
                kind = SetupRecoveryEvidenceKind.ApprovedProfileCandidate;
                detail = "이전 승인 프로필 후보와 일치합니다. 같은 소유 정보와 저장 경로를 유지합니다.";
            }
        }
        else if (target is not null && parent.StartsWith(historyRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(parent);
            if (Guid.TryParseExact(id, "N", out _) && File.Exists(Path.Combine(operationsRoot, id + ".000000.Started.json")))
            {
                operationId = id;
                kind = SetupRecoveryEvidenceKind.IncompleteRecord;
                detail = "승인된 작업의 백업/계획 저장 흔적입니다. 설정 변경 완료를 의미하지 않습니다.";
                if (Path.GetFileName(target) == "candidate.profile.json")
                {
                    var candidate = DeserializeEvidence<BoxProfile>(bytes);
                    if (candidate is not null && CandidateMatchesStartedApproval(candidate, id)) kind = SetupRecoveryEvidenceKind.ApprovedProfileCandidate;
                }
            }
        }
        if (target is not null && File.Exists(target) && !bytes.SequenceEqual(File.ReadAllBytes(target)))
        {
            kind = SetupRecoveryEvidenceKind.ConflictingRecord;
            // Consent storage conflicts cannot have changed Sandboxie settings.
            blocks = !parent.Equals(consentRoot, StringComparison.OrdinalIgnoreCase);
            detail = "기존 게시 파일과 내용이 다릅니다. 어느 파일도 덮어쓰지 않습니다.";
        }
        return new(path, hash, bytes.LongLength, kind, operationId, blocks, detail);
    }

    private static bool ValidConsentEvidence(string path)
    {
        var consent = DeserializeEvidence<RecoveryConsent>(File.ReadAllBytes(path));
        return consent is not null && consent.Id == Path.GetFileNameWithoutExtension(path) &&
            Guid.TryParseExact(consent.Id, "N", out _) && consent.Sequence > 0 && consent.RecordedAtUtc != default &&
            (!consent.Granted || !string.IsNullOrWhiteSpace(consent.PlanRevision) && !string.IsNullOrWhiteSpace(consent.ScopeFingerprint) &&
                !string.IsNullOrWhiteSpace(consent.UserSid) && !string.IsNullOrWhiteSpace(consent.BoxName) &&
                !string.IsNullOrWhiteSpace(consent.StoragePath) && !string.IsNullOrWhiteSpace(consent.ConfigPath) &&
                !string.IsNullOrWhiteSpace(consent.InstallVersion) && !string.IsNullOrWhiteSpace(consent.ChangeHash));
    }

    private bool TryFindApprovedCandidate(BoxProfile candidate, out string? operationId)
    {
        operationId = null;
        if (!CandidateBelongsToStore(candidate) || !Directory.Exists(historyRoot)) return false;
        foreach (var directory in Directory.EnumerateDirectories(historyRoot))
        {
            ProfileStore.RejectReparseAncestors(directory);
            var id = Path.GetFileName(directory);
            if (Guid.TryParseExact(id, "N", out _) && File.Exists(Path.Combine(operationsRoot, id + ".000000.Started.json")) && LoadCandidate(id) == candidate)
            { operationId = id; return true; }
        }
        return false;
    }

    private BoxProfile? ApprovedCandidateForStagedName(string name, out string? operationId)
    {
        operationId = null;
        if (!Directory.Exists(historyRoot)) return null;
        foreach (var directory in Directory.EnumerateDirectories(historyRoot))
        {
            ProfileStore.RejectReparseAncestors(directory);
            var id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _) || !File.Exists(Path.Combine(operationsRoot, id + ".000000.Started.json"))) continue;
            var candidate = LoadCandidate(id);
            if (candidate is not null && CandidateBelongsToStore(candidate) && name == $".profile.{candidate.OwnerToken}.tmp")
            { operationId = id; return candidate; }
        }
        return null;
    }

    private bool CandidateBelongsToStore(BoxProfile candidate)
    {
        try { store.Validate(candidate); return true; }
        catch (InvalidDataException) { return false; }
    }

    private bool CandidateMatchesStartedApproval(BoxProfile candidate, string operationId)
    {
        if (!CandidateBelongsToStore(candidate) || !Guid.TryParseExact(operationId, "N", out _)) return false;
        var startedPath = Path.Combine(operationsRoot, operationId + ".000000.Started.json");
        ProfileStore.RejectReparseAncestors(startedPath);
        if (!File.Exists(startedPath)) return false;
        var started = DeserializeEvidence<RecoveryEvent>(File.ReadAllBytes(startedPath));
        if (started is null || !EventMatchesTarget(started, startedPath)) return false;
        var configPath = started.ScopeFingerprint.Split('|')[0];
        if (!Path.IsPathFullyQualified(configPath)) return false;
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(store.Root + "|" + configPath + "|" + candidate.UserSid));
        var name = "SideTalk_" + Convert.ToHexString(seed).ToLowerInvariant()[..23];
        var token = Convert.ToHexString(SHA256.HashData(seed)).ToLowerInvariant()[..32];
        return candidate.BoxName == name && candidate.OwnerToken == token;
    }

    private bool IsEventOriginValid(RecoveryEvent entry) =>
        entry.Kind == "Completed" ? IsCompletedOriginValid(entry) : CanRestoreEvent(entry);

    private bool IsCompletedOriginValid(RecoveryEvent entry)
    {
        try
        {
            // The original Completed proves only its historical prefix. Later Failed
            // or Reconciled records belong to the current published chain, which is
            // checked separately before accepting an archived Completed below.
            var prefix = new Dictionary<int, RecoveryEvent>();
            void AddPrefix(RecoveryEvent record, string target)
            {
                if (record.OperationId != entry.OperationId || !EventMatchesTarget(record, target))
                    throw new InvalidDataException("과거 완료 기록의 작업과 원래 위치가 일치하지 않습니다.");
                if (record.Sequence > entry.Sequence) return;
                if (prefix.TryGetValue(record.Sequence, out var existing) && existing != record)
                    throw new InvalidDataException("과거 완료 기록에 충돌하는 순서가 있습니다.");
                prefix[record.Sequence] = record;
            }

            if (Directory.Exists(operationsRoot))
            {
                foreach (var path in Directory.EnumerateFiles(operationsRoot, entry.OperationId + ".*.json"))
                {
                    ProfileStore.RejectReparseAncestors(path);
                    var record = DeserializeEvidence<RecoveryEvent>(File.ReadAllBytes(path))
                        ?? throw new InvalidDataException("복구 이벤트가 비어 있거나 손상되었습니다.");
                    AddPrefix(record, path);
                }
                foreach (var path in Directory.EnumerateFiles(operationsRoot, entry.OperationId + ".*.tmp"))
                {
                    ProfileStore.RejectReparseAncestors(path);
                    var record = DeserializeEvidence<RecoveryEvent>(File.ReadAllBytes(path));
                    var match = ImmutableTemporaryName.Match(Path.GetFileName(path));
                    if (record is not null && match.Success) AddPrefix(record, match.Groups["target"].Value);
                }
            }
            if (Directory.Exists(EvidenceRoot))
                foreach (var directory in Directory.EnumerateDirectories(EvidenceRoot))
                {
                    if (!File.Exists(Path.Combine(directory, "metadata.json"))) continue;
                    var manifest = ReadEvidenceManifest(directory);
                    if (manifest.Kind != SetupRecoveryEvidenceKind.CompleteRecord || manifest.OperationId != entry.OperationId ||
                        !File.Exists(manifest.PreservationPath)) continue;
                    var bytes = File.ReadAllBytes(manifest.PreservationPath);
                    if (bytes.LongLength != manifest.Length || HashBytes(bytes) != manifest.Sha256)
                        throw new InvalidDataException("보존 원본의 바이트가 기록과 다릅니다.");
                    var record = DeserializeEvidence<RecoveryEvent>(bytes)
                        ?? throw new InvalidDataException("보존된 복구 이벤트가 손상되었습니다.");
                    var match = ImmutableTemporaryName.Match(Path.GetFileName(manifest.OriginalPath));
                    if (!match.Success) throw new InvalidDataException("보존 이벤트의 원래 파일 이름이 유효하지 않습니다.");
                    AddPrefix(record, match.Groups["target"].Value);
                }
            AddPrefix(entry, $"{entry.OperationId}.{entry.Sequence:D6}.{entry.Kind}.json");
            ValidateOperation(prefix.Values.ToArray());
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException) { return false; }
    }

    private bool CanRestoreEvent(RecoveryEvent entry)
    {
        if (entry.Kind == "Completed") return false;
        try
        {
            var records = Directory.Exists(operationsRoot)
                ? Directory.EnumerateFiles(operationsRoot, entry.OperationId + ".*.json").Select(path =>
                {
                    ProfileStore.RejectReparseAncestors(path);
                    return JsonSerializer.Deserialize<RecoveryEvent>(File.ReadAllText(path), RecoveryJson.Options)
                        ?? throw new InvalidDataException("복구 이벤트가 비어 있습니다.");
                }).ToList()
                : new List<RecoveryEvent>();
            var same = records.SingleOrDefault(record => record.Sequence == entry.Sequence);
            if (same is not null) return same == entry;
            if (Directory.Exists(operationsRoot))
                foreach (var path in Directory.EnumerateFiles(operationsRoot, entry.OperationId + ".*.tmp"))
                {
                    ProfileStore.RejectReparseAncestors(path);
                    var temporary = DeserializeEvidence<RecoveryEvent>(File.ReadAllBytes(path));
                    var match = ImmutableTemporaryName.Match(Path.GetFileName(path));
                    if (temporary is null || !match.Success || !EventMatchesTarget(temporary, match.Groups["target"].Value) ||
                        temporary.Sequence == entry.Sequence || records.Any(record => record.Sequence == temporary.Sequence)) continue;
                    records.Add(temporary);
                }
            if (Directory.Exists(EvidenceRoot))
                foreach (var directory in Directory.EnumerateDirectories(EvidenceRoot))
                {
                    if (!File.Exists(Path.Combine(directory, "metadata.json"))) continue;
                    var manifest = ReadEvidenceManifest(directory);
                    if (manifest.Kind != SetupRecoveryEvidenceKind.CompleteRecord || manifest.OperationId != entry.OperationId ||
                        !File.Exists(manifest.PreservationPath)) continue;
                    var preserved = DeserializeEvidence<RecoveryEvent>(File.ReadAllBytes(manifest.PreservationPath));
                    if (preserved is not null && preserved.Sequence != entry.Sequence &&
                        !records.Any(record => record.Sequence == preserved.Sequence)) records.Add(preserved);
                }
            records.Add(entry);
            ValidateOperation(records);
            return entry.Kind != "Reconciled";
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException) { return false; }
    }

    private static bool EventMatchesTarget(RecoveryEvent entry, string target) =>
        Path.GetFileName(target) == $"{entry.OperationId}.{entry.Sequence:D6}.{entry.Kind}.json" &&
        Guid.TryParseExact(entry.OperationId, "N", out _) && Guid.TryParseExact(entry.PlanId, "N", out _) &&
        entry.RecordedAtUtc != default && !string.IsNullOrWhiteSpace(entry.ScopeFingerprint);

    private void PreserveEvidence(SetupRecoveryEvidenceItem item)
    {
        ProfileStore.RejectReparseAncestors(item.OriginalPath);
        // DELETE access belongs to this exclusive handle; external path replacement is
        // denied until the exact inspected file has been renamed through that handle.
        using var source = WindowsEvidenceFile.OpenExclusiveSource(item.OriginalPath);
        var bytes = new byte[checked((int)source.Length)];
        source.ReadExactly(bytes);
        if (HashBytes(bytes) != item.Sha256) throw new InvalidDataException("승인 이후 보존 대상 파일이 변경되었습니다.");
        Directory.CreateDirectory(EvidenceRoot);
        ProfileStore.RejectReparseAncestors(EvidenceRoot);
        // Deterministic identity permits recovery after manifest publication but before move.
        var id = EvidenceArchiveId(item.OriginalPath, item.Sha256);
        var directory = Path.Combine(EvidenceRoot, id);
        Directory.CreateDirectory(directory);
        ProfileStore.RejectReparseAncestors(directory);
        var manifest = new EvidenceManifest(1, item.OriginalPath, item.Sha256, item.Length, item.Kind, item.OperationId,
            item.BlocksExecution && (item.Kind is SetupRecoveryEvidenceKind.UnknownSource or SetupRecoveryEvidenceKind.ConflictingRecord),
            item.Detail, Path.Combine(directory, "original.bin"), ReadMetadataStages(directory),
            IsConsentEvidencePath(item.OriginalPath) ? ConsentSequenceFloorForPreservation(item.OriginalPath, bytes) : 0);
        var metadataPath = Path.Combine(directory, "metadata.json");
        if (File.Exists(metadataPath))
        {
            var existingManifest = ReadEvidenceManifest(directory);
            manifest = manifest with { ConsentSequenceFloor = existingManifest.ConsentSequenceFloor };
            if (JsonSerializer.Serialize(existingManifest, RecoveryJson.Options) != JsonSerializer.Serialize(manifest, RecoveryJson.Options))
                throw new InvalidDataException("보존 메타데이터가 원본과 다릅니다.");
        }
        else WriteImmutable(metadataPath, manifest);
        beforePreservationRename?.Invoke(item.OriginalPath, manifest.PreservationPath);
        WindowsEvidenceFile.RenameNoReplace(source.SafeFileHandle, manifest.PreservationPath);
    }

    private bool EvidenceNeedsPublication(EvidenceManifest manifest)
    {
        if (manifest.Unresolved) return false;
        var bytes = File.ReadAllBytes(manifest.PreservationPath);
        var match = ImmutableTemporaryName.Match(Path.GetFileName(manifest.OriginalPath));
        if (manifest.Kind == SetupRecoveryEvidenceKind.CompleteRecord && match.Success)
        {
            var entry = DeserializeEvidence<RecoveryEvent>(bytes);
            return entry is not null && entry.Kind != "Completed" &&
                !File.Exists(Path.Combine(Path.GetDirectoryName(manifest.OriginalPath)!, match.Groups["target"].Value));
        }
        if (manifest.Kind == SetupRecoveryEvidenceKind.ApprovedProfileCandidate)
            return !File.Exists(match.Success ? Path.Combine(Path.GetDirectoryName(manifest.OriginalPath)!, match.Groups["target"].Value) : store.ProfilePath);
        return false;
    }

    private void ResumePublishedEvidence(EvidenceManifest manifest, string preserved, bool inspectOnly)
    {
        if (manifest.Unresolved || manifest.Kind is SetupRecoveryEvidenceKind.ConsentRecord or SetupRecoveryEvidenceKind.IncompleteRecord) return;
        var bytes = File.ReadAllBytes(preserved);
        string? target = null;
        var match = ImmutableTemporaryName.Match(Path.GetFileName(manifest.OriginalPath));
        if (manifest.Kind == SetupRecoveryEvidenceKind.CompleteRecord && match.Success)
        {
            var entry = DeserializeEvidence<RecoveryEvent>(bytes);
            var eventTarget = Path.Combine(Path.GetDirectoryName(manifest.OriginalPath)!, match.Groups["target"].Value);
            if (entry is null || entry.OperationId != manifest.OperationId || !EventMatchesTarget(entry, eventTarget) || !IsEventOriginValid(entry))
                throw new InvalidDataException("보존 이벤트의 현재 체인과 원래 위치를 확인할 수 없습니다.");
            if (entry.Kind == "Completed")
            {
                // Validate the actual publication order and every reconciliation
                // reference. The archived prefix never supplies completion authority.
                _ = ReadInterruptedOperation();
                return;
            }
            target = Path.Combine(Path.GetDirectoryName(manifest.OriginalPath)!, match.Groups["target"].Value);
        }
        else if (manifest.Kind == SetupRecoveryEvidenceKind.ApprovedProfileCandidate)
        {
            var candidate = DeserializeEvidence<BoxProfile>(bytes) ??
                ApprovedCandidateForStagedName(Path.GetFileName(manifest.OriginalPath), out _) ??
                throw new InvalidDataException("보존된 후보가 손상되었습니다.");
            if (!CandidateBelongsToStore(candidate)) throw new InvalidDataException("보존된 후보의 소유 범위가 다릅니다.");
            if (manifest.OperationId is null || !CandidateMatchesStartedApproval(candidate, manifest.OperationId))
                throw new InvalidDataException("보존 후보의 원래 시작/승인 범위가 일치하지 않습니다.");
            target = match.Success ? Path.Combine(Path.GetDirectoryName(manifest.OriginalPath)!, match.Groups["target"].Value) : store.ProfilePath;
            if (target == store.ProfilePath)
            {
                if (LoadCandidate(manifest.OperationId) != candidate)
                    throw new InvalidDataException("보존 후보를 승인한 원래 작업을 확인할 수 없습니다.");
                var current = store.Load();
                if (current is not null && current != candidate) throw new InvalidDataException("승인 후보와 기존 프로필이 충돌합니다.");
                if (!inspectOnly && current is null) store.CreateCandidate(candidate);
                return;
            }
        }
        if (target is null) return;
        ProfileStore.RejectReparseAncestors(target);
        if (File.Exists(target))
        {
            if (!File.ReadAllBytes(target).SequenceEqual(bytes)) throw new InvalidDataException("보존된 후보와 게시된 파일이 충돌합니다.");
            return;
        }
        if (inspectOnly) return;
        if (manifest.Kind == SetupRecoveryEvidenceKind.CompleteRecord)
            WriteImmutable(target, DeserializeEvidence<RecoveryEvent>(bytes)!);
        else
            WriteImmutable(target, DeserializeEvidence<BoxProfile>(bytes)!);
    }

    private EvidenceManifest ReadEvidenceManifest(string directory)
    {
        var metadata = Path.Combine(directory, "metadata.json");
        ProfileStore.RejectReparseAncestors(metadata);
        var manifest = JsonSerializer.Deserialize<EvidenceManifest>(File.ReadAllBytes(metadata), RecoveryJson.Options)
            ?? throw new InvalidDataException("보존 메타데이터가 비어 있습니다.");
        if (manifest.Schema != 1 || !Path.IsPathFullyQualified(manifest.OriginalPath) ||
            !string.Equals(Path.GetFullPath(manifest.OriginalPath), manifest.OriginalPath, StringComparison.OrdinalIgnoreCase) ||
            !manifest.OriginalPath.StartsWith(store.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            manifest.OriginalPath.StartsWith(EvidenceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !RecognizedEvidenceSource(manifest) || Path.GetFileName(directory) != EvidenceArchiveId(manifest.OriginalPath, manifest.Sha256) ||
            manifest.PreservationPath != Path.Combine(directory, "original.bin") || manifest.Length < 0 ||
            !Regex.IsMatch(manifest.Sha256, "\\A[A-F0-9]{64}\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("보존 메타데이터의 위치 또는 해시가 유효하지 않습니다.");
        if ((manifest.Kind is SetupRecoveryEvidenceKind.CompleteRecord or SetupRecoveryEvidenceKind.ApprovedProfileCandidate) &&
            !Guid.TryParseExact(manifest.OperationId, "N", out _))
            throw new InvalidDataException("보존된 승인 근거의 작업 식별자가 유효하지 않습니다.");
        var consentSource = IsConsentEvidencePath(manifest.OriginalPath);
        if (!Enum.IsDefined(manifest.Kind) || manifest.Unresolved !=
            ((manifest.Kind is SetupRecoveryEvidenceKind.UnknownSource or SetupRecoveryEvidenceKind.ConflictingRecord) && !consentSource) ||
            manifest.ConsentSequenceFloor < 0 || !consentSource && manifest.ConsentSequenceFloor != 0)
            throw new InvalidDataException("보존 기록의 차단 역할이 원본 분류와 다릅니다.");
        ProfileStore.RejectReparseAncestors(manifest.OriginalPath);
        var stages = manifest.MetadataStages ?? throw new InvalidDataException("보존 메타데이터 저장 흔적 목록이 없습니다.");
        if (!stages.SequenceEqual(ReadMetadataStages(directory)))
            throw new InvalidDataException("중단된 보존 메타데이터가 기록된 바이트와 다릅니다.");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            ProfileStore.RejectReparseAncestors(path);
            if (path != metadata && path != manifest.PreservationPath && !stages.Any(stage => path == Path.Combine(directory, stage.Name)))
                throw new InvalidDataException("보존 폴더에 기록되지 않은 항목이 있습니다.");
        }
        return manifest;
    }

    private bool RecognizedEvidenceSource(EvidenceManifest manifest)
    {
        var parent = Path.GetDirectoryName(manifest.OriginalPath)!;
        var name = Path.GetFileName(manifest.OriginalPath);
        var temporary = ImmutableTemporaryName.Match(name);
        if (manifest.Kind == SetupRecoveryEvidenceKind.ApprovedProfileCandidate)
            return (parent == store.Root && Regex.IsMatch(name, @"\A\.profile\.[a-f0-9]{32}\.tmp\z", RegexOptions.CultureInvariant)) ||
                (parent == Path.Combine(historyRoot, manifest.OperationId ?? string.Empty) && temporary.Success && temporary.Groups["target"].Value == "candidate.profile.json");
        if (manifest.Kind == SetupRecoveryEvidenceKind.CompleteRecord)
            return parent == operationsRoot && temporary.Success;
        return (parent == store.Root && name.StartsWith(".profile.", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) ||
            (manifest.OriginalPath.StartsWith(recoveryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                (Path.GetExtension(name).Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    (parent == operationsRoot || parent == consentRoot) && Path.GetExtension(name).Equals(".json", StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<MetadataStage> ReadMetadataStages(string directory)
    {
        var stages = new List<MetadataStage>();
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            ProfileStore.RejectReparseAncestors(path);
            if (Path.GetFileName(path) is "metadata.json" or "original.bin") continue;
            if (!File.Exists(path) || !Regex.IsMatch(Path.GetFileName(path), @"\Ametadata\.json\.[a-f0-9]{32}\.tmp\z", RegexOptions.CultureInvariant))
                throw new InvalidDataException("중단된 보존 폴더에 확인할 수 없는 항목이 있습니다: " + path);
            var bytes = File.ReadAllBytes(path);
            stages.Add(new(Path.GetFileName(path), HashBytes(bytes), bytes.LongLength));
        }
        return stages.OrderBy(stage => stage.Name, StringComparer.Ordinal).ToArray();
    }

    private bool IsConsentEvidencePath(string path)
    {
        var temporary = ImmutableTemporaryName.Match(Path.GetFileName(path));
        return Path.GetDirectoryName(path) == consentRoot &&
            Guid.TryParseExact(Path.GetFileNameWithoutExtension(temporary.Success ? temporary.Groups["target"].Value : path), "N", out _);
    }

    private long ReadPreservedConsentSequenceFloor()
    {
        if (!Directory.Exists(EvidenceRoot)) return 0;
        long floor = 0;
        foreach (var directory in Directory.EnumerateDirectories(EvidenceRoot))
        {
            if (!File.Exists(Path.Combine(directory, "metadata.json"))) continue;
            var manifest = ReadEvidenceManifest(directory);
            if (IsConsentEvidencePath(manifest.OriginalPath)) floor = Math.Max(floor, manifest.ConsentSequenceFloor);
        }
        return floor;
    }

    private long ConsentSequenceFloorForPreservation(string originalPath, byte[] bytes)
    {
        var floor = ReadPreservedConsentSequenceFloor();
        if (Directory.Exists(consentRoot))
            foreach (var path in Directory.EnumerateFiles(consentRoot, "*.json"))
            {
                ProfileStore.RejectReparseAncestors(path);
                if (path == originalPath) continue;
                var consent = DeserializeEvidence<RecoveryConsent>(File.ReadAllBytes(path));
                if (consent is { Sequence: > 0 }) floor = Math.Max(floor, consent.Sequence);
            }
        var staged = DeserializeEvidence<RecoveryConsent>(bytes);
        if (staged is { Sequence: > 0 }) floor = Math.Max(floor, staged.Sequence);
        return floor;
    }

    private static IEnumerable<string> EnumerateSafeFiles(string directory)
    {
        ProfileStore.RejectReparseAncestors(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            ProfileStore.RejectReparseAncestors(entry);
            if (Directory.Exists(entry))
                foreach (var child in EnumerateSafeFiles(entry)) yield return child;
            else yield return entry;
        }
    }

    private static T? DeserializeEvidence<T>(byte[] bytes) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(bytes, RecoveryJson.Options); }
        catch (JsonException) { return null; }
    }
    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string EvidenceArchiveId(string originalPath, string sha256) =>
        HashBytes(Encoding.UTF8.GetBytes(originalPath + "|" + sha256)).ToLowerInvariant()[..32];
    private static string EvidenceSnapshot(IReadOnlyList<SetupRecoveryEvidenceItem> items) =>
        HashBytes(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(items, RecoveryJson.Options)));
    private sealed record EvidenceManifest(int Schema, string OriginalPath, string Sha256, long Length,
        SetupRecoveryEvidenceKind Kind, string? OperationId, bool Unresolved, string Detail, string PreservationPath,
        IReadOnlyList<MetadataStage> MetadataStages, long ConsentSequenceFloor);
    private sealed record MetadataStage(string Name, string Sha256, long Length);
}
