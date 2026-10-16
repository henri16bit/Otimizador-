using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCAnalyzer.Core;
using PCAnalyzer.Hardware;
using PCAnalyzer.Reports;
using PCAnalyzer.Storage;

namespace PCAnalyzer.Verify;

internal static class Program
{
    private static readonly string[] EssentialKeys = ["cpu.total.percent", "memory.total.bytes", "memory.available.bytes", "memory.commit.bytes", "memory.commit.limit.bytes"];
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(2);
    private const int MinimumBatches = 3;
    private const int MaximumBatches = 6;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    private static async Task<int> Main(string[] arguments)
    {
        if (arguments.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine("PCAnalyzer.Verify [--output DIRETÓRIO]\nValidação manual passiva: inventário, CPU/RAM/processo próprio, 3 a 6 lotes a cada 2 s, SQLite e relatórios anônimos. Sem elevação.");
            return 0;
        }
        string directory;
        try
        {
            directory = arguments.Length switch
            {
                0 => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCAnalyzer", "validation"),
                2 when arguments[0] == "--output" && !string.IsNullOrWhiteSpace(arguments[1]) => Path.GetFullPath(arguments[1]),
                _ => throw new ArgumentException("Use --output DIRETÓRIO ou --help.")
            };
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { Console.Error.WriteLine("Argumentos inválidos. Use --help."); return 1; }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Sem validação de hardware: esta ferramenta requer Windows. WMI, NVML e Win32 não foram executados; nenhum resultado foi simulado.");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        var phase = "inventário";
        try
        {
            Console.WriteLine("Iniciando validação passiva explícita. Ctrl+C cancela. Nenhum ping, stress, benchmark ou otimização será executado.");
            var started = DateTimeOffset.UtcNow;
            var inventory = await new WindowsHardwareDiscovery().DiscoverAsync(cancellation.Token);
            var context = new SessionContext("Validação passiva; carga do usuário não controlada", "Leve", Cadence.TotalSeconds,
                "Três a seis lotes; primeiro estabelece deltas. Os critérios usam os dois últimos lotes após aquecimento limitado. " +
                "CPU/RAM e processo próprio são obrigatórios; campos de processos protegidos de terceiros não são exigidos. " +
                "Esta verificação não valida precisão por instrumento de referência nem WPF, ETW ou PresentMon.");
            var session = new DiagnosticSession(Guid.NewGuid(), "Validação Windows explícita", started, null, context, inventory, [], []);
            PrintInventory(ReportPrivacy.Anonymize(session).Inventory);
            phase = "coleta";
            var batches = new List<TelemetryBatch>();
            await using (var collector = new WindowsTelemetryCollector())
            {
                for (var index = 0; index < MaximumBatches; index++)
                {
                    if (index > 0) await Task.Delay(Cadence, cancellation.Token);
                    var batch = await collector.CollectAsync(Cadence, cancellation.Token);
                    batches.Add(batch);
                    Console.WriteLine($"Lote {index + 1}/{MaximumBatches} no máximo — timestamp UTC {batch.Timestamp:O}");
                    foreach (var group in batch.Metrics.GroupBy(metric => metric.Status).OrderBy(group => group.Key))
                        Console.WriteLine($"  {group.Key}: {group.Count()} métricas");
                    foreach (var metric in batch.Metrics.Where(metric => EssentialKeys.Contains(metric.Key) ||
                        metric.Key == "collector.duration.ms" || metric.Key.StartsWith("gpu.", StringComparison.Ordinal)))
                        Console.WriteLine($"  {metric.Key}: {Number(metric.Value)} {metric.Unit} | {metric.Status} | {metric.Source} | intervalo {Number(metric.SamplingInterval.TotalSeconds)} s");
                    var self = OwnProcess(batch);
                    Console.WriteLine(self is null ? "  Processo próprio: não retornou uma entrada única neste lote." :
                        $"  Processo próprio: working set {self.WorkingSetBytes?.ToString(CultureInfo.InvariantCulture) ?? "—"} bytes | CPU {Number(self.CpuPercent)}% | {self.Limitation}");
                    if (batches.Count < MinimumBatches) continue;
                    if (Evaluate(batches, inventory, out _).All(check => check.Passed)) break;
                    if (batches.Count < MaximumBatches)
                        Console.WriteLine("  Aguardando fontes passivas, dentro do limite de seis lotes; dados ausentes continuam ausentes.");
                }
            }

            session = session with { EndedAt = DateTimeOffset.UtcNow, Samples = batches.SelectMany(batch => batch.Metrics).ToArray() };
            var checks = Evaluate(batches, inventory, out var gpuRequired);
            var runName = "run-" + session.Id.ToString("N");
            var runDirectory = Path.Combine(directory, runName);
            phase = "persistência SQLite";
            var store = new SQLiteHistoryStore(Path.Combine(runDirectory, "validation.db"));
            await store.SaveSessionAsync(session, cancellation.Token);
            var saved = await store.LoadSessionAsync(session.Id, cancellation.Token)
                ?? throw new InvalidDataException("Sessão não retornou do SQLite.");
            if (!string.Equals(JsonSerializer.Serialize(session, JsonOptions), JsonSerializer.Serialize(saved, JsonOptions), StringComparison.Ordinal))
                throw new InvalidDataException("Sessão e recarga SQLite diferem em valores ou metadados.");
            Console.WriteLine($"SQLite: recarga exata da sessão e das {saved.Samples.Count} métricas, incluindo valores nulos, estados, fontes, timestamps e intervalos.");

            phase = "exportação e verificação dos relatórios";
            var jsonPath = Path.Combine(runDirectory, "session.anonymous.json");
            var csvPath = Path.Combine(runDirectory, "session.anonymous.csv");
            var htmlPath = Path.Combine(runDirectory, "session.anonymous.html");
            await ReportExporter.ExportAsync(saved, jsonPath, ReportFormat.Json, true, cancellation.Token);
            await ReportExporter.ExportAsync(saved, csvPath, ReportFormat.Csv, true, cancellation.Token);
            await ReportExporter.ExportAsync(saved, htmlPath, ReportFormat.Html, true, cancellation.Token);
            var json = await File.ReadAllTextAsync(jsonPath, cancellation.Token);
            var exported = JsonSerializer.Deserialize<DiagnosticSession>(json, JsonOptions)
                ?? throw new InvalidDataException("JSON exportado inválido.");
            if (JsonSerializer.Serialize(ReportPrivacy.Anonymize(saved), JsonOptions) != JsonSerializer.Serialize(exported, JsonOptions))
                throw new InvalidDataException("JSON exportado difere do snapshot anônimo da sessão salva.");
            CheckMeasurementFields(saved.Samples, exported.Samples);
            CheckCsv(json, await File.ReadAllTextAsync(csvPath, cancellation.Token));
            if (await File.ReadAllTextAsync(htmlPath, cancellation.Token) != ReportExporter.ExportHtml(saved, true))
                throw new InvalidDataException("HTML escrito difere da exportação da sessão salva.");

            var pass = checks.All(check => check.Passed);
            var ownProcessSamples = batches.Select((batch, index) =>
            {
                var self = OwnProcess(batch);
                return new
                {
                    batch = index + 1, batch.Timestamp, uniqueOwnPidPresent = self is not null,
                    self?.WorkingSetBytes, self?.CpuPercent,
                    limitation = self?.Limitation ?? "O coletor não retornou uma entrada única para o PID próprio."
                };
            }).ToArray();
            var result = new
            {
                schemaVersion = 2, sessionId = session.Id, capturedAtUtc = DateTimeOffset.UtcNow, platform = "Windows",
                requiredMetricsAvailable = pass, nvidiaGpuTelemetryRequired = gpuRequired, sampleBatches = batches.Count,
                evaluatedBatches = batches.Select((_, index) => index + 1).Skip(1).TakeLast(2).ToArray(),
                maximumBatches = MaximumBatches, ownProcessSamples,
                savedMetricCount = saved.Samples.Count, exactSqliteRoundTrip = true,
                jsonNumericAndMetadataPreserved = true, csvMatchesJsonLeaves = true, htmlMatchesSavedSessionExport = true,
                referenceHardwareAccuracyValidated = false, wpfEtwPresentMonOrOptimizationValidated = false,
                checks,
                scope = "Leituras passivas reais e persistência/exportação; não prova precisão de sensores, desempenho esperado, ganhos, WPF, ETW, PresentMon ou reversão."
            };
            await File.WriteAllTextAsync(Path.Combine(runDirectory, "validation-result.json"), JsonSerializer.Serialize(result, JsonOptions), new UTF8Encoding(false), cancellation.Token);
            foreach (var check in checks.Where(check => !check.Passed)) Console.Error.WriteLine($"Não validado: {check.Description}");
            Console.WriteLine(gpuRequired ? "GPU NVIDIA: exigida na verificação dos sensores NVML essenciais." :
                "GPU: nenhuma NVIDIA identificada; critérios de telemetria GPU não validados por esta execução.");
            Console.WriteLine("Relatórios JSON/CSV/HTML anônimos verificados a partir dos dados recarregados. O banco SQLite guarda dados locais completos.");
            Console.WriteLine($"Artefatos: pasta {runName} no diretório " + (arguments.Length == 0 ? @"%LOCALAPPDATA%\PCAnalyzer\validation." : "informado em --output."));
            Console.WriteLine(pass ? "Verificação passiva concluída; CPU/RAM e processo próprio disponíveis após aquecimento. Precisão por referência e demais critérios Windows continuam pendentes." :
                "Verificação incompleta: faltaram leituras essenciais. Artefatos preservados com os estados reais.");
            return pass ? 0 : 3;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Validação cancelada; recursos de coleta foram liberados. Uma exportação interrompida não é declarada válida."); return 130; }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Falha durante {phase} ({error.GetType().Name}); esta execução não valida o critério. Nenhum dado ausente foi substituído.");
            return phase.Contains("persistência", StringComparison.Ordinal) || phase.Contains("relatórios", StringComparison.Ordinal) ? 4 : 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private sealed record Check(string Description, bool Passed);

    private static ProcessSample? OwnProcess(TelemetryBatch batch)
    {
        var matching = batch.Processes.Where(process => process.ProcessId == Environment.ProcessId).ToArray();
        return matching.Length == 1 ? matching[0] : null;
    }

    private static IReadOnlyList<Check> Evaluate(IReadOnlyList<TelemetryBatch> batches, HardwareInventory inventory, out bool gpuRequired)
    {
        var checks = new List<Check>();
        checks.Add(new("Ao menos três lotes passivos coletados; primeiro reservado para estabelecer deltas", batches.Count >= MinimumBatches));
        var nvmlIds = inventory.Components.Where(component => component.Category == "GPU (NVML)").Select(component => component.Id).Distinct().ToArray();
        var nvidiaWmiCount = inventory.Components.Count(component => component.Category == "GPU" &&
            (component.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || component.Facts.Any(fact =>
                fact.Name == "AdapterCompatibility" && fact.Value?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) is true)));
        gpuRequired = nvidiaWmiCount > 0 || nvmlIds.Length > 0;
        if (gpuRequired)
            checks.Add(new("Dispositivos NVIDIA enumerados pela NVML (sem presumir associação de índices WMI/NVML)", nvmlIds.Length > 0 && nvmlIds.Length >= nvidiaWmiCount));
        foreach (var (batch, index) in batches.Select((batch, index) => (batch, index)).Skip(1).TakeLast(2))
        {
            var self = OwnProcess(batch);
            checks.Add(new($"Lote {index + 1}: processo próprio com PID único, nome e timestamp do lote", self is not null &&
                !string.IsNullOrWhiteSpace(self.Name) && self.Name != "PID " + Environment.ProcessId && self.Timestamp == batch.Timestamp));
            checks.Add(new($"Lote {index + 1}: working set do processo próprio disponível e positivo", self?.WorkingSetBytes is > 0));
            checks.Add(new($"Lote {index + 1}: CPU do processo próprio disponível e entre 0 e 100% após duas amostras", self?.CpuPercent is { } cpu &&
                double.IsFinite(cpu) && cpu is >= 0 and <= 100));
            foreach (var key in EssentialKeys) CheckMetric(key, 0, key == "cpu.total.percent" ? 100 : double.MaxValue,
                requirePositive: key is "memory.total.bytes" or "memory.commit.limit.bytes");
            var total = Value("memory.total.bytes");
            var available = Value("memory.available.bytes");
            var commit = Value("memory.commit.bytes");
            var limit = Value("memory.commit.limit.bytes");
            checks.Add(new($"Lote {index + 1}: RAM disponível ≤ RAM física; commit ≤ limite de commit", total.HasValue && available.HasValue &&
                commit.HasValue && limit.HasValue && available <= total && commit <= limit));
            foreach (var id in nvmlIds)
            {
                CheckMetric(id + ".utilization.percent", 0, 100);
                CheckMetric(id + ".memory.used.bytes", 0, double.MaxValue);
                CheckMetric(id + ".memory.total.bytes", 0, double.MaxValue, requirePositive: true);
                var used = Value(id + ".memory.used.bytes");
                var capacity = Value(id + ".memory.total.bytes");
                checks.Add(new($"Lote {index + 1}: {id} VRAM usada ≤ capacidade", used.HasValue && capacity.HasValue && used <= capacity));
            }
            void CheckMetric(string key, double minimum, double maximum, bool requirePositive = false)
            {
                var matching = batch.Metrics.Where(metric => metric.Key == key).ToArray();
                var metric = matching.Length == 1 ? matching[0] : null;
                var value = Value(key);
                var valid = value.HasValue && value >= minimum && value <= maximum && (!requirePositive || value > 0) &&
                    metric is not null && !string.IsNullOrWhiteSpace(metric.Source) && !string.IsNullOrWhiteSpace(metric.Method) &&
                    metric.Timestamp != default && (key != "cpu.total.percent" || metric.SamplingInterval > TimeSpan.Zero);
                checks.Add(new($"Lote {index + 1}: {key} disponível, finito e com metadados de origem" +
                    (metric is null ? " (ausente/duplicado)" : " (" + metric.Status + ")"), valid));
            }
            double? Value(string key)
            {
                var matching = batch.Metrics.Where(metric => metric.Key == key).ToArray();
                return matching.Length == 1 && matching[0].Status == Availability.Available && matching[0].Value is { } value && double.IsFinite(value) ? value : null;
            }
        }
        return checks;
    }

    private static void PrintInventory(HardwareInventory inventory)
    {
        Console.WriteLine("Inventário (modelos/fontes; identificadores pessoais omitidos):");
        foreach (var component in inventory.Components)
        {
            string[] preferred = component.Category switch
            {
                "Sistema" => ["Caption", "Model"], "BIOS" => ["SMBIOSBIOSVersion"], "Placa-mãe" => ["Product"],
                "CPU" or "GPU" or "Rede" => ["Name"], "GPU (NVML)" => ["Nome"], "RAM" => ["PartNumber"],
                "Armazenamento" => ["Model", "FriendlyName"], "Monitor" => ["Modelo EDID"], _ => []
            };
            var fact = preferred.Select(name => component.Facts.FirstOrDefault(item => item.Name == name && item.Status == Availability.Available &&
                !string.IsNullOrWhiteSpace(item.Value))).FirstOrDefault(item => item is not null);
            var name = fact?.Value ?? (component.Category == "Sistema" ? component.Category : component.Name);
            var source = fact?.Source ?? component.Facts.FirstOrDefault()?.Source ?? "Provedor não retornado";
            Console.WriteLine($"  {component.Category}: {name} | fonte: {source}");
        }
        foreach (var capability in inventory.Capabilities.Where(capability => capability.Status != Availability.Available))
            Console.WriteLine($"  Disponibilidade: {capability.Name} = {capability.Status}");
    }

    private static void CheckMeasurementFields(IReadOnlyList<MetricSample> original, IReadOnlyList<MetricSample> exported)
    {
        if (original.Count != exported.Count) throw new InvalidDataException("Quantidade de métricas mudou na exportação.");
        for (var index = 0; index < original.Count; index++)
        {
            var left = original[index]; var right = exported[index];
            if (!Nullable.Equals(left.Value, right.Value) || left.Status != right.Status || left.Timestamp != right.Timestamp ||
                left.SamplingInterval != right.SamplingInterval || left.Kind != right.Kind || left.Aggregation != right.Aggregation)
                throw new InvalidDataException("Número, ausência, disponibilidade ou metadados temporais mudaram na exportação.");
        }
    }

    private static void CheckCsv(string json, string csv)
    {
        using var document = JsonDocument.Parse(json);
        var expected = new Dictionary<string, (string Type, string Value)>();
        Flatten(document.RootElement, "");
        var rows = ReadCsv(csv);
        if (rows.Count == 0 || !rows[0].SequenceEqual(["path", "type", "value"]) || rows.Count - 1 != expected.Count)
            throw new InvalidDataException("CSV não contém a representação completa do JSON.");
        foreach (var row in rows.Skip(1))
            if (row.Length != 3 || !expected.Remove(row[0], out var field) || row[1] != field.Type || row[2] != field.Value)
                throw new InvalidDataException("Campo CSV difere do JSON salvo/exportado.");
        if (expected.Count != 0) throw new InvalidDataException("CSV omitiu campos.");

        void Flatten(JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Any())
                foreach (var property in element.EnumerateObject()) Flatten(property.Value, path + "/" + property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
            else if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0)
                for (var index = 0; index < element.GetArrayLength(); index++) Flatten(element[index], path + "/" + index.ToString(CultureInfo.InvariantCulture));
            else expected.Add(path, (element.ValueKind.ToString(), element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText()));
        }
    }

    private static IReadOnlyList<string[]> ReadCsv(string csv)
    {
        var rows = new List<string[]>(); var row = new List<string>(); var value = new StringBuilder(); var quoted = false;
        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (character == '"')
            {
                if (quoted && index + 1 < csv.Length && csv[index + 1] == '"') { value.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (character == ',' && !quoted) { row.Add(value.ToString()); value.Clear(); }
            else if (character is '\r' or '\n' && !quoted)
            {
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n') index++;
                row.Add(value.ToString()); value.Clear(); rows.Add(row.ToArray()); row.Clear();
            }
            else value.Append(character);
        }
        if (quoted) throw new InvalidDataException("CSV possui campo entre aspas incompleto.");
        if (value.Length > 0 || row.Count > 0) { row.Add(value.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    private static string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "—";
}
