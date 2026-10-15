using PCAnalyzer.Core;
using PCAnalyzer.Network;
using PCAnalyzer.Gaming;

namespace PCAnalyzer.App.Services;

public sealed record SelectionItem(string Id, string Name);
public sealed record NetworkSettingView(string AdapterId, string AdapterName, string Keyword, string Name,
    string CurrentValue, IReadOnlyList<SelectionItem> ValidValues, string Explanation, string CurrentDisplayValue = "",
    string Status = "Somente leitura")
{
    public bool CanChange => ValidValues.Count > 0;
}

public interface IApplicationBackend : IAsyncDisposable, ITestHistoryStore
{
    string DataDirectory { get; }
    Task<HardwareInventory> InitializeAsync(CancellationToken ct);
    Task<TelemetryBatch> CollectAsync(TimeSpan interval, CancellationToken ct);
    IReadOnlyList<Finding> Analyze(HardwareInventory inventory, IReadOnlyList<MetricSample> samples);
    Task SaveSessionAsync(DiagnosticSession session, CancellationToken ct);
    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken ct);
    Task<DiagnosticSession?> LoadSessionAsync(Guid id, CancellationToken ct);
    Task ExportAsync(DiagnosticSession session, string path, string format, bool anonymize, CancellationToken ct);
    Task<TechnicalResult> CompareAsync(Guid before, Guid after, CancellationToken ct);
    Task<TechnicalResult> PingAsync(string host, Guid? adapterId, IProgress<TestProgress> progress, CancellationToken ct);
    Task<TechnicalResult> ImportFramesAsync(string path, PresentMonParseOptions options, CancellationToken ct);
    Task<TechnicalResult> CaptureFramesAsync(string executablePath, int processId, CancellationToken ct);
    Task<TechnicalResult> CaptureDpcAsync(CancellationToken ct);
    Task<TechnicalResult> ImportEtlAsync(string path, CancellationToken ct);
    Task<IReadOnlyList<SelectionItem>> ListPowerPlansAsync(CancellationToken ct);
    Task<string> PreviewPowerPlanAsync(string id, CancellationToken ct);
    Task<ChangeRecord> ApplyPowerPlanAsync(string id, CancellationToken ct);
    Task<NetworkSnapshot> DiscoverNetworkAsync(CancellationToken ct);
    Task<AdapterDetail> InspectNetworkAsync(Guid id, CancellationToken ct);
    IReadOnlyList<NetworkSettingView> NetworkSettingsFor(AdapterDetail detail);
    Task<string> PreviewNetworkChangeAsync(NetworkSettingView setting, string value, CancellationToken ct);
    Task<ChangeRecord> ApplyNetworkChangeAsync(NetworkSettingView setting, string value, CancellationToken ct);
    Task<IReadOnlyList<ChangeRecord>> ListChangesAsync(CancellationToken ct);
    Task<ChangeRecord> RestoreChangeAsync(Guid id, CancellationToken ct);
    Task<TechnicalResult> RunLabAsync(string test, string? path, IProgress<double> progress, CancellationToken ct);
    void LogError(string operation, Exception exception);
}
