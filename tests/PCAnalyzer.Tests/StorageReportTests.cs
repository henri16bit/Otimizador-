using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PCAnalyzer.Core;
using PCAnalyzer.Reports;
using PCAnalyzer.Storage;

namespace PCAnalyzer.Tests;

public sealed class StorageReportTests
{
    [Fact]
    public async Task SessionRoundTripPreservesEveryMetricPropertyAndSurvivesReopen()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "nested", "history.db");
        var original = Session() with { Label = "Sessão '); DROP TABLE sessions; --" };
        var store = new SQLiteHistoryStore(path);
        await store.SaveSessionAsync(original);
        var reopened = new SQLiteHistoryStore(path);
        var loaded = await reopened.LoadSessionAsync(original.Id);
        Assert.NotNull(loaded);
        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(original.Label, loaded.Label);
        Assert.Equal(original.Context, loaded.Context);
        Assert.Equal(original.StartedAt, loaded.StartedAt);
        Assert.Equal(original.StartedAt.Offset, loaded.StartedAt.Offset);
        Assert.Equal(original.Samples, loaded.Samples);
        Assert.Equal(original.Inventory.Components[0].Facts, loaded.Inventory.Components[0].Facts);
        Assert.Equal(original.Findings[0].Evidence, loaded.Findings[0].Evidence);
        Assert.Single(await reopened.ListSessionsAsync(1));
        Assert.Empty(await reopened.ListSessionsAsync(0));
        Assert.Null(await reopened.LoadSessionAsync(Guid.NewGuid()));
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RecoveryLedgerCommitsPreparedAndAppliedStatesWithoutLosingOriginalValue()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "history.db");
        var store = new SQLiteHistoryStore(path);
        var now = DateTimeOffset.UtcNow;
        var prepared = new ChangeRecord(Guid.NewGuid(), "NetworkProperty", "Ethernet", "original;valor", "novo",
            ChangeRisk.Moderate, ChangeState.Prepared, now, now, RecoveryInstructions: "Restaurar original");
        await store.SaveChangeAsync(prepared);
        Assert.Equal(prepared, await new SQLiteHistoryStore(path).LoadChangeAsync(prepared.Id));
        var applied = prepared with { State = ChangeState.Applied, UpdatedAt = now.AddSeconds(1) };
        await store.SaveChangeAsync(applied);
        Assert.Equal(applied, Assert.Single(await store.ListChangesAsync()));
        Assert.Equal(prepared.Before, (await store.LoadChangeAsync(prepared.Id))!.Before);
    }

    [Fact]
    public async Task AssociatedChangesRoundTripAndAppearExactlyInEveryCompleteReport()
    {
        using var directory = new TemporaryDirectory();
        var session = Session();
        var change = new ChangeRecord(Guid.NewGuid(), "network-advanced-property", "Ethernet:InterruptModeration", "[\"1\"]", "[\"0\"]",
            ChangeRisk.Moderate, ChangeState.Restored, session.StartedAt.AddSeconds(1), session.StartedAt.AddSeconds(9),
            RecoveryInstructions: "Valor original verificado.");
        session = session with { Changes = [change] };
        var store = new SQLiteHistoryStore(Path.Combine(directory.Path, "history.db"));
        await store.SaveSessionAsync(session);
        var loaded = await store.LoadSessionAsync(session.Id);
        Assert.Equal(change, Assert.Single(loaded!.Changes!));
        using var json = JsonDocument.Parse(ReportExporter.ExportJson(loaded, anonymize: false));
        var exported = json.RootElement.GetProperty("changes")[0];
        Assert.Equal(change.Id.ToString("D"), exported.GetProperty("id").GetString());
        Assert.Equal(change.Before, exported.GetProperty("before").GetString());
        Assert.Equal(change.After, exported.GetProperty("after").GetString());
        Assert.Equal("Restored", exported.GetProperty("state").GetString());
        var csv = ReportExporter.ExportCsv(loaded, anonymize: false);
        Assert.Contains("\"/changes/0/state\",\"String\",\"Restored\"", csv);
        Assert.Contains("\"/changes/0/before\",\"String\",\"[\"\"1\"\"]\"", csv);
        var html = ReportExporter.ExportHtml(loaded, anonymize: false);
        Assert.Contains("Diário de alterações da sessão", html);
        Assert.Contains("Valor original", html);
        Assert.Contains("Restored", html);
        Assert.Contains("/changes/0/updatedAt", html);
        Assert.Contains("[&quot;1&quot;]", html);
    }

    [Fact]
    public void ChangePrivacyScrubsEveryFieldAndKeepsNumericDriverConfigurations()
    {
        var session = Session();
        const string privateGuid = "a8232cf9-5c4c-4967-ac9b-d5d36a8fef44";
        const string privatePath = @"C:\Users\Alice\private.txt";
        var change = new ChangeRecord(Guid.Parse(privateGuid), "kind office-machine 192.168.4.51",
            privateGuid + ":adapter office-machine", "[\"1\"]", "[\"0\"]", ChangeRisk.Moderate,
            ChangeState.RestoreFailed, session.StartedAt, session.StartedAt.AddSeconds(3),
            "Falha " + privatePath + " IP=192.168.4.51", "Recuperar " + privateGuid + " office-machine");
        var sensitiveValue = change with { Id = Guid.NewGuid(),
            Before = JsonSerializer.Serialize(new[] { privatePath, "192.168.4.51", privateGuid }),
            After = JsonSerializer.Serialize(new Dictionary<string, string> { ["MACAddress"] = "00:11:22:33:44:55",
                ["IPv6Address"] = "2001:db8::11", ["token"] = "private-unlabeled-secret", ["setting"] = "0" }) };
        session = session with { Changes = [change, sensitiveValue] };
        var anonymized = ReportPrivacy.Anonymize(session);
        Assert.NotEqual(change.Id, anonymized.Changes![0].Id);
        Assert.Equal(change.Before, anonymized.Changes[0].Before);
        Assert.Equal(change.After, anonymized.Changes[0].After);
        Assert.Equal(change.CreatedAt, anonymized.Changes[0].CreatedAt);
        Assert.Equal(change.State, anonymized.Changes[0].State);
        Assert.Equal(anonymized.Changes[0].Id, ReportPrivacy.Anonymize(session).Changes![0].Id);
        using var redactedConfiguration = JsonDocument.Parse(anonymized.Changes[1].Before);
        Assert.Equal(JsonValueKind.Array, redactedConfiguration.RootElement.ValueKind);
        foreach (var report in new[] { ReportExporter.ExportJson(session), ReportExporter.ExportCsv(session), ReportExporter.ExportHtml(session) })
            foreach (var privateText in new[] { privateGuid, "office-machine", "192.168.4.51", "Alice", "00:11:22:33:44:55", "2001:db8::11", "private-unlabeled-secret" })
                Assert.DoesNotContain(privateText, report, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(change, session.Changes[0]);
        Assert.Equal(sensitiveValue.Before, session.Changes[1].Before);
    }

    [Fact]
    public async Task ConcurrentSavesKeepIndependentSessionRecords()
    {
        using var directory = new TemporaryDirectory();
        var store = new SQLiteHistoryStore(Path.Combine(directory.Path, "history.db"));
        var sessions = Enumerable.Range(0, 12).Select(index => Session() with
        {
            Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow.AddSeconds(index)
        }).ToArray();
        await Task.WhenAll(sessions.Select(session => store.SaveSessionAsync(session)));
        var listed = await store.ListSessionsAsync();
        Assert.Equal(12, listed.Count);
        Assert.Equal(sessions[^1].Id, listed[0].Id);
    }

    [Fact]
    public async Task FutureDatabaseVersionIsRefusedWithoutDowngradingIt()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "future.db");
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version=999;";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => new SQLiteHistoryStore(path).InitializeAsync());
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(999L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public void GenericIdentificationFactsAreRedactedRegardlessOfIdentifierFormat()
    {
        var session = Session();
        var component = session.Inventory.Components[0] with
        {
            Facts = [new("UniqueId", "custom-sensitive-identifier", "WMI"),
                new("Número de série", "numero-secreto-789", "WMI"), new("SystemName", "another-private-host", "WMI")]
        };
        session = session with { Inventory = session.Inventory with { Components = [component] } };
        var json = ReportExporter.ExportJson(session);
        Assert.DoesNotContain("custom-sensitive-identifier", json);
        Assert.DoesNotContain("numero-secreto-789", json);
        Assert.DoesNotContain("another-private-host", json);
    }

    [Fact]
    public void DefaultPrivacyRemovesIdentifiersFromAllTextAndKeepsOriginalNumericValues()
    {
        var session = Session();
        var json = ReportExporter.ExportJson(session);
        foreach (var sensitive in new[] { "office-machine", "SERIAL-PRIVATE-123", "192.168.4.51", "00:11:22:33:44:55", "2001:db8::11", @"C:\Users\Alice", "private-drive-id" })
            Assert.DoesNotContain(sensitive, json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(session.Samples[0].Value, document.RootElement.GetProperty("samples")[0].GetProperty("value").GetDouble());
        Assert.Equal(session.Samples[0].SamplingInterval.ToString(), document.RootElement.GetProperty("samples")[0].GetProperty("samplingInterval").GetString());
        Assert.Contains("office-machine", session.Label);
        Assert.Contains("SERIAL-PRIVATE-123", session.Inventory.Components[0].Facts[0].Value);
        Assert.Contains("office-machine", ReportExporter.ExportJson(session, anonymize: false));
    }

    [Fact]
    public void ExportValuesRemainInvariantAndCsvEscapesQuotesCommasAndNewlines()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            var session = Session() with { Label = "Texto, com \"aspas\"\r\nnova linha" };
            var csv = ReportExporter.ExportCsv(session, anonymize: false);
            Assert.Contains("\"/label\",\"String\",\"Texto, com \"\"aspas\"\"\r\nnova linha\"\r\n", csv);
            Assert.Contains("\"/samples/0/value\",\"Number\",\"12.125\"", csv);
            Assert.Contains("\"/samples/1/value\",\"Null\",\"null\"", csv);
            Assert.Contains("\"12.125\"", ReportExporter.ExportMetricCsv(session, anonymize: false));
            var json = ReportExporter.ExportJson(session, anonymize: false);
            Assert.Contains("12.125", json);
            Assert.DoesNotContain("12,125", json);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void HtmlEscapesApplicationAndHardwareText()
    {
        var session = Session() with { Label = "<script>alert('teste')</script>" };
        var html = ReportExporter.ExportHtml(session, anonymize: false);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("12.125", html);
        Assert.Contains("IntervalAverage", html);
        Assert.Contains("/inventory/components/0/facts/0/value", html);
    }

    [Fact]
    public async Task ExportWritesAtomicallyAndHonorsCancellation()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "exports", "session.json");
        await ReportExporter.ExportAsync(Session(), path, ReportFormat.Json);
        var saved = await File.ReadAllTextAsync(path);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReportExporter.ExportAsync(Session(), path, ReportFormat.Json,
            cancellationToken: canceled.Token));
        Assert.Equal(saved, await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void ComparisonShowsMeansPercentilesAndPreservesMissingAsNull()
    {
        var before = SessionWithSamples([1, 2, 3, 4]);
        var after = SessionWithSamples([2, 4, 6, 8]);
        var result = SessionComparer.Compare(before, after);
        var comparison = Assert.Single(result.Metrics);
        Assert.Equal(2.5, comparison.BeforeMean);
        Assert.Equal(5, comparison.AfterMean);
        Assert.Equal(2.5, comparison.MeanDelta);
        Assert.Equal(100, comparison.MeanDeltaPercent);
        Assert.Equal(3.85, comparison.BeforeP95!.Value, 10);
        Assert.Contains(result.Warnings, warning => warning.Contains("Menos de 10"));
        var unavailable = SessionComparer.Compare(before, after with { Samples = [] });
        var absent = Assert.Single(unavailable.Metrics);
        Assert.Null(absent.AfterMean);
        Assert.Null(absent.MeanDelta);
        Assert.False(absent.Comparable);
    }

    [Fact]
    public void ComparisonWarnsForWorkloadIntervalHardwareAndMetadataMismatch()
    {
        var before = SessionWithSamples([10, 20]);
        var after = SessionWithSamples([12, 24]) with
        {
            Context = new("jogo", "Ativo", 2, HardwareFingerprint: "different-hardware")
        };
        after = after with { Samples = after.Samples.Select(sample => sample with { Source = "fonte diferente" }).ToArray() };
        var result = SessionComparer.Compare(before, after);
        Assert.False(result.ConditionsComparable);
        Assert.Contains(result.Warnings, warning => warning.Contains("carga"));
        Assert.Contains(result.Warnings, warning => warning.Contains("intervalo"));
        Assert.Contains(result.Warnings, warning => warning.Contains("hardware"));
        Assert.False(Assert.Single(result.Metrics).Comparable);
        Assert.Null(result.Metrics[0].MeanDelta);
        Assert.Contains("não demonstram causalidade", result.Interpretation);
    }

    [Fact]
    public void VolatileInventoryFactsDoNotMisclassifyTheSamePhysicalHardware()
    {
        var before = SessionWithSamples(Enumerable.Range(1, 10).Select(value => (double)value).ToArray());
        var cpu = new HardwareComponent("cpu.0", "CPU", "Intel Core i7", [
            new("NumberOfCores", "8", "WMI"), new("Uptime", "100", "WMI"),
            new("FreeSpace", "512000", "WMI"), new("CurrentClockSpeed", "3000", "WMI"),
            new("Tempo de atividade", "100", "WMI"), new("Gateway", "192.168.1.1", "Network"),
            new("Arquivo de paginação", @"C:\pagefile.sys", "WMI")]);
        before = before with { Context = before.Context with { HardwareFingerprint = null },
            Inventory = before.Inventory with { Components = [cpu] } };
        var after = before with { Id = Guid.NewGuid(), Inventory = before.Inventory with
        {
            Components = [cpu with { Facts = cpu.Facts.Select(fact => fact.Name == "NumberOfCores" ? fact : fact with { Value = "valor volátil diferente" }).ToArray() }]
        } };
        var compared = SessionComparer.Compare(before, after);
        Assert.True(compared.ConditionsComparable);
        Assert.DoesNotContain(compared.Warnings, warning => warning.Contains("hardware", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DriverAndPowerConfigurationDifferencesProduceASeparateConditionWarning()
    {
        var before = SessionWithSamples(Enumerable.Range(1, 10).Select(value => (double)value).ToArray());
        var gpu = new HardwareComponent("gpu.0", "GPU", "NVIDIA", [new("DriverVersion", "1.0", "WMI"), new("VRAM total bytes", "12884901888", "NVML")]);
        var power = new HardwareComponent("power", "Sistema", "Energia", [new("Plano ativo GUID", "plano-a", "PowerGetActiveScheme")]);
        before = before with { Context = before.Context with { HardwareFingerprint = null },
            Inventory = before.Inventory with { Components = [gpu, power] } };
        var after = before with { Id = Guid.NewGuid(), Inventory = before.Inventory with
        {
            Components = [gpu with { Facts = [gpu.Facts[0] with { Value = "2.0" }, gpu.Facts[1]] },
                power with { Facts = [power.Facts[0] with { Value = "plano-b" }] }]
        } };
        var compared = SessionComparer.Compare(before, after);
        Assert.False(compared.ConditionsComparable);
        Assert.DoesNotContain(compared.Warnings, warning => warning.StartsWith("O inventário de hardware", StringComparison.Ordinal));
        Assert.Contains(compared.Warnings, warning => warning.Contains("configuração reportada", StringComparison.Ordinal));
    }

    [Fact]
    public void LoggerRotatesWithinBoundAndNeverCopiesRawExceptionMessages()
    {
        using var directory = new TemporaryDirectory();
        var logger = new JsonFileLogger(directory.Path, maxFileBytes: 65_536, retainedFiles: 3);
        for (var index = 0; index < 180; index++)
            logger.Log(LogLevel.Error, "test.failure", new string('x', 2000) + " token=do-not-store",
                new InvalidOperationException("arbitrary-unlabeled-secret-do-not-store"));
        var files = Directory.GetFiles(directory.Path, "*.jsonl");
        Assert.InRange(files.Length, 1, 3);
        Assert.All(files, file => Assert.True(new FileInfo(file).Length <= 65_536));
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("do-not-store", text);
            foreach (var line in File.ReadLines(file))
            {
                using var entry = JsonDocument.Parse(line);
                Assert.Equal("InvalidOperationException", entry.RootElement.GetProperty("exceptionType").GetString());
            }
        }
        Assert.DoesNotContain("arbitrary", JsonFileLogger.UserMessage(new InvalidOperationException("arbitrary-secret")));
    }

    private static DiagnosticSession Session()
    {
        var time = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(-3));
        return new(Guid.NewGuid(), "office-machine / sessão", time, time.AddMinutes(1),
            new("repouso", "Passivo", .75, "host=office-machine IP=192.168.4.51", "hardware-fingerprint-private"),
            new(time, [new("private-drive-id", "Storage", "Disco de teste", [
                new("SerialNumber", "SERIAL-PRIVATE-123", "WMI"), new("HostName", "office-machine", "WMI"),
                new("IPv6Address", "2001:db8::11", "Network"), new("MAC", "00:11:22:33:44:55", "Network"),
                new("Model", "Modelo de teste", "WMI")])], [new("Telemetry", Availability.Available, "Pronto")]),
            [new("disk.latency", "Storage", 12.125, "ms", @"C:\Users\Alice\trace.etl", "Média do intervalo", time,
                TimeSpan.FromTicks(7_500_001), Kind: MeasurementKind.Derived, Aggregation: Aggregation.IntervalAverage,
                Limitation: "office-machine 192.168.4.51"),
             MetricSample.Missing("gpu.temperature", "GPU", "°C", "NVML", time, Availability.ToolMissing, "Sem ferramenta")],
            [new("disk.latency", "Storage", "Latência", "Observar mais amostras", FindingCategory.PossibleProblem,
                Severity.Warning, Confidence.Low, [new("Média 12.125", "Coleta", "disk.latency", 12.125, "ms", time)],
                "Medir novamente", "Comparar", "Sem alteração")]);
    }

    [Fact]
    public void NormalSamplingJitterKeepsEquivalentConditionsComparable()
    {
        var before = SessionWithSamples(Enumerable.Range(1, 20).Select(i => (double)i).ToArray());
        var after = before with { Id = Guid.NewGuid(), Samples = before.Samples.Select((sample, i) => sample with
            { SamplingInterval = TimeSpan.FromSeconds(.75 + i % 3 * .01) }).ToArray() };
        var comparison = SessionComparer.Compare(before, after);
        Assert.True(comparison.ConditionsComparable);
        Assert.DoesNotContain(comparison.Warnings, w => w.Contains("intervalos efetivos", StringComparison.Ordinal));
    }

    [Fact]
    public void SignificantSamplingChangesAreReported()
    {
        var before = SessionWithSamples(Enumerable.Range(1, 20).Select(i => (double)i).ToArray());
        var after = before with { Id = Guid.NewGuid(), Samples = before.Samples.Select((sample, i) => sample with
            { SamplingInterval = TimeSpan.FromSeconds(i < 10 ? .75 : 5) }).ToArray() };
        var comparison = SessionComparer.Compare(before, after);
        Assert.False(comparison.ConditionsComparable);
        Assert.Contains(comparison.Warnings, w => w.Contains("intervalos efetivos", StringComparison.Ordinal));
    }

    [Fact]
    public void AccumulatedCounterTotalsAreNotComparedAsPerformanceImprovements()
    {
        var before = SessionWithSamples([10, 15]) with
        {
            Samples = SessionWithSamples([10, 15]).Samples.Select(s => s with { Aggregation = Aggregation.Cumulative }).ToArray()
        };
        var after = before with { Id = Guid.NewGuid(), Samples = before.Samples.Select(s => s with { Value = s.Value + 1000 }).ToArray() };
        var metric = Assert.Single(SessionComparer.Compare(before, after).Metrics);
        Assert.False(metric.Comparable);
        Assert.Null(metric.MeanDelta);
        Assert.Null(metric.P95Delta);
        Assert.Contains("Totais acumulados", metric.Limitation);
    }

    [Fact]
    public async Task SqliteBusyWaitDoesNotBlockTheCallingThread()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "history.db");
        var store = new SQLiteHistoryStore(path);
        await store.InitializeAsync();
        await using var locker = new SqliteConnection($"Data Source={path};Pooling=False");
        await locker.OpenAsync();
        await using var command = locker.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE;";
        await command.ExecuteNonQueryAsync();
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { returned.SetResult(store.SaveSessionAsync(Session())); }
            catch (Exception error) { returned.SetException(error); }
        });
        thread.Start();
        Task? pendingSave = null;
        try { pendingSave = await returned.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally
        {
            command.CommandText = "ROLLBACK;";
            await command.ExecuteNonQueryAsync();
            thread.Join(TimeSpan.FromSeconds(5));
        }
        Assert.NotNull(pendingSave);
        await pendingSave.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(await store.ListSessionsAsync());
    }

    private static DiagnosticSession SessionWithSamples(double[] values)
    {
        var session = Session();
        return session with
        {
            Samples = values.Select((value, index) => new MetricSample("cpu.load", "CPU", value, "%", "PDH",
                "Contador do intervalo", session.StartedAt.AddSeconds(index), TimeSpan.FromSeconds(.75))).ToArray()
        };
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pc-analyzer-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
    }
}
