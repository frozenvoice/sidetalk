using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sidetalk.Core;

/// <summary>
/// The durable description of one Sandboxie command.  <see cref="Process"/> is null
/// for an intent that was persisted before Process.Start returned or before its PID
/// could be identified.
/// </summary>
public sealed record PendingCommandRecord(
    string OperationId,
    SidetalkCommandAction Action,
    BoxProfile Profile,
    string StartExe,
    DateTimeOffset RequestedAtUtc,
    CommandProcessIdentity? Process)
{
    public CommandProcessIdentity? Identity => Process;
}

/// <summary>
/// Append-only operation journal for the SIDETALK profile root.  The journal has one
/// lock for all readers and writers.  Every final record is published by a flushed,
/// same-directory no-overwrite move; failed temporary files are intentionally retained.
/// </summary>
public sealed class CommandJournal
{
    private const int Schema = 1;
    private const long MaxEntryBytes = 64 * 1024;
    private static readonly Regex FinalName = new(@"\A(?<operation>[a-f0-9]{32})\.(?<phase>intent|started|completed)\.json\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex OperationIdPattern = new(@"\A[a-f0-9]{32}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string root;
    private readonly string journalDirectory;
    private Lease? lease;

    public CommandJournal(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("명령 기록의 프로필 루트는 절대 경로여야 합니다.", nameof(root));
        this.root = Path.GetFullPath(root);
        journalDirectory = Path.Combine(this.root, "command-journal");
        ProfileStore.RejectReparseAncestors(this.root);
    }

    public string Root => root;
    public string JournalDirectory => journalDirectory;
    public string LockPath => Path.Combine(journalDirectory, "command.lock");

    /// <summary>Creates/opens the journal lock and holds it until the returned lease is disposed.</summary>
    public IDisposable AcquireLease()
    {
        if (lease is not null)
            throw new InvalidOperationException("이 CommandJournal 인스턴스가 이미 명령 기록 잠금을 보유하고 있습니다.");

        EnsureJournalDirectory();
        FileStream stream;
        try
        {
            stream = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("SIDETALK 명령 기록 잠금을 얻지 못했습니다. 다른 실행 중인 SIDETALK가 완료될 때까지 기다리세요.", error);
        }

        lease = new Lease(this, stream);
        return lease;
    }

    /// <summary>
    /// Opens an existing journal lock. Writable inspection may recover a missing lock
    /// after interrupted initialization; read-only callers never create files. Inspection
    /// does not create an absent journal directory or change any command record.
    /// </summary>
    public IDisposable? TryAcquireInspectionLease(bool recoverMissingLock = false)
    {
        ProfileStore.RejectReparseAncestors(root);
        if (!Directory.Exists(journalDirectory))
            return null;
        ProfileStore.RejectReparseAncestors(journalDirectory);
        ProfileStore.RejectReparseAncestors(LockPath);
        if (!File.Exists(LockPath))
        {
            // Reuse the exclusive no-truncate acquisition path. Creating the lock does
            // not complete a command: callers must still validate and reconcile records.
            if (recoverMissingLock) return AcquireLease();
            throw new InvalidDataException("명령 기록 폴더에 잠금 파일이 없어 상태를 확정할 수 없습니다.");
        }

        FileStream stream;
        try
        {
            stream = new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("다른 SIDETALK 명령이 실행 중이어서 상태를 확인할 수 없습니다.", error);
        }

        if (lease is not null)
        {
            stream.Dispose();
            throw new InvalidOperationException("이 CommandJournal 인스턴스가 이미 명령 기록 잠금을 보유하고 있습니다.");
        }
        lease = new Lease(this, stream);
        return lease;
    }

    /// <summary>
    /// Returns the one unfinished operation, or null when every recorded operation has
    /// a completion record.  Any malformed or orphaned final record fails closed.
    /// </summary>
    public PendingCommandRecord? ReadPending(BoxProfile profile, string startExe)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var expectedStart = ValidateProfileAndStart(profile, startExe);
        if (!Directory.Exists(journalDirectory))
            return null;
        RequireLease();

        var states = ReadStates();
        var pending = new List<PendingCommandRecord>();
        foreach (var state in states.Values)
        {
            var record = state.ToPending();
            if (!SameProfile(record.Profile, profile) || !SamePath(record.StartExe, expectedStart))
                throw new InvalidDataException("명령 기록의 전용 환경 또는 Start.exe 경로가 현재 설정과 다릅니다.");
            if (!state.IsCompleted)
                pending.Add(record);
        }

        if (pending.Count > 1)
            throw new InvalidDataException("완료되지 않은 SIDETALK 명령 기록이 여러 개 있어 중복 실행을 차단했습니다.");
        return pending.Count == 0 ? null : pending[0];
    }

