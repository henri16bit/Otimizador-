using PCAnalyzer.Core;
using PCAnalyzer.Hardware;
using PCAnalyzer.Network;

namespace PCAnalyzer.App.Services;

public sealed partial class ApplicationBackend
{
    private PerformanceAuditCoordinator? _auditCoordinator;
    private PerformanceAuditReport? _lastConfigurationAudit;
    private readonly SemaphoreSlim _auditGate = new(1, 1);

    public async Task<TechnicalResult> AuditConfigurationAsync(IProgress<TestProgress> progress, CancellationToken ct)
    {
        await _auditGate.WaitAsync(ct);
        try
        {
            _lastConfigurationAudit = null;
            _auditCoordinator ??= new PerformanceAuditCoordinator(
                [new WindowsConfigurationAuditProvider(), new NvidiaDriverAuditProvider(),
                 new DeviceInterruptAuditProvider(), new NetworkConfigurationAuditProvider(_network),
                 new StorageMemoryAuditProvider()], log: LogError);
            var started = DateTimeOffset.UtcNow;
            var audit = await _auditCoordinator.RunAsync(progress, ct);
            _lastConfigurationAudit = audit;
            var inventory = _lastInventory ?? new HardwareInventory(started, [],
                [new("Inventário", Availability.Collecting, "A identidade do hardware ainda não terminou de ser coletada.")]);
            var available = audit.Assessments.Count(a => a.Observation.Status == Availability.Available);
            var session = new DiagnosticSession(Guid.NewGuid(), "Auditoria de configurações", started, DateTimeOffset.UtcNow,
                new("Snapshot de configurações; sem alteração operacional", "Auditoria", 0,
                    "Os valores são leituras pontuais e podem mudar durante a coleta. Benefícios exigem medições controladas."), inventory, [], []);
            return new("Auditoria de configurações", [audit.Summary,
                    "Nenhuma configuração foi alterada. Uma configuração encontrada não comprova uso pelo jogo nem ganho de desempenho."],
                Limitations: audit.Limitations, Availability: available == 0 ? Availability.InsufficientData : Availability.Available,
                Cancelled: ct.IsCancellationRequested, Session: session, ComparisonKey: "audit:configuration:v1",
                Partial: audit.Assessments.Any(a => a.Observation.Status != Availability.Available), Audit: audit);
        }
        finally { _auditGate.Release(); }
    }
}
