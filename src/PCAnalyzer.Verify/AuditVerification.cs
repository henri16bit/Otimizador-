using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using PCAnalyzer.Core;
using PCAnalyzer.Hardware;
using PCAnalyzer.Network;
using PCAnalyzer.Reports;
using PCAnalyzer.Storage;

namespace PCAnalyzer.Verify;

internal sealed record AuditVerificationCheck(string Description, bool Passed);
internal sealed record AuditProviderCoverage(string Provider, bool Required, int Observations, int Available,
    IReadOnlyDictionary<string, int> StatusCounts);
internal sealed record AuditInterpretationExample(string RuleId, string Current, string Recommended,
    string ShortReason, string FunctionalKind);
internal sealed record AuditVerificationResult(bool Passed, int SavedAssessmentCount,
    bool ExactSqliteRoundTrip, bool AnonymousJsonMatchesSavedSession, bool AnonymousHtmlMatchesSavedSession,
    bool AssessmentMetadataPreserved, bool NvidiaSettingsRead,
    IReadOnlyList<AuditProviderCoverage> Providers, IReadOnlyList<AuditVerificationCheck> Checks,
    IReadOnlyList<AuditInterpretationExample> Examples, string Scope);

/// <summary>Acceptance of actual read-only providers; fixtures cannot satisfy these checks.</summary>
internal static class AuditVerification
{
    [SupportedOSPlatform("windows")]
    internal static async Task<AuditVerificationResult> RunAsync(string directory, DiagnosticSession context,
        SQLiteHistoryStore history, JsonSerializerOptions jsonOptions, CancellationToken ct)
    {
        IPerformanceAuditProvider[] providers = [new WindowsConfigurationAuditProvider(), new DeviceInterruptAuditProvider(),
            new NetworkConfigurationAuditProvider(), new StorageMemoryAuditProvider(), new NvidiaDriverAuditProvider()];
        var started = DateTimeOffset.UtcNow;
        // Native/WMI/PowerShell reads remain bounded. No UAC, probes or configuration changes are requested.
        var coordinator = new PerformanceAuditCoordinator(providers, TimeSpan.FromSeconds(60));
        var audit = await coordinator.RunAsync(new InlineProgress<TestProgress>(progress =>
            Console.WriteLine("Auditoria passiva: " + progress.Stage)), ct);
        ct.ThrowIfCancellationRequested();
        var observations = audit.Assessments.Select(assessment => assessment.Observation).ToArray();
        var presentation = AuditPresentationBuilder.Build(audit);
        var checks = new List<AuditVerificationCheck>();
        var policies = audit.Assessments.Select(assessment =>
            (Assessment: assessment, Recommendation: AuditRecommendationClassifier.Classify(assessment))).ToArray();
        var informational = policies.Where(item => item.Recommendation.FunctionalKind is
            AuditFunctionalKind.Informational or AuditFunctionalKind.NoDocumentedEffect).ToArray();
        if (informational.Length > 0)
            checks.Add(new($"Interpretação: {informational.Length} leituras informativas ou sem efeito documentado não viram otimizações",
                informational.All(item => !item.Recommendation.IsImprovement &&
                    item.Recommendation.Kind is not (AuditRecommendationKind.Compare or AuditRecommendationKind.SpecificValue))));
        var incomplete = policies.Where(item => !AuditRecommendationClassifier.Readable(item.Assessment.Observation)).ToArray();
        if (incomplete.Length > 0)
            checks.Add(new($"Interpretação: {incomplete.Length} leituras ausentes ou indisponíveis não recomendam alteração nem alegam benefício",
                incomplete.All(item => !item.Recommendation.IsImprovement && !item.Recommendation.IsAdequate &&
                    item.Recommendation.Kind == AuditRecommendationKind.Unavailable &&
                    item.Recommendation.BenefitConfidence is null && item.Recommendation.TestId is null)));
        checks.Add(new("Apresentação: itens informativos, sem efeito documentado ou indisponíveis ficam fora das melhorias",
            presentation.Where(row => row.FunctionalKind is AuditFunctionalKind.Informational or
                AuditFunctionalKind.NoDocumentedEffect or AuditFunctionalKind.Unsupported or AuditFunctionalKind.Incomplete)
                .All(row => !row.IsImprovement && !row.IsComparison)));
        var coverage = providers.Select(provider =>
        {
            var rules = provider.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
            var rows = observations.Where(observation => rules.Contains(observation.RuleId)).ToArray();
            var required = provider is not NvidiaDriverAuditProvider;
            var available = rows.Count(row => row.Status == Availability.Available);
            if (required) checks.Add(new(provider.GetType().Name + ": ao menos uma observação realmente disponível", available > 0));
            return new AuditProviderCoverage(provider.GetType().Name, required, rows.Length, available,
                rows.GroupBy(row => row.Status.ToString()).ToDictionary(group => group.Key, group => group.Count()));
        }).ToArray();

        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var version = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
            foreach (var name in new[] { "ProductName", "CurrentBuildNumber" })
            {
                var expected = version?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                var rows = observations.Where(observation => observation.RuleId == "windows.version" && observation.Name == name &&
                    observation.Facts.GetValueOrDefault("registryView") == (view == RegistryView.Registry32 ? "32-bit" : "64-bit")).ToArray();
                checks.Add(new($"Registro {view}: {name} coincide com leitura HKLM independente, presente e REG_SZ",
                    !string.IsNullOrWhiteSpace(expected) && rows.Length == 1 && rows[0].Status == Availability.Available &&
                    rows[0].RawValue == expected && rows[0].Facts.GetValueOrDefault("presence") == "present" &&
                    rows[0].Facts.GetValueOrDefault("registryType") == RegistryValueKind.String.ToString()));
            }
        }