    /// <summary>Publishes an immutable intent before the caller starts Start.exe.</summary>
    public PendingCommandRecord Prepare(BoxProfile profile, SidetalkCommandAction action, string startExe)
    {
        RequireLease();
        var expectedStart = ValidateProfileAndStart(profile, startExe);
        if (ReadPending(profile, expectedStart) is not null)
            throw new InvalidOperationException("완료되지 않은 Sandboxie 명령이 있어 새 명령을 실행할 수 없습니다.");
        if (!Enum.IsDefined(action))
            throw new ArgumentOutOfRangeException(nameof(action));

        var record = new PendingCommandRecord(Guid.NewGuid().ToString("N"), action, profile, expectedStart, DateTimeOffset.UtcNow, null);
        Publish(record, "intent");
        return record;
    }

    /// <summary>Publishes the process identity after Process.Start returned.</summary>
    public void RecordStarted(PendingCommandRecord record, CommandProcessIdentity identity)
    {
        RequireLease();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(identity);
        var expectedStart = ValidateProfileAndStart(record.Profile, record.StartExe);
        ValidateIdentity(identity);
        if (!SamePath(identity.ImagePath, expectedStart))
            throw new InvalidDataException("기록할 명령 프로세스가 전용 Start.exe와 다릅니다.");

        var state = ReadState(record, expectedStart);
        if (state.Intent is null || state.Started is not null || state.Completed is not null)
            throw new InvalidDataException("명령 기록의 시작 상태가 예상과 다릅니다.");
        if (record.Process is not null || !SameRecordMetadata(record, state.Intent))
            throw new InvalidDataException("명령 기록이 준비된 의도와 다릅니다.");

        Publish(record with { StartExe = expectedStart, Process = identity }, "started");
    }

    /// <summary>
    /// Publishes completion only after the caller has independently confirmed command
    /// exit and a fresh validated box state.  Intent-only completion is allowed for the
    /// conservative unidentified-PID recovery path; it carries no fabricated exit code.
    /// </summary>
    public void Complete(PendingCommandRecord record)
    {
        RequireLease();
        ArgumentNullException.ThrowIfNull(record);
        var expectedStart = ValidateProfileAndStart(record.Profile, record.StartExe);
        var state = ReadState(record, expectedStart);
        if (state.Intent is null)
            throw new InvalidDataException("완료할 명령 기록의 상태가 예상과 다릅니다.");
        if (state.Completed is not null)
        {
            if (!SameRecordMetadata(record, state.Intent) ||
                (record.Process is not null && !SameIdentity(record.Process, state.Completed.Process)))
                throw new InvalidDataException("이미 완료된 명령 기록의 식별자가 다릅니다.");
            return;
        }
        if (!SameRecordMetadata(record, state.Intent))
            throw new InvalidDataException("완료할 명령 기록이 준비된 의도와 다릅니다.");
        if (record.Process is not null && state.Started?.Process is not null && !SameIdentity(record.Process, state.Started.Process))
            throw new InvalidDataException("완료할 명령 프로세스 식별자가 기록과 다릅니다.");

        var completion = state.Started is null
            ? state.Intent
            : state.Started;
        Publish(new PendingCommandRecord(completion.OperationId, completion.Action, completion.Profile, completion.StartExe, completion.RequestedAtUtc, completion.Process), "completed");
    }

