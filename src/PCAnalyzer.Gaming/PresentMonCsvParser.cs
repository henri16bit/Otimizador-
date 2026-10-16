using System.Globalization;
using System.Text;

namespace PCAnalyzer.Gaming;

public sealed class PresentMonFormatException(string message) : FormatException(message);

/// <summary>Streams CSV records; stores only the configured, bounded number of frame samples.</summary>
public sealed class PresentMonCsvParser
{
    public Task<PresentMonImportResult> ImportFileAsync(string path, PresentMonParseOptions? options = null,
        CancellationToken cancellationToken = default) => Task.Run(
            () => ImportFileCoreAsync(path, options, cancellationToken), cancellationToken);

    private async Task<PresentMonImportResult> ImportFileCoreAsync(string path, PresentMonParseOptions? options,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await ParseAsync(reader, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PresentMonImportResult> ParseAsync(TextReader reader, PresentMonParseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        options ??= new PresentMonParseOptions();
        ValidateOptions(options);
        var records = new CsvRecordReader(reader, options.MaximumRecordCharacters);
        var headerRecord = await records.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (headerRecord is null) throw new PresentMonFormatException("O CSV está vazio.");
        var headers = SplitCsv(headerRecord);
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++)
            if (!columns.TryAdd(headers[i].Trim().TrimStart('\uFEFF'), i))
                throw new PresentMonFormatException($"Coluna duplicada: {headers[i]}.");
        foreach (var required in new[] { "Application", "ProcessID", "SwapChainAddress" })
            if (!columns.ContainsKey(required)) throw new PresentMonFormatException($"Coluna obrigatória ausente: {required}.");

        var schema = SelectSchema(columns, options);
        var states = new Dictionary<StreamKey, StreamBuilder>();
        var streams = new List<StreamBuilder>();
        var warnings = new List<string>();
        long read = 0, invalid = 0, filtered = 0;
        var stored = 0;
        var complete = true;
        var cancelled = false;
        string? errorDetail = null;
        string? record;
        while ((record = await ReadNextRecordAsync().ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(record)) continue;
            read++;
            List<string> values;
            try { values = SplitCsv(record); }
            catch (PresentMonFormatException ex) { Reject(ex.Message); continue; }
            if (values.Count != headers.Count) { Reject("Número de campos diferente do cabeçalho."); continue; }
            string Get(string name) => columns.TryGetValue(name, out var index) ? values[index].Trim() : "";
            var application = Get("Application");
            var swapChain = NormalizeSwapChain(Get("SwapChainAddress"));
            if (application.Length == 0 || !int.TryParse(Get("ProcessID"), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0 || swapChain is null)
            { Reject("Aplicação, PID ou endereço de swap chain inválido."); continue; }
            if (options.ProcessId is { } selectedPid && pid != selectedPid ||
                options.Application is { } selectedApp && !application.Equals(selectedApp, StringComparison.OrdinalIgnoreCase) ||
                options.SwapChainAddress is { } selectedChain && !swapChain.Equals(NormalizeSwapChain(selectedChain), StringComparison.OrdinalIgnoreCase))
            { filtered++; continue; }
            if (!TryTimestamp(Get(schema.TimestampColumn), schema, options, out var timestamp))
            { Reject($"Timestamp inválido em {schema.TimestampColumn}; nenhum valor ou unidade foi presumido."); continue; }

            bool? dropped = null;
            if (columns.ContainsKey("Dropped"))
            {
                var raw = Get("Dropped");
                dropped = raw.Equals("1", StringComparison.Ordinal) || raw.Equals("true", StringComparison.OrdinalIgnoreCase) ? true :
                    raw.Equals("0", StringComparison.Ordinal) || raw.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : null;
                if (!dropped.HasValue) { Reject("Dropped precisa ser 0, 1, true ou false."); continue; }
            }
            else if (columns.ContainsKey("DisplayedTime"))
            {
                var raw = Get("DisplayedTime");
                if (IsMissing(raw)) dropped = true;
                else if (TryNumber(raw, out var displayed) && displayed >= 0) dropped = displayed == 0;
                else { Reject("DisplayedTime inválido."); continue; }
            }

            var duration = ReadDuration(Get, schema.DurationColumn);
            if (schema.SumCpuDuration)
            {
                var busy = ReadNonnegative(Get, schema.CpuBusyColumn);
                var wait = ReadNonnegative(Get, schema.CpuWaitColumn);
                duration = busy.HasValue && wait.HasValue && busy.Value + wait.Value > 0 && double.IsFinite(busy.Value + wait.Value)
                    ? busy.Value + wait.Value : null;
            }
            var displayDuration = columns.ContainsKey("DisplayedTime") ? ReadDuration(Get, "DisplayedTime") :
                columns.ContainsKey("MsBetweenDisplayChange") ? ReadDuration(Get, "MsBetweenDisplayChange") : null;
            if (dropped == true) displayDuration = null;
            if (!duration.HasValue && !IsMissing(Get(schema.DurationColumn)))
                Warn($"Registro {read}: duração ausente, zero ou inválida; frame preservado, intervalo excluído das estatísticas.");
            var frameType = columns.ContainsKey("FrameType") ? Get("FrameType") : null;
            if (string.IsNullOrWhiteSpace(frameType)) frameType = null;
            var key = new StreamKey(application.ToUpperInvariant(), pid, swapChain, frameType?.ToUpperInvariant());
            if (!states.TryGetValue(key, out var state))
            {
                if (streams.Count >= options.MaximumStreams) { complete = false; Warn("Limite de streams atingido; importação parcial."); break; }
                state = new StreamBuilder(new FrameStreamIdentity(application, pid, swapChain, FrameType: frameType));
                states.Add(key, state);
                streams.Add(state);
            }
            else if (state.Frames.Count > 0 && timestamp < state.Frames[^1].TimestampMilliseconds)
            {
                if (streams.Count >= options.MaximumStreams) { complete = false; Warn("Limite de streams atingido durante reset de timestamp; importação parcial."); break; }
                state = new StreamBuilder(state.Identity with { Segment = state.Identity.Segment + 1 });
                states[key] = state;
                streams.Add(state);
                Warn($"PID {pid}, swap chain {swapChain}: timestamp retrocedeu; segmento separado para evitar misturar capturas/processos reutilizados.");
            }
            if (stored >= options.MaximumFrames) { complete = false; Warn("Limite de frames atingido; importação parcial. Ajuste MaximumFrames conscientemente."); break; }
            state.Frames.Add(new FrameSample(read, timestamp, duration, dropped, displayDuration,
                ReadNonnegative(Get, schema.CpuBusyColumn), ReadNonnegative(Get, schema.CpuWaitColumn),
                ReadNonnegative(Get, columns.ContainsKey("GPUBusy") ? "GPUBusy" : "MsGPUBusy"),
                ReadNonnegative(Get, columns.ContainsKey("DisplayLatency") ? "DisplayLatency" : "MsUntilDisplayed"),
                ReadNonnegative(Get, columns.ContainsKey("AllInputToPhotonLatency") ? "AllInputToPhotonLatency" : "MsAllInputToPhotonLatency"), frameType));
            stored++;
        }
        if (invalid > 100) warnings.Add($"{invalid} registros inválidos no total; detalhes limitados aos primeiros 100 avisos.");
        var output = streams.Select(state => BuildStream(state, schema, complete)).ToArray();
        return new PresentMonImportResult(output, read, invalid, filtered, complete, warnings, cancelled, errorDetail);

        async Task<string?> ReadNextRecordAsync()
        {
            try { return await records.ReadAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                complete = false;
                cancelled = true;
                warnings.Add("Leitura cancelada: frames anteriores ao cancelamento foram preservados; as estatísticas descrevem somente esse trecho.");
                return null;
            }
            catch (Exception ex) when (stored > 0 && ex is PresentMonFormatException or IOException or UnauthorizedAccessException)
            {
                complete = false;
                errorDetail = ex.Message;
                warnings.Add("Leitura interrompida; frames válidos anteriores ao erro foram preservados. " + ex.Message);
                return null;
            }
        }

        void Warn(string message) { if (warnings.Count < 100) warnings.Add(message); }
        void Reject(string message) { invalid++; Warn($"Registro {read}: {message}"); }
    }

    private static FrameStream BuildStream(StreamBuilder state, Schema schema, bool complete)
    {
        var frames = state.Frames.ToArray();
        var span = frames.Length > 1 ? (frames[^1].TimestampMilliseconds - frames[0].TimestampMilliseconds) / 1000 : 0;
        var limitations = new List<string>
        {
            "Cadência da aplicação inclui intervalos válidos de presents descartados. FPS exibido usa somente intervalos reais de exibição; os dois valores medem eventos diferentes.",
            "ETW não mede latência de rede, tempo físico do monitor nem sozinho prova um gargalo de CPU ou GPU."
        };
        if (frames.Any(x => !x.Dropped.HasValue)) limitations.Add("O CSV não fornece estado de exibição para todos os frames; dropped desconhecido permanece separado.");
        if (frames.All(x => !x.DisplayedTimeMilliseconds.HasValue)) limitations.Add("Sem intervalos válidos de exibição; FPS exibido indisponível.");
        if (!complete) limitations.Add("Importação parcial: estatísticas descrevem somente os frames retidos.");
        if (schema.TimestampBasis.Contains("sem época", StringComparison.Ordinal)) limitations.Add("Timestamp relativo/QPC sem época civil: não é possível alinhar automaticamente com telemetria de outra captura.");
        return new FrameStream(state.Identity, schema.DurationSource, schema.TimestampColumn, schema.TimestampBasis, frames,
            frames.Count(x => x.Dropped == true), frames.Count(x => !x.Dropped.HasValue),
            FrameStatisticsCalculator.Calculate(frames.Where(x => x.FrameTimeMilliseconds.HasValue).Select(x => x.FrameTimeMilliseconds!.Value), span),
            FrameStatisticsCalculator.Calculate(frames.Where(x => x.Dropped != true && x.DisplayedTimeMilliseconds.HasValue).Select(x => x.DisplayedTimeMilliseconds!.Value), span), limitations);
    }

    private static Schema SelectSchema(Dictionary<string, int> columns, PresentMonParseOptions options)
    {
        var cpuBusy = columns.ContainsKey("CPUBusy") ? "CPUBusy" : "MsCPUBusy";
        var cpuWait = columns.ContainsKey("CPUWait") ? "CPUWait" : "MsCPUWait";
        string duration;
        var sum = false;
        if (columns.ContainsKey("FrameTime")) duration = "FrameTime";
        else if (columns.ContainsKey("MsBetweenAppStart")) duration = "MsBetweenAppStart";
        else if (columns.ContainsKey("MsBetweenPresents")) duration = "MsBetweenPresents";
        else if (columns.ContainsKey(cpuBusy) && columns.ContainsKey(cpuWait)) { duration = cpuBusy; sum = true; }
        else throw new PresentMonFormatException("Não há duração reconhecida em milissegundos: FrameTime, MsBetweenAppStart, MsBetweenPresents ou CPUBusy + CPUWait.");
        var candidates = duration.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase)
            ? new[] { "TimeInSeconds", "TimeInMs", "TimeInQPC", "TimeInDateTime", "CPUStartTime", "CPUStartTimeInMs", "CPUStartTimeInSeconds", "CPUStartQPCTime", "CPUStartQPCTimeInMs", "CPUStartQPC", "CPUStartDateTime" }
            : new[] { "CPUStartTime", "CPUStartTimeInMs", "CPUStartTimeInSeconds", "CPUStartQPCTime", "CPUStartQPCTimeInMs", "CPUStartQPC", "CPUStartDateTime", "TimeInSeconds", "TimeInMs", "TimeInQPC", "TimeInDateTime" };
        var timestamp = candidates.FirstOrDefault(columns.ContainsKey) ?? throw new PresentMonFormatException("Timestamp PresentMon reconhecido ausente.");
        var unit = timestamp.Contains("DateTime", StringComparison.Ordinal) ? TimestampUnit.DateTime :
            timestamp is "CPUStartQPC" or "TimeInQPC" ? TimestampUnit.Qpc :
            timestamp.Contains("Seconds", StringComparison.Ordinal) ? TimestampUnit.Seconds : TimestampUnit.Milliseconds;
        if (unit == TimestampUnit.Qpc && !options.QpcFrequencyHz.HasValue)
            throw new PresentMonFormatException("CSV em ticks QPC exige QpcFrequencyHz real da captura; ticks não são milissegundos.");
        if (unit == TimestampUnit.DateTime && !options.DateTimeUtcOffset.HasValue)
            throw new PresentMonFormatException("CSV com data/hora local exige DateTimeUtcOffset da captura; o fuso da máquina atual não será presumido.");
        return new Schema(duration, sum ? $"{cpuBusy} + {cpuWait} (ms)" : $"{duration} (ms)", timestamp, unit,
            unit == TimestampUnit.DateTime ? "Data/hora local com offset explicitamente informado, convertida para UTC" :
            unit == TimestampUnit.Qpc || timestamp.Contains("QPC", StringComparison.Ordinal) ? "QPC convertido para ms, sem época civil" : "Tempo da captura em ms, sem época civil",
            sum, cpuBusy, cpuWait);
    }

    private static bool TryTimestamp(string text, Schema schema, PresentMonParseOptions options, out double milliseconds)
    {
        milliseconds = 0;
        if (schema.TimestampUnit == TimestampUnit.DateTime)
        {
            if (!options.DateTimeUtcOffset.HasValue) return false;
            // PresentMon writes up to nine subsecond digits; .NET ticks preserve seven.
            var dot = text.IndexOf('.');
            if (dot >= 0 && text.Length - dot - 1 > 7 && text[(dot + 1)..].All(char.IsDigit)) text = text[..(dot + 8)];
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) || local.Kind != DateTimeKind.Unspecified) return false;
            try { milliseconds = new DateTimeOffset(local, options.DateTimeUtcOffset.Value).ToUnixTimeMilliseconds(); return true; }
            catch (ArgumentException) { return false; }
        }
        if (!TryNumber(text, out var value) || value < 0) return false;
        milliseconds = schema.TimestampUnit switch
        {
            TimestampUnit.Seconds => value * 1000,
            TimestampUnit.Qpc => value / options.QpcFrequencyHz!.Value * 1000,
            _ => value
        };
        return double.IsFinite(milliseconds);
    }

    private static double? ReadDuration(Func<string, string> get, string name) =>
        TryNumber(get(name), out var value) && value > 0 ? value : null;
    private static double? ReadNonnegative(Func<string, string> get, string name) =>
        TryNumber(get(name), out var value) && value >= 0 ? value : null;
    private static bool TryNumber(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    private static bool IsMissing(string text) => string.IsNullOrWhiteSpace(text) || text.Equals("NA", StringComparison.OrdinalIgnoreCase) || text.Equals("N/A", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeSwapChain(string value)
    {
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        return ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address)
            ? $"0x{address:X}" : null;
    }

    private static void ValidateOptions(PresentMonParseOptions options)
    {
        if (options.ProcessId is <= 0) throw new ArgumentOutOfRangeException(nameof(options.ProcessId));
        if (options.MaximumFrames is < 1 or > 10_000_000 || options.MaximumStreams is < 1 or > 4096 || options.MaximumRecordCharacters is < 128 or > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(options), "Limites de importação fora da faixa suportada.");
        if (options.QpcFrequencyHz is { } frequency && (!double.IsFinite(frequency) || frequency <= 0)) throw new ArgumentOutOfRangeException(nameof(options.QpcFrequencyHz));
        if (options.SwapChainAddress is { } address && NormalizeSwapChain(address) is null) throw new ArgumentException("Filtro de swap chain inválido.", nameof(options));
        if (options.DateTimeUtcOffset is { } offset && (Math.Abs(offset.TotalHours) > 14 || offset.Ticks % TimeSpan.TicksPerMinute != 0))
            throw new ArgumentOutOfRangeException(nameof(options.DateTimeUtcOffset));
    }

    private sealed class CsvRecordReader(TextReader reader, int maximumCharacters)
    {
        private readonly char[] _buffer = new char[4096];
        private int _position, _count;
        private bool _skipLineFeed;

        public async Task<string?> ReadAsync(CancellationToken cancellationToken)
        {
            var record = new StringBuilder(Math.Min(maximumCharacters, 1024));
            var quoted = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = await NextAsync(cancellationToken).ConfigureAwait(false);
                if (next is null)
                {
                    if (quoted) throw new PresentMonFormatException("Campo CSV entre aspas não foi fechado.");
                    return record.Length == 0 ? null : record.ToString();
                }
                var c = next.Value;
                if (_skipLineFeed)
                {
                    _skipLineFeed = false;
                    if (c == '\n') continue;
                }
                if (!quoted && c is '\r' or '\n')
                {
                    _skipLineFeed = c == '\r';
                    return record.ToString();
                }
                if (record.Length >= maximumCharacters)
                    throw new PresentMonFormatException("Registro CSV ultrapassa o limite de tamanho.");
                record.Append(c);
                // Each quote toggles parity. Adjacent escaped quotes cancel each other, including across buffer boundaries.
                if (c == '"') quoted = !quoted;
            }
        }

        private async ValueTask<char?> NextAsync(CancellationToken cancellationToken)
        {
            if (_position == _count)
            {
                _count = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _position = 0;
                if (_count == 0) return null;
            }
            return _buffer[_position++];
        }
    }

    private static List<string> SplitCsv(string record)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        var quoteClosed = false;
        for (var i = 0; i < record.Length; i++)
        {
            var c = record[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < record.Length && record[i + 1] == '"') { value.Append('"'); i++; }
                    else { quoted = false; quoteClosed = true; }
                }
                else value.Append(c);
            }
            else if (c == ',') { values.Add(value.ToString()); value.Clear(); quoteClosed = false; }
            else if (c == '"' && value.Length == 0 && !quoteClosed) quoted = true;
            else if (c == '"' || quoteClosed && !char.IsWhiteSpace(c)) throw new PresentMonFormatException("Aspas CSV em posição inválida.");
            else value.Append(c);
        }
        if (quoted) throw new PresentMonFormatException("Campo CSV entre aspas não foi fechado.");
        values.Add(value.ToString());
        return values;
    }

    private enum TimestampUnit { Milliseconds, Seconds, Qpc, DateTime }
    private sealed record Schema(string DurationColumn, string DurationSource, string TimestampColumn,
        TimestampUnit TimestampUnit, string TimestampBasis, bool SumCpuDuration, string CpuBusyColumn, string CpuWaitColumn);
    private sealed record StreamKey(string Application, int ProcessId, string SwapChainAddress, string? FrameType);
    private sealed class StreamBuilder(FrameStreamIdentity identity)
    {
        public FrameStreamIdentity Identity { get; } = identity;
        public List<FrameSample> Frames { get; } = [];
    }
}