        using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var scheduling = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\PriorityControl", writable: false))
        {
            // Verify whatever DWORD is really stored; neither 0x26 nor 0x2A is an acceptance target.
            if (scheduling?.GetValue("Win32PrioritySeparation", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is int signed &&
                scheduling.GetValueKind("Win32PrioritySeparation") == RegistryValueKind.DWord)
            {
                var expected = unchecked((uint)signed);
                var rows = presentation.Where(row => row.Evidence.Any(assessment =>
                    assessment.Observation.RuleId == "windows.scheduler.separation")).ToArray();
                var expectedDisplay = "0x" + expected.ToString("X", CultureInfo.InvariantCulture) +
                    " (" + expected.ToString(CultureInfo.InvariantCulture) + ")";
                checks.Add(new("Agendamento: valor apresentado em hexadecimal/decimal coincide com DWORD real lido independentemente",
                    rows.Length == 1 && rows[0].CurrentValue == expectedDisplay && rows[0].Evidence.All(assessment =>
                        assessment.Observation.Status == Availability.Available &&
                        assessment.Observation.Facts.GetValueOrDefault("decimal") == expected.ToString(CultureInfo.InvariantCulture))));
                checks.Add(new("Agendamento: DWORD observado não recebe substituto universal nem ganho presumido",
                    rows.Length == 1 && !rows[0].IsImprovement && rows[0].RecommendationKind != AuditRecommendationKind.SpecificValue &&
                    rows[0].Evidence.All(assessment => AuditRecommendationClassifier.Classify(assessment).BenefitConfidence is null)));
            }
        }

        using (var ownProcess = Process.GetCurrentProcess())
        {
            var rows = observations.Where(observation => observation.RuleId == "windows.process.scheduling" &&
                observation.Facts.GetValueOrDefault("PID") == Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).ToArray();
            var own = rows.Length == 1 ? rows[0] : null;
            checks.Add(new("Processo próprio: classe de prioridade disponível e igual à leitura direta",
                own?.Status == Availability.Available && own.Facts.GetValueOrDefault("PriorityClass") == ownProcess.PriorityClass.ToString()));
            var expectedAffinity = "0x" + unchecked((ulong)ownProcess.ProcessorAffinity.ToInt64()).ToString("X", CultureInfo.InvariantCulture);
            checks.Add(new("Processo próprio: máscara de afinidade disponível e igual à leitura direta",
                own?.Status == Availability.Available && own.Facts.GetValueOrDefault("AffinityMask") == expectedAffinity));
            var ownRows = presentation.Where(row => row.Evidence.Any(assessment =>
                assessment.Observation.RuleId == "windows.process.scheduling" &&
                assessment.Observation.Facts.GetValueOrDefault("PID") == Environment.ProcessId.ToString(CultureInfo.InvariantCulture))).ToArray();
            var expectedPriority = ownProcess.PriorityClass switch
            {
                ProcessPriorityClass.Normal => "normal", ProcessPriorityClass.RealTime => "tempo real",
                ProcessPriorityClass.High => "alta", ProcessPriorityClass.AboveNormal => "acima do normal",
                ProcessPriorityClass.BelowNormal => "abaixo do normal", ProcessPriorityClass.Idle => "ociosa", _ => "não identificada"
            };
            checks.Add(new("Processo próprio: linha individual apresenta prioridade e afinidade reais, sem agregar outros executáveis",
                ownRows.Length == 1 && ownRows[0].Evidence.Count == 1 &&
                ownRows[0].CurrentValue == "Prioridade " + expectedPriority + " · máscara " + expectedAffinity &&
                ownRows[0].Evidence[0].Observation.InstanceId == own?.InstanceId));
        }

        // Only fixed, non-identifying fields are included: no paths, raw facts, third-party process names or PIDs.
        var examples = new List<AuditInterpretationExample>();
        AddExample("windows.version", observation => observation.Name == "ProductName");
        AddExample("windows.version", observation => observation.Name == "CurrentBuildNumber");
        AddExample("windows.scheduler.separation");
        AddExample("windows.process.scheduling", observation =>
            observation.Facts.GetValueOrDefault("PID") == Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        AddExample("memory.modules");
        void AddExample(string ruleId, Func<AuditObservation, bool>? condition = null)
        {
            var row = presentation.FirstOrDefault(candidate => candidate.Evidence.Any(assessment =>
                assessment.Observation.RuleId == ruleId && AuditRecommendationClassifier.Readable(assessment.Observation) &&
                (condition is null || condition(assessment.Observation))));
            if (row is not null)
                examples.Add(new(ruleId, row.CurrentValue, row.RecommendedValue, row.ShortReason, row.FunctionalKind.ToString()));
        }

        var topologyRows = observations.Where(observation => observation.RuleId == "hardware.cpu.topology").ToArray();
        var topology = topologyRows.Length == 1 ? topologyRows[0] : null;
        var activeProcessors = GetActiveProcessorCount(0xffff); // ALL_PROCESSOR_GROUPS, independent of the collector's core enumeration.
        checks.Add(new("Topologia: processadores lógicos positivos e iguais a GetActiveProcessorCount em todos os grupos",
            topology?.Status == Availability.Available && activeProcessors > 0 &&
            uint.TryParse(topology.Facts.GetValueOrDefault("logicalProcessorCount"), NumberStyles.None, CultureInfo.InvariantCulture, out var logical) &&
            logical == activeProcessors));
        checks.Add(new("PnP: ao menos um dispositivo presente com identidade e fonte disponíveis",
            observations.Any(observation => observation.RuleId == "devices.identity" && observation.Status == Availability.Available &&
                !string.IsNullOrWhiteSpace(observation.InstanceId) && !string.IsNullOrWhiteSpace(observation.Source))));

        var partial = observations.Any(observation => observation.Status != Availability.Available);
        var run = new TestSession(Guid.NewGuid(), "audit.verify", "Validação da auditoria Windows",
            partial ? TestStatus.Partial : TestStatus.Completed, started, DateTimeOffset.UtcNow,
            new("Leituras passivas concluídas"), new("Auditoria real do Windows", [audit.Summary],
                Session: context, Partial: partial, Audit: audit));
        await history.SaveTestAsync(run, ct);
        var saved = await history.LoadTestAsync(run.Id, ct) ?? throw new InvalidDataException("Auditoria não retornou do SQLite.");
        if (TestReportExporter.ExportJson(run, false) != TestReportExporter.ExportJson(saved, false))
            throw new InvalidDataException("Auditoria e recarga SQLite diferem em valores, regras ou classificações.");

        var jsonPath = Path.Combine(directory, "audit.anonymous.json");
        var htmlPath = Path.Combine(directory, "audit.anonymous.html");
        await TestReportExporter.ExportAsync(saved, jsonPath, true, ct);
        await TestReportExporter.ExportAsync(saved, htmlPath, true, ct);
        var json = await File.ReadAllTextAsync(jsonPath, ct);
        if (json != TestReportExporter.ExportJson(saved, true) ||
            await File.ReadAllTextAsync(htmlPath, ct) != TestReportExporter.ExportHtml(saved, true))
            throw new InvalidDataException("Relatórios da auditoria diferem da exportação anônima da sessão salva.");
        var exported = JsonSerializer.Deserialize<TestSession>(json, jsonOptions)?.Result?.Audit
            ?? throw new InvalidDataException("JSON da auditoria não contém avaliações estruturadas.");
        if (exported.CapturedAt != audit.CapturedAt || exported.CatalogVersion != audit.CatalogVersion ||
            exported.Assessments.Count != audit.Assessments.Count || !exported.Assessments.Zip(audit.Assessments).All(pair =>
                pair.First.Observation.RuleId == pair.Second.Observation.RuleId &&
                pair.First.Observation.Status == pair.Second.Observation.Status &&
                pair.First.Rule.Id == pair.Second.Rule.Id && pair.First.Rule.Support == pair.Second.Rule.Support &&
                pair.First.Classification == pair.Second.Classification && pair.First.Confidence == pair.Second.Confidence &&
                pair.First.Observation.Facts.Keys.Order(StringComparer.Ordinal).SequenceEqual(pair.Second.Observation.Facts.Keys.Order(StringComparer.Ordinal))))
            throw new InvalidDataException("A exportação anônima alterou a cobertura, os estados ou classificações da auditoria.");

        foreach (var provider in coverage)
            Console.WriteLine($"Auditoria {provider.Provider}: {provider.Available}/{provider.Observations} observações disponíveis" +
                (provider.Required ? "." : "; NVIDIA opcional, sem inferência de valores ausentes."));
        foreach (var check in checks.Where(check => !check.Passed)) Console.Error.WriteLine("Auditoria não validada: " + check.Description);
        return new(checks.All(check => check.Passed), audit.Assessments.Count, true, true, true, true,
            observations.Any(observation => observation.RuleId.StartsWith("nvidia.drs.", StringComparison.Ordinal) && observation.Status == Availability.Available),
            coverage, checks, examples, "Leituras reais no Windows, confronto de Registro/processo/topologia com APIs independentes, " +
                "classificação e apresentação de valores observados, SQLite exato e exportação anônima. " +
                "Não valida ganho, aplicação efetiva de tweaks, precisão de sensores, GPU ausente, interrupções em carga ou o PC do usuário.");
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);
}
