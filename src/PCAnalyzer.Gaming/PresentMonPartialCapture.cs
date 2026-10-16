using PCAnalyzer.Core;

namespace PCAnalyzer.Gaming;

/// <summary>Reads an already preserved CSV after cancellation/failure; never restarts a capture or a process.</summary>
public static class PresentMonPartialCapture
{
    public static Task<PresentMonCaptureResult> ReadCancelledAsync(PresentMonCaptureRequest request,
        PresentMonToolInspection? tool, string detail) => ReadAsync(request, tool, detail, true, null, null, null);

    public static Task<PresentMonCaptureResult> ReadFailureAsync(PresentMonCaptureRequest request,
        PresentMonToolInspection? tool, string detail, Availability failureStatus = Availability.ReadError,
        int? exitCode = null, string? processOutput = null) => ReadAsync(request, tool, detail, false, failureStatus, exitCode, processOutput);

    private static async Task<PresentMonCaptureResult> ReadAsync(PresentMonCaptureRequest request,
        PresentMonToolInspection? tool, string detail, bool cancelled, Availability? failureStatus, int? exitCode, string? processOutput)
    {
        var limitation = (cancelled ? "Captura cancelada" : "Captura interrompida") + ": estatísticas descrevem somente os frames retidos no CSV parcial.";
        if (!File.Exists(request.OutputCsvPath))
            return new(failureStatus ?? Availability.InsufficientData, detail + " Nenhum CSV parcial foi produzido.", request.OutputCsvPath, tool,
                ExitCode: exitCode, ProcessOutput: processOutput, Cancelled: cancelled);
        try
        {
            // Cancellation must remain responsive even when an external tool left a large CSV.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var options = (request.ParseOptions ?? new PresentMonParseOptions()) with
            { ProcessId = request.ProcessId, MaximumFrames = Math.Min(request.ParseOptions?.MaximumFrames ?? 1_000_000, 100_000) };
            var parsed = await new PresentMonCsvParser().ImportFileAsync(request.OutputCsvPath, options, timeout.Token).ConfigureAwait(false);
            var import = parsed with
            {
                Complete = false,
                Cancelled = cancelled,
                Warnings = parsed.Warnings.Concat([limitation, "Recuperação parcial limitada a 100.000 frames e cinco segundos; o arquivo CSV original permanece preservado."]).ToArray(),
                Streams = parsed.Streams.Select(stream => stream with { Limitations = stream.Limitations.Concat([limitation]).ToArray() }).ToArray()
            };
            var status = failureStatus ?? (import.ErrorDetail is not null ? Availability.ReadError :
                import.Streams.Any(s => s.ApplicationStatistics.SampleCount > 0) ? Availability.Available : Availability.InsufficientData);
            return new(status, detail + " " + limitation, request.OutputCsvPath, tool, import, exitCode, processOutput, cancelled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PresentMonFormatException or OperationCanceledException)
        {
            return new(Availability.ReadError, detail + " O CSV foi preservado, mas não foi possível ler o resultado parcial: " + ex.Message,
                request.OutputCsvPath, tool, ExitCode: exitCode, ProcessOutput: processOutput, Cancelled: cancelled);
        }
    }
}
