namespace PCAnalyzer.Core;

public enum Availability { Available, Collecting, Unsupported, PermissionRequired, ToolMissing, ReadError, InsufficientData }
public enum MeasurementKind { Direct, Derived, Estimate }
public enum Aggregation { Instantaneous, IntervalAverage, Cumulative }
public enum Severity { Information, Warning, Critical }
public enum Confidence { Low, Medium, High }
public enum FindingCategory { ObservedProblem, PossibleProblem, OptimizationOpportunity, NoAnomaly, InsufficientData }
public enum ChangeRisk { Low, Moderate, High }
public enum ChangeState { Prepared, Applied, Failed, Restored, RestoreFailed, RestorePrepared }

public sealed record MetricSample(
    string Key, string Component, double? Value, string Unit, string Source, string Method,
    DateTimeOffset Timestamp, TimeSpan SamplingInterval, Availability Status = Availability.Available,
    MeasurementKind Kind = MeasurementKind.Direct, Aggregation Aggregation = Aggregation.Instantaneous,
    string? Limitation = null)
{
    public static MetricSample Missing(string key, string component, string unit, string source, DateTimeOffset time,
        Availability status, string reason) => new(key, component, null, unit, source, "Indisponível", time,
            TimeSpan.Zero, status, Limitation: reason);
}

public sealed record HardwareFact(string Name, string? Value, string Source,
    Availability Status = Availability.Available, string? Limitation = null);
public sealed record HardwareComponent(string Id, string Category, string Name, IReadOnlyList<HardwareFact> Facts);
public sealed record Capability(string Name, Availability Status, string Detail);
public sealed record HardwareInventory(DateTimeOffset CapturedAt, IReadOnlyList<HardwareComponent> Components,
    IReadOnlyList<Capability> Capabilities);
public sealed record ProcessSample(int ProcessId, string Name, double? CpuPercent, long? WorkingSetBytes,
    DateTimeOffset Timestamp, string? Limitation = null);
public sealed record TelemetryBatch(DateTimeOffset Timestamp, IReadOnlyList<MetricSample> Metrics,
    IReadOnlyList<ProcessSample> Processes);
public sealed record Evidence(string Description, string Source, string? MetricKey = null,
    double? Value = null, string? Unit = null, DateTimeOffset? Timestamp = null);
public sealed record Finding(string Id, string Component, string Title, string Explanation,
    FindingCategory Category, Severity Severity, Confidence Confidence, IReadOnlyList<Evidence> Evidence,
    string Recommendation, string Verification, string Reversal, ChangeRisk Risk = ChangeRisk.Low,
    string Context = "Observação passiva", string ExpectedImpact = "Benefício não quantificado",
    string Requirements = "Repetir a observação em condições controladas");
public sealed record SessionContext(string Workload, string CollectionMode, double IntervalSeconds,
    string Notes = "", string? HardwareFingerprint = null);
public sealed record DiagnosticSession(Guid Id, string Label, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    SessionContext Context, HardwareInventory Inventory, IReadOnlyList<MetricSample> Samples,
    IReadOnlyList<Finding> Findings, IReadOnlyList<ChangeRecord>? Changes = null,
    IReadOnlyList<ProcessSample>? Processes = null);
public sealed record SessionSummary(Guid Id, string Label, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    int SampleCount, string Workload);
public sealed record ChangeRecord(Guid Id, string Kind, string Target, string Before, string After,
    ChangeRisk Risk, ChangeState State, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? Error = null, string? RecoveryInstructions = null);

public interface IHardwareDiscovery
{
    Task<HardwareInventory> DiscoverAsync(CancellationToken cancellationToken = default);
}
public interface ITelemetryCollector : IAsyncDisposable
{
    Task<TelemetryBatch> CollectAsync(TimeSpan interval, CancellationToken cancellationToken = default);
}
public interface IHistoryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SaveSessionAsync(DiagnosticSession session, CancellationToken cancellationToken = default);
    Task<DiagnosticSession?> LoadSessionAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task SaveChangeAsync(ChangeRecord change, CancellationToken cancellationToken = default);
    Task<ChangeRecord?> LoadChangeAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChangeRecord>> ListChangesAsync(CancellationToken cancellationToken = default);
}
public interface IReversibleOperation
{
    string Kind { get; }
    string Target { get; }
    ChangeRisk Risk { get; }
    string RecoveryInstructions { get; }
    Task<string> ReadAsync(CancellationToken cancellationToken);
    Task ValidateAsync(string value, CancellationToken cancellationToken);
    Task WriteAsync(string value, CancellationToken cancellationToken);
}

public interface IExpectedValueOperation : IReversibleOperation
{
    Task WriteExpectedAsync(string value, string expectedCurrent, CancellationToken cancellationToken);
}
