using PCAnalyzer.Core;
using PCAnalyzer.Network;
using PCAnalyzer.Optimization;

namespace PCAnalyzer.Tests;

public sealed class OptimizationNetworkTests
{
    [Fact]
    public async Task ConsultationPreviewReadsAndValidatesWithoutWritingHistoryOrConfiguration()
    {
        var history = new FakeHistory();
        var operation = new FakeOperation();
        var preview = await new ReversibleChangeEngine(history).PreviewAsync(operation, "new");
        Assert.Equal("old", preview.Before);
        Assert.Equal("new", preview.After);
        Assert.True(preview.HasChange);
        Assert.Equal(1, operation.Reads);
        Assert.Equal(0, operation.Writes);
        Assert.Empty(history.Saves);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("old")]
    [InlineData("different")]
    public async Task ApplyIsBlockedBeforeReadingOrWritingEvenWithPriorApproval(string? approved)
    {
        var history = new FakeHistory();
        var operation = new FakeOperation();
        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            new ReversibleChangeEngine(history).ApplyAsync(operation, "new", approvedOriginal: approved));
        Assert.Equal(AuditOnlyPolicy.Explanation, error.Message);
        Assert.Equal("old", operation.Current);
        Assert.Equal(0, operation.Reads);
        Assert.Equal(0, operation.Writes);
        Assert.Empty(history.Saves);
    }

    [Theory]
    [InlineData(ChangeState.Prepared)]
    [InlineData(ChangeState.Applied)]
    [InlineData(ChangeState.Failed)]
    [InlineData(ChangeState.RestorePrepared)]
    [InlineData(ChangeState.RestoreFailed)]
    [InlineData(ChangeState.Restored)]
    public async Task LegacyHistoryRemainsReadableAndUnchangedWhenRestorationIsRejected(ChangeState state)
    {
        var history = new FakeHistory();
        var operation = new FakeOperation();
        var now = DateTimeOffset.UtcNow;
        var original = new ChangeRecord(Guid.NewGuid(), operation.Kind, operation.Target, "old", "new",
            ChangeRisk.Low, state, now, now, RecoveryInstructions: "Historical recovery information");
        await history.SaveChangeAsync(original);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new ReversibleChangeEngine(history).RestoreAsync(original.Id, operation));
        Assert.Equal(original, await history.LoadChangeAsync(original.Id));
        Assert.Equal(original, Assert.Single(await history.ListChangesAsync()));
        Assert.Single(history.Saves);
        Assert.Equal(0, operation.Reads);
        Assert.Equal(0, operation.Writes);
    }

    [Fact]
    public async Task DirectPowerWriteIsRejectedBeforeAnyPlatformApi()
    {
        Assert.Throws<NotSupportedException>(() => PowerPlans.SetActive(Guid.NewGuid()));
        await Assert.ThrowsAsync<NotSupportedException>(() => new PowerPlanOperation().WriteAsync(
            Guid.NewGuid().ToString("D"), CancellationToken.None));
    }

    [Fact]
    public async Task DirectNetworkOperationWritesCannotReachPropertyAccess()
    {
        var access = new FakeNetworkAccess();
        var operation = new NetworkPropertyOperation(access, Guid.NewGuid(), "*RSS");
        await Assert.ThrowsAsync<NotSupportedException>(() => operation.WriteAsync("[\"1\"]", CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => operation.WriteExpectedAsync("[\"1\"]", "[\"0\"]", CancellationToken.None));
        Assert.Equal("0", access.Current);
        Assert.Equal(0, access.Reads);
        Assert.Equal(0, access.Writes);
        Assert.Empty(access.ExpectedWrites);
    }

    [Fact]
    public async Task DirectNetworkInspectorWritesAreRejectedWithoutDiscoveringAdapters()
    {
        var inspector = new NetworkInspector();
        // A deliberately nonexistent adapter must still hit the policy boundary first,
        // on Windows and Linux alike, before any PowerShell or hardware access.
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            inspector.SetPropertyAsync(id, "*RSS", ["1"]));
        INetworkPropertyAccess access = inspector;
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            access.SetPropertyExpectedAsync(id, "*RSS", ["1"], ["0"]));
    }

    [Fact]
    public async Task NetworkConsultationStillReadsCurrentDriverValuesWithoutWriting()
    {
        var access = new FakeNetworkAccess();
        var operation = new NetworkPropertyOperation(access, Guid.NewGuid(), "*RSS");
        Assert.Equal("[\"0\"]", await operation.ReadAsync(CancellationToken.None));
        var preview = await new ReversibleChangeEngine(new FakeHistory()).PreviewAsync(operation, "[\"1\"]");
        Assert.Equal("[\"0\"]", preview.Before);
        Assert.Equal("[\"1\"]", preview.After);
        Assert.Equal(0, access.Writes);
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
        public int Reads { get; private set; }
        public string Current { get; private set; } = "0";
        public int Writes { get; private set; }
        public List<string> ExpectedWrites { get; } = [];
        public Task<NetworkProperty> GetPropertyAsync(Guid id, string keyword, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(new NetworkProperty(keyword, "Test only", [Current], Current,
                ["0", "1", "2"], ["0", "1", "2"], "Fake", "Fake", "Fake", "Fake"));
        }
        public Task SetPropertyExpectedAsync(Guid id, string keyword, IReadOnlyList<string> destination,
            IReadOnlyList<string> expected, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            ExpectedWrites.Add(expected[0]);
            if (Current != expected[0]) throw new InvalidOperationException("Test-only expected value conflict.");
            Current = destination[0];
            Writes++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOperation : IReversibleOperation
    {
        public string Current { get; private set; } = "old";
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public string Kind => "test-only";
        public string Target => "fake-target";
        public ChangeRisk Risk => ChangeRisk.Low;
        public string RecoveryInstructions => "Test-only fake operation.";
        public Task<string> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
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
            Writes++;
            Current = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHistory : IHistoryStore
    {
        private readonly Dictionary<Guid, ChangeRecord> _changes = [];
        public List<ChangeRecord> Saves { get; } = [];
        public Task SaveChangeAsync(ChangeRecord change, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
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