    public bool IsCompleted(PendingCommandRecord record)
    {
        RequireLease();
        ArgumentNullException.ThrowIfNull(record);
        var expectedStart = ValidateProfileAndStart(record.Profile, record.StartExe);
        var state = ReadState(record, expectedStart);
        if (state.Intent is null || !SameRecordMetadata(record, state.Intent))
            throw new InvalidDataException("명령 기록의 식별자가 현재 의도와 다릅니다.");
        if (state.Completed is null)
            return false;
        if (record.Process is not null && !SameIdentity(record.Process, state.Completed.Process))
            throw new InvalidDataException("완료된 명령 프로세스 식별자가 다릅니다.");
        return true;
    }

    private void Publish(PendingCommandRecord record, string phase)
    {
        var operation = ValidateOperationId(record.OperationId);
        if (phase is not ("intent" or "started" or "completed"))
            throw new ArgumentOutOfRangeException(nameof(phase));
        var entry = new JournalEntry(Schema, phase, operation, record.Action, record.Profile, record.StartExe,
            record.RequestedAtUtc, record.Process);
        var temporary = Path.Combine(journalDirectory, $".{operation}.{phase}.{Guid.NewGuid():N}.tmp");
        var final = Path.Combine(journalDirectory, $"{operation}.{phase}.json");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, entry, Json);
                stream.Flush(flushToDisk: true);
            }
            // Do not remove or overwrite a previous immutable record.  If this move fails,
            // the temporary file remains as a diagnostic artifact for the next run.
            File.Move(temporary, final, overwrite: false);
        }
        catch
        {
            // Deliberately preserve a failed temporary file.  No cleanup or data deletion
            // is performed by SIDETALK's command journal.
            throw;
        }
    }

    private Dictionary<string, OperationState> ReadStates()
    {
        var states = new Dictionary<string, OperationState>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(journalDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            ProfileStore.RejectReparseAncestors(path);
            var match = FinalName.Match(Path.GetFileName(path));
            if (!match.Success)
                throw new InvalidDataException("명령 기록 폴더에 인식할 수 없는 JSON 파일이 있습니다.");
            var operationId = match.Groups["operation"].Value;
            var phase = match.Groups["phase"].Value;
            var entry = ReadEntry(path);
            ValidateEntry(entry, operationId, phase);

            if (!states.TryGetValue(operationId, out var state))
                state = new OperationState(operationId);
            state.Set(phase, entry);
            states[operationId] = state;
        }

        foreach (var state in states.Values)
            state.Validate();
        return states;
    }

    private OperationState ReadState(PendingCommandRecord record, string expectedStart)
    {
        var states = ReadStates();
        if (!states.TryGetValue(record.OperationId, out var state))
            throw new InvalidDataException("명령 의도 기록이 없습니다.");
        foreach (var candidate in new[] { state.Intent, state.Started, state.Completed }.Where(item => item is not null))
        {
            if (!SameProfile(candidate!.Profile, record.Profile) || !SamePath(candidate.StartExe, expectedStart))
                throw new InvalidDataException("명령 기록의 전용 환경 또는 Start.exe 경로가 현재 설정과 다릅니다.");
        }
        return state;
    }

    private JournalEntry ReadEntry(string path)
    {
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaxEntryBytes)
            throw new InvalidDataException("명령 기록 파일 크기가 유효하지 않습니다.");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > MaxEntryBytes)
                throw new InvalidDataException("명령 기록 파일 크기가 유효하지 않습니다.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.Position != stream.Length)
                throw new InvalidDataException("명령 기록을 끝까지 읽지 못했습니다.");
            return JsonSerializer.Deserialize<JournalEntry>(bytes, Json) ?? throw new InvalidDataException("명령 기록이 비어 있습니다.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("명령 기록 JSON이 손상되었습니다.", error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new InvalidDataException("명령 기록을 읽을 수 없습니다.", error);
        }
    }

    private void ValidateEntry(JournalEntry entry, string filenameOperation, string filenamePhase)
    {
        if (entry.Schema != Schema || string.IsNullOrEmpty(entry.Phase) || string.IsNullOrEmpty(entry.OperationId) ||
            string.IsNullOrEmpty(entry.StartExe) || !OperationIdPattern.IsMatch(entry.OperationId) ||
            !entry.OperationId.Equals(filenameOperation, StringComparison.Ordinal) ||
            !entry.Phase.Equals(filenamePhase, StringComparison.Ordinal) ||
            entry.Profile is null || !Enum.IsDefined(entry.Action) ||
            entry.RequestedAtUtc.Offset != TimeSpan.Zero || entry.RequestedAtUtc.UtcTicks <= DateTime.MinValue.Ticks)
            throw new InvalidDataException("명령 기록의 식별자 또는 버전이 유효하지 않습니다.");

        var expectedStart = ValidateProfileAndStart(entry.Profile, entry.StartExe);
        if (!SamePath(entry.StartExe, expectedStart))
            throw new InvalidDataException("명령 기록의 Start.exe 경로 형식이 유효하지 않습니다.");
        if (entry.Phase == "intent" && entry.Process is not null)
            throw new InvalidDataException("intent 기록에는 명령 프로세스 식별자를 넣을 수 없습니다.");
        if (entry.Phase == "started" && entry.Process is null)
            throw new InvalidDataException("started 기록에는 명령 프로세스 식별자가 필요합니다.");
        if (entry.Process is not null)
            ValidateIdentity(entry.Process);
        if (entry.Process is not null && !SamePath(entry.Process.ImagePath, expectedStart))
            throw new InvalidDataException("명령 기록의 프로세스 이미지가 Start.exe와 다릅니다.");
    }

    private string ValidateProfileAndStart(BoxProfile profile, string startExe)
    {
        ArgumentNullException.ThrowIfNull(profile);
        new ProfileStore(root).Validate(profile);
        return ValidateStartExe(startExe);
    }

    private string ValidateStartExe(string startExe)
    {
        if (string.IsNullOrWhiteSpace(startExe) || !Path.IsPathFullyQualified(startExe) ||
            !Path.GetFileName(startExe).Equals("Start.exe", StringComparison.OrdinalIgnoreCase) ||
            startExe.Any(char.IsControl))
            throw new InvalidDataException("Sandboxie Start.exe의 절대 경로가 필요합니다.");
        var full = Path.GetFullPath(startExe);
        ProfileStore.RejectReparseAncestors(full);
        return full;
    }

    private void ValidateIdentity(CommandProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.ProcessId <= 0 || identity.CreationTimeUtcTicks <= DateTime.MinValue.Ticks ||
            identity.CreationTimeUtcTicks > DateTime.MaxValue.Ticks ||
            string.IsNullOrWhiteSpace(identity.ImagePath) || !Path.IsPathFullyQualified(identity.ImagePath) ||
            identity.ImagePath.Any(char.IsControl))
            throw new InvalidDataException("명령 프로세스 식별자가 유효하지 않습니다.");
    }

    private static string ValidateOperationId(string operationId)
    {
        if (!OperationIdPattern.IsMatch(operationId))
            throw new InvalidDataException("명령 기록 식별자가 유효하지 않습니다.");
        return operationId;
    }

    private static bool SameRecordMetadata(PendingCommandRecord first, JournalEntry second) =>
        first.OperationId.Equals(second.OperationId, StringComparison.Ordinal) && first.Action == second.Action &&
        SameProfile(first.Profile, second.Profile) && SamePath(first.StartExe, second.StartExe) &&
        first.RequestedAtUtc == second.RequestedAtUtc;

    private static bool SameProfile(BoxProfile first, BoxProfile second) =>
        first.Schema == second.Schema && first.BoxName.Equals(second.BoxName, StringComparison.Ordinal) &&
        first.OwnerToken.Equals(second.OwnerToken, StringComparison.Ordinal) && first.UserSid.Equals(second.UserSid, StringComparison.Ordinal) &&
        first.StoragePath.Equals(second.StoragePath, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string first, string second) =>
        Path.GetFullPath(first).TrimEnd('\\').Equals(Path.GetFullPath(second).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private void EnsureJournalDirectory()
    {
        ProfileStore.RejectReparseAncestors(root);
        Directory.CreateDirectory(root);
        ProfileStore.RejectReparseAncestors(root);
        Directory.CreateDirectory(journalDirectory);
        ProfileStore.RejectReparseAncestors(journalDirectory);
        ProfileStore.RejectReparseAncestors(LockPath);
    }

    private void RequireLease()
    {
        if (lease is null)
            throw new InvalidOperationException("명령 기록을 읽거나 변경하기 전에 CommandJournal 잠금을 얻어야 합니다.");
    }

    private sealed class Lease(CommandJournal owner, FileStream stream) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            stream.Dispose();
            if (ReferenceEquals(owner.lease, this)) owner.lease = null;
        }
    }

    private sealed class OperationState(string operationId)
    {
        public string OperationId { get; } = operationId;
        public JournalEntry? Intent { get; private set; }
        public JournalEntry? Started { get; private set; }
        public JournalEntry? Completed { get; private set; }
        public bool IsCompleted => Completed is not null;

        public void Set(string phase, JournalEntry entry)
        {
            switch (phase)
            {
                case "intent" when Intent is null: Intent = entry; break;
                case "started" when Started is null: Started = entry; break;
                case "completed" when Completed is null: Completed = entry; break;
                default: throw new InvalidDataException("명령 기록에 중복된 상태 파일이 있습니다.");
            }
        }

        public void Validate()
        {
            if (Intent is null)
                throw new InvalidDataException("명령 기록에 intent가 없어 상태를 확정할 수 없습니다.");
            if (Started is not null && !SameMetadata(Intent, Started))
                throw new InvalidDataException("intent와 started 명령 기록이 일치하지 않습니다.");
            if (Completed is not null)
            {
                if (Started is not null && (!SameMetadata(Started, Completed) || !SameIdentity(Started.Process, Completed.Process)))
                    throw new InvalidDataException("started와 completed 명령 기록이 일치하지 않습니다.");
                if (Started is null && (Completed.Process is not null || !SameMetadata(Intent, Completed)))
                    throw new InvalidDataException("PID 없는 명령의 completed 기록이 intent와 일치하지 않습니다.");
            }
        }

        public PendingCommandRecord ToPending() =>
            new(OperationId, Intent!.Action, Intent.Profile, Intent.StartExe, Intent.RequestedAtUtc, Started?.Process);

        private static bool SameMetadata(JournalEntry first, JournalEntry second) =>
            first.Schema == second.Schema && first.Phase != second.Phase && first.OperationId.Equals(second.OperationId, StringComparison.Ordinal) &&
            first.Action == second.Action && SameProfile(first.Profile, second.Profile) && SamePath(first.StartExe, second.StartExe) &&
            first.RequestedAtUtc == second.RequestedAtUtc;
    }

    private sealed record JournalEntry(
        int Schema,
        string Phase,
        string OperationId,
        SidetalkCommandAction Action,
        BoxProfile Profile,
        string StartExe,
        DateTimeOffset RequestedAtUtc,
        CommandProcessIdentity? Process);

    private static bool SameIdentity(CommandProcessIdentity? first, CommandProcessIdentity? second) =>
        first is null && second is null || first is not null && second is not null && first.ProcessId == second.ProcessId &&
        first.CreationTimeUtcTicks == second.CreationTimeUtcTicks && SamePath(first.ImagePath, second.ImagePath);
}
