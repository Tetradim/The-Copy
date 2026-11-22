using System.Text.Json;

namespace TetherBridge.Desktop.FullTunnel;

// A backend must compare the current setting to Before/After before writing it.
// This transaction does not itself alter Windows networking.
public sealed record NetworkChange(string Kind, string Key, string? Before, string? After);
public enum NetworkRecoveryResult { Restored, AlreadyRestored, Conflict }
public interface ITransactionalNetworkBackend
{
    Task ApplyAsync(NetworkChange change, CancellationToken token);
    Task<NetworkRecoveryResult> RecoverAsync(NetworkChange change, CancellationToken token);
}

public sealed class NetworkTransaction(string journalPath, ITransactionalNetworkBackend backend)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private sealed record Journal(int Version, List<NetworkChange> Changes);

    public async Task ApplyAsync(IReadOnlyList<NetworkChange> changes, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0 || changes.Count > 128 || changes.Any(c => c is null || string.IsNullOrWhiteSpace(c.Kind) || string.IsNullOrWhiteSpace(c.Key)) ||
            changes.Select(c => (c.Kind, c.Key)).Distinct().Count() != changes.Count)
            throw new ArgumentException("Network changes must have distinct, valid identities.");
        await gate.WaitAsync(token);
        try
        {
            using var lease = AcquireLease();
            if (File.Exists(journalPath)) throw new InvalidOperationException("Unresolved network recovery journal exists.");
            var journal = new Journal(1, new List<NetworkChange>());
            Save(journal);
            try
            {
                foreach (var change in changes)
                {
                    token.ThrowIfCancellationRequested();
                    journal.Changes.Add(change);
                    Save(journal); // durable intent before any possibly partial mutation
                    await backend.ApplyAsync(change, token);
                }
            }
            catch (Exception failure)
            {
                bool recovered;
                try { recovered = await RecoverCoreAsync(CancellationToken.None); }
                catch (Exception recoveryFailure)
                {
                    throw new NetworkRecoveryPendingException("Network activation failed and recovery could not be completed.", new AggregateException(failure, recoveryFailure));
                }
                if (!recovered) throw new NetworkRecoveryPendingException("Network activation failed and recovery is blocked by a conflicting change.", failure);
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<bool> RecoverAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { using var lease = AcquireLease(); return await RecoverCoreAsync(token); }
        finally { gate.Release(); }
    }

    private async Task<bool> RecoverCoreAsync(CancellationToken token)
    {
        if (!File.Exists(journalPath)) return true;
        Journal journal;
        try { journal = JsonSerializer.Deserialize<Journal>(await File.ReadAllTextAsync(journalPath, token)) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new InvalidDataException("Network recovery journal is corrupt; no network changes were made.", ex); }
        if (journal.Version != 1 || journal.Changes is null || journal.Changes.Count > 128 ||
            journal.Changes.Any(c => c is null || string.IsNullOrWhiteSpace(c.Kind) || string.IsNullOrWhiteSpace(c.Key)) ||
            journal.Changes.Select(c => (c.Kind, c.Key)).Distinct().Count() != journal.Changes.Count)
            throw new InvalidDataException("Network recovery journal is invalid; no network changes were made.");
        for (var i = journal.Changes.Count - 1; i >= 0; i--)
        {
            token.ThrowIfCancellationRequested();
            var result = await backend.RecoverAsync(journal.Changes[i], token);
            if (result == NetworkRecoveryResult.Conflict) return false;
            if (result != NetworkRecoveryResult.Restored && result != NetworkRecoveryResult.AlreadyRestored)
                throw new InvalidDataException("Unrecognized network recovery result.");
            journal.Changes.RemoveAt(i);
            Save(journal);
        }
        if (journal.Changes.Count != 0) return false;
        File.Delete(journalPath);
        return true;
    }

    private FileStream AcquireLease()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(journalPath))!;
        Directory.CreateDirectory(directory);
        return new FileStream(journalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void Save(Journal journal)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(journalPath))!;
        Directory.CreateDirectory(directory);
        var temporary = journalPath + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { file.Write(bytes); file.Flush(flushToDisk: true); }
        File.Move(temporary, journalPath, overwrite: true);
    }
}
