using PCAnalyzer.Core;
using PCAnalyzer.Network;
using PCAnalyzer.Optimization;

namespace PCAnalyzer.Tests;

public sealed class OptimizationNetworkTests
{
    [Fact]
    public async Task ChangeSinceUserApprovedPreviewNeverWritesOrRebasesBackupSilently()
    {
        var history = new FakeHistory(); var engine = new ReversibleChangeEngine(history); var operation = new FakeOperation();
        var preview = await engine.PreviewAsync(operation, "new"); operation.Current = "third";
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ApplyAsync(operation, "new", approvedOriginal: preview.Before));
        Assert.Equal(0, operation.Writes); Assert.Empty(history.Saves); Assert.Equal("third", operation.Current);
    }
    [Fact]
    public async Task BackupCommitFailurePreventsEveryWrite()
    {
        var store = new FakeHistory { BeforeSave = _ => throw new IOException("disk full") };
        var operation = new FakeOperation();
        await Assert.ThrowsAsync<IOException>(() => new ReversibleChangeEngine(store).ApplyAsync(operation, "new"));
        Assert.Equal(0, operation.Writes);
        Assert.Equal("old", operation.Current);
    }

    [Fact]
    public async Task ApplyCommitsOriginalBeforeWriteAndVerifiesAfter()
    {
        var store = new FakeHistory();
        var operation = new FakeOperation
        {
            OnWrite = _ =>
            {
                var prepared = Assert.Single(store.Saves);
                Assert.Equal(ChangeState.Prepared, prepared.State);
                Assert.Equal("old", prepared.Before);
                Assert.Equal("new", prepared.After);
            }
        };
        var change = await new ReversibleChangeEngine(store).ApplyAsync(operation, "new");
        Assert.Equal(ChangeState.Applied, change.State);
        Assert.Equal("new", operation.Current);
        Assert.Equal(ChangeState.Applied, store.Saves.Last().State);
        Assert.True(operation.Reads >= 3);
    }

    [Fact]
    public async Task ReadbackMismatchIsDurablyFailedWithOriginalBackup()
    {
        var store = new FakeHistory();
        var operation = new FakeOperation { IgnoreWrite = true };
        var change = await new ReversibleChangeEngine(store).ApplyAsync(operation, "new");
        Assert.Equal(ChangeState.Failed, change.State);
        Assert.Equal("old", change.Before);
        Assert.Contains("diverge", change.Error!);
        Assert.Equal(change, await store.LoadChangeAsync(change.Id));
    }

    [Fact]
    public async Task InvalidDestinationCannotCreateBackupOrWrite()
    {
        var store = new FakeHistory();
        var operation = new FakeOperation();
        await Assert.ThrowsAsync<ArgumentException>(() => new ReversibleChangeEngine(store).ApplyAsync(operation, "invalid"));
        Assert.Empty(store.Saves);
        Assert.Equal(0, operation.Writes);
    }

    [Fact]
    public async Task CancellationAfterCompletedWriteStillVerifiesAndCommitsResult()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeHistory();
        var operation = new FakeOperation { OnWrite = _ => cancellation.Cancel() };
        var change = await new ReversibleChangeEngine(store).ApplyAsync(operation, "new", cancellation.Token);
        Assert.Equal(ChangeState.Applied, change.State);
        Assert.Equal("new", operation.Current);
        Assert.Equal(ChangeState.Applied, store.Saves.Last().State);
    }

    [Fact]
    public async Task PartialWriteExceptionReadsActualStateAndCanBeRestored()
    {
        var store = new FakeHistory();
        var operation = new FakeOperation { AfterWrite = _ => throw new OperationCanceledException() };
        var engine = new ReversibleChangeEngine(store);
        var change = await engine.ApplyAsync(operation, "new");
        Assert.Equal(ChangeState.Failed, change.State);
        Assert.Equal("new", operation.Current);
        Assert.Contains("Leitura real após falha: new", change.Error!);
        operation.AfterWrite = null;
        var restored = await engine.RestoreAsync(change.Id, operation);
        Assert.Equal(ChangeState.Restored, restored.State);
        Assert.Equal("old", operation.Current);
    }

    [Fact]
    public async Task RestoreRefusesAnExternalChange()
    {
        var store = new FakeHistory();
        var operation = new FakeOperation();
        var engine = new ReversibleChangeEngine(store);
        var change = await engine.ApplyAsync(operation, "new");
        operation.Current = "third";
        var result = await engine.RestoreAsync(change.Id, operation);
        Assert.Equal(ChangeState.RestoreFailed, result.State);
        Assert.Equal("third", operation.Current);
        Assert.Equal(1, operation.Writes);
        Assert.Contains("Conflito", result.Error!);
    }

    [Fact]
    public async Task NetworkExpectedWritePreservesJournaledOriginalAcrossLateExternalChange()
    {
        var access = new FakeNetworkAccess { ChangeAtRead = 5 };
        var operation = new NetworkPropertyOperation(access, Guid.NewGuid(), "*RSS");
        var change = await new ReversibleChangeEngine(new FakeHistory()).ApplyAsync(operation,
            NetworkPropertyOperation.SerializeValue("1"));
        Assert.Equal(ChangeState.Failed, change.State);
        Assert.Equal(NetworkPropertyOperation.SerializeValue("0"), change.Before);
        Assert.Equal("2", access.Current); // Third-party value was not overwritten.
        Assert.Equal("0", Assert.Single(access.ExpectedWrites)); // Baseline came from backup, not a later read.
        Assert.Equal(0, access.Writes);
    }

    [Fact]
    public async Task NetworkRestorePassesAppliedValueAsExpectedAndOriginalAsDestination()
    {
        var access = new FakeNetworkAccess();
        var operation = new NetworkPropertyOperation(access, Guid.NewGuid(), "*RSS");
        var engine = new ReversibleChangeEngine(new FakeHistory());
        var change = await engine.ApplyAsync(operation, NetworkPropertyOperation.SerializeValue("1"));
        Assert.Equal(ChangeState.Applied, change.State);
        Assert.Equal(ChangeState.Restored, (await engine.RestoreAsync(change.Id, operation)).State);
        Assert.Equal(["0", "1"], access.ExpectedWrites);
        Assert.Equal(["1", "0"], access.Destinations);
        Assert.Equal("0", access.Current);
    }

    [Fact]
    public async Task RestoreIntentMustCommitBeforeWrite()
    {
        var store = new FakeHistory();
        var operation = new FakeOperation();
        var engine = new ReversibleChangeEngine(store);
        var change = await engine.ApplyAsync(operation, "new");
        store.BeforeSave = record =>
        {
            if (record.State == ChangeState.RestorePrepared) throw new IOException("disk full");
        };
        await Assert.ThrowsAsync<IOException>(() => engine.RestoreAsync(change.Id, operation));
        Assert.Equal("new", operation.Current);
        Assert.Equal(1, operation.Writes);
        Assert.Equal(ChangeState.Applied, (await store.LoadChangeAsync(change.Id))!.State);
    }

    [Fact]
    public async Task RestoreReadFailureIsPersistedAndNeverWrites()
    {
        var store = new FakeHistory();
        var operation = new FakeOperation();
        var engine = new ReversibleChangeEngine(store);
        var change = await engine.ApplyAsync(operation, "new");
        operation.ThrowOnRead = true;
        var result = await engine.RestoreAsync(change.Id, operation);
        Assert.Equal(ChangeState.RestoreFailed, result.State);
        Assert.Equal(1, operation.Writes);
        Assert.Equal("new", operation.Current);
        Assert.Equal(result, await store.LoadChangeAsync(change.Id));
        Assert.Contains("nenhuma escrita", result.Error!);
    }

    [Fact]
    public async Task RepeatedChangesKeepTheirIndividualBackupsAndUnwindInOrder()
    {
        var store = new FakeHistory();
        var operation = new FakeOperation();
        var engine = new ReversibleChangeEngine(store);
        var first = await engine.ApplyAsync(operation, "new");
        var second = await engine.ApplyAsync(operation, "third");
        Assert.Equal("old", first.Before);
        Assert.Equal("new", second.Before);
        Assert.Equal(ChangeState.RestoreFailed, (await engine.RestoreAsync(first.Id, operation)).State);
        Assert.Equal(ChangeState.Restored, (await engine.RestoreAsync(second.Id, operation)).State);
        Assert.Equal("new", operation.Current);
        Assert.Equal(ChangeState.Restored, (await engine.RestoreAsync(first.Id, operation)).State);
        Assert.Equal("old", operation.Current);
        var writes = operation.Writes;
        Assert.Equal(ChangeState.Restored, (await engine.RestoreAsync(first.Id, operation)).State);
        Assert.Equal(writes, operation.Writes); // Idempotent restoration still checks actual state.
    }

    [Theory]
    [InlineData(ChangeState.Prepared)]
    [InlineData(ChangeState.RestorePrepared)]
    public async Task IncompleteJournalCanRecoverFromActualAppliedValue(ChangeState state)
    {
        var store = new FakeHistory();
        var operation = new FakeOperation { Current = "new" };
        var now = DateTimeOffset.UtcNow;
        var incomplete = new ChangeRecord(Guid.NewGuid(), operation.Kind, operation.Target, "old", "new",
            ChangeRisk.Low, state, now, now);
        await store.SaveChangeAsync(incomplete);
        operation.OnWrite = _ => Assert.Equal(ChangeState.RestorePrepared, store.Saves.Last().State);
        var restored = await new ReversibleChangeEngine(store).RestoreAsync(incomplete.Id, operation);
        Assert.Equal(ChangeState.Restored, restored.State);
        Assert.Equal("old", operation.Current);
    }

    [Fact]
    public async Task FailedOutcomeCommitKeepsPreparedBackupAndReportsUncertainty()
    {
        var store = new FakeHistory
        {
            BeforeSave = record => { if (record.State == ChangeState.Applied) throw new IOException("disk full"); }
        };
        var operation = new FakeOperation();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ReversibleChangeEngine(store).ApplyAsync(operation, "new"));
        Assert.Equal("new", operation.Current);
        Assert.Equal(ChangeState.Prepared, Assert.Single(store.Saves).State);
        Assert.Contains("estado real", error.Message);
    }

    [Fact]
    public void PingSummaryUsesR7AndBreaksJitterAcrossGaps()
    {
        var now = DateTimeOffset.UtcNow;
        PingObservation[] samples = [new(1, now, "Success", 10), new(2, now, "Success", 14),
            new(3, now, "TimedOut", null), new(4, now, "Success", 100), new(5, now, "Success", 102),
            new(6, now, "LocalError", null, "send failed")];
        var result = ManualPingService.Summarize("example.test", now, 6, samples);
        Assert.Equal(5, result.SentCount);
        Assert.Equal(4, result.ReceivedCount);
        Assert.Equal(20.0, result.LossPercent);
        Assert.Equal(10.0, result.MinimumRttMs);
        Assert.Equal(56.5, result.AverageRttMs);
        Assert.Equal(102.0, result.MaximumRttMs);
        Assert.Equal(101.7, result.P95RttMs!.Value, 8);
        Assert.Equal(3.0, result.JitterMs);
    }

    [Fact]
    public void EmptyPingResultsDoNotInventZeroLatencyOrZeroLoss()
    {
        var result = ManualPingService.Summarize("example.test", DateTimeOffset.UtcNow, 10, [], true);
        Assert.Null(result.LossPercent);
        Assert.Null(result.AverageRttMs);
        Assert.Null(result.P95RttMs);
        Assert.Null(result.JitterMs);
        Assert.True(result.Cancelled);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("example.com; Remove-Item")]
    [InlineData("example.com:443")]
    public void PingHostRejectsCommandsUrlsAndPorts(string input) =>
        Assert.Throws<ArgumentException>(() => ManualPingService.ValidateHost(input));

    [Theory]
    [InlineData("[\"0\",\"1\"]")]
    [InlineData("[null]")]
    [InlineData(" [\"1\"] ")]
    [InlineData("command")]
    public void NetworkValuesRejectNonScalarAndNonCanonicalInput(string input) =>
        Assert.Throws<ArgumentException>(() => NetworkPropertyOperation.ParseValues(input));

    [Fact]
    public void NetworkValueRoundTripUsesTypedCanonicalJson()
    {
        Assert.Equal(["1"], NetworkPropertyOperation.ParseValues(NetworkPropertyOperation.SerializeValue("1")));
        Assert.Throws<ArgumentException>(() => new NetworkPropertyOperation(new NetworkInspector(), Guid.Empty, "*RSS"));
        Assert.Throws<ArgumentException>(() => new NetworkPropertyOperation(new NetworkInspector(), Guid.NewGuid(), "*RSS; command"));
    }

    private sealed class FakeNetworkAccess : INetworkPropertyAccess
    {
        private int _reads;
        public string Current { get; private set; } = "0";
        public int ChangeAtRead { get; init; }
        public int Writes { get; private set; }
        public List<string> ExpectedWrites { get; } = [];
        public List<string> Destinations { get; } = [];
        public Task<NetworkProperty> GetPropertyAsync(Guid id, string keyword, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (++_reads == ChangeAtRead) Current = "2";
            return Task.FromResult(new NetworkProperty(keyword, "Test only", [Current], Current,
                ["0", "1", "2"], ["0", "1", "2"], "Fake", "Fake", "Fake", "Fake"));
        }
        public Task SetPropertyExpectedAsync(Guid id, string keyword, IReadOnlyList<string> destination,
            IReadOnlyList<string> expected, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            ExpectedWrites.Add(expected[0]);
            Destinations.Add(destination[0]);
            if (Current != expected[0]) throw new InvalidOperationException("Test-only expected value conflict.");
            Current = destination[0];
            Writes++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOperation : IReversibleOperation
    {
        public string Current { get; set; } = "old";
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public bool ThrowOnRead { get; set; }
        public bool IgnoreWrite { get; init; }
        public Action<string>? OnWrite { get; set; }
        public Action<string>? AfterWrite { get; set; }
        public string Kind => "test-only";
        public string Target => "fake-target";
        public ChangeRisk Risk => ChangeRisk.Low;
        public string RecoveryInstructions => "Test-only fake operation.";
        public Task<string> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (ThrowOnRead) throw new IOException("test-only read failure");
            Reads++;
            return Task.FromResult(Current);
        }
        public Task ValidateAsync(string value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (value is not ("old" or "new" or "third")) throw new ArgumentException("invalid test destination");
            return Task.CompletedTask;
        }
        public Task WriteAsync(string value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            OnWrite?.Invoke(value);
            Writes++;
            if (!IgnoreWrite) Current = value;
            AfterWrite?.Invoke(value);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHistory : IHistoryStore
    {
        private readonly Dictionary<Guid, ChangeRecord> _changes = [];
        public List<ChangeRecord> Saves { get; } = [];
        public Action<ChangeRecord>? BeforeSave { get; set; }
        public Task SaveChangeAsync(ChangeRecord change, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            BeforeSave?.Invoke(change);
            _changes[change.Id] = change;
            Saves.Add(change);
            return Task.CompletedTask;
        }
        public Task<ChangeRecord?> LoadChangeAsync(Guid id, CancellationToken token = default) =>
            Task.FromResult(_changes.GetValueOrDefault(id));
        public Task<IReadOnlyList<ChangeRecord>> ListChangesAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<ChangeRecord>>(_changes.Values.ToArray());
        public Task InitializeAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task SaveSessionAsync(DiagnosticSession session, CancellationToken token = default) => throw new NotSupportedException();
        public Task<DiagnosticSession?> LoadSessionAsync(Guid id, CancellationToken token = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit = 100, CancellationToken token = default) => throw new NotSupportedException();
    }
}
