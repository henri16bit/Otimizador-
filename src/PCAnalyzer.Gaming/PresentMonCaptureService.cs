using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing.Session;
using PCAnalyzer.Core;

namespace PCAnalyzer.Gaming;

/// <summary>Opt-in, bounded ETW capture with a PresentMon executable explicitly selected by the user.</summary>
public sealed partial class PresentMonCaptureService
{
    private static readonly string[] RequiredOptions = ["process_id", "output_file", "timed", "terminate_after_timed", "session_name", "terminate_existing_session"];

    public async Task<PresentMonToolInspection> InspectAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return new(executablePath, Availability.Unsupported, null, PresentMonCliStyle.Unsupported, "Captura PresentMon ao vivo exige Windows. A importação CSV funciona em outras plataformas.", []);
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath))
            return new(executablePath, Availability.ToolMissing, null, PresentMonCliStyle.Unsupported, "Selecione o caminho absoluto do executável PresentMon instalado por você.", []);
        if (!Path.GetExtension(executablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return new(executablePath, Availability.Unsupported, null, PresentMonCliStyle.Unsupported, "É necessário selecionar diretamente um executável .exe; scripts e comandos de shell não são executados.", []);
        try
        {
            // Hold the file against replacement/write throughout verification and the help probe.
            using var readLease = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var signature = PresentMonSignatureVerifier.Inspect(executablePath);
            if (signature.Status != PresentMonSignatureStatus.TrustedPublisher)
                return new(executablePath, Availability.Unsupported, null, PresentMonCliStyle.Unsupported, signature.Detail, [],
                    signature.Publisher, signature.Status);
            var probe = await RunAsync(executablePath, ["--help"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            if (probe.TimedOut)
                return new(executablePath, Availability.Unsupported, null, PresentMonCliStyle.Unsupported, "O executável não respondeu a --help no prazo; captura recusada.", [], signature.Publisher, signature.Status);
            var version = VersionPattern().Match(probe.Output);
            if (!version.Success)
                return new(executablePath, Availability.Unsupported, null, PresentMonCliStyle.Unsupported, "A saída de ajuda não identifica uma versão PresentMon Console. Use uma versão oficial compatível ou importe o CSV manualmente.", [], signature.Publisher, signature.Status);
            var found = OptionPattern().Matches(probe.Output).Select(x => x.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
            var modern = RequiredOptions.All(x => found.Contains("--" + x, StringComparer.Ordinal));
            var legacy = RequiredOptions.All(x => found.Contains("-" + x, StringComparer.Ordinal));
            var style = modern ? PresentMonCliStyle.Modern : legacy ? PresentMonCliStyle.Legacy : PresentMonCliStyle.Unsupported;
            return new(executablePath, style == PresentMonCliStyle.Unsupported ? Availability.Unsupported : Availability.Available,
                version.Groups[1].Value, style,
                style == PresentMonCliStyle.Unsupported ? "Ajuda incompleta ou CLI não reconhecida: faltam opções para captura limitada por PID, tempo e sessão exclusiva. Importe CSV em vez de adivinhar argumentos." :
                "Assinatura, editor, ajuda e versão inspecionados. A captura usa uma sessão ETW exclusiva e preserva frames descartados; nenhuma elevação automática ou mudança no Vanguard é solicitada.", found,
                signature.Publisher, signature.Status);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new(executablePath, ex is UnauthorizedAccessException ? Availability.PermissionRequired : Availability.ReadError,
                null, PresentMonCliStyle.Unsupported, $"Não foi possível inspecionar PresentMon: {ex.Message}", []);
        }
    }

    public async Task<PresentMonCaptureResult> CaptureAsync(PresentMonCaptureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            // Keep the same file immutable from signature/help inspection through capture and
            // session cleanup. A second process cannot replace it between the verified launches.
            using var readLease = OperatingSystem.IsWindows() && Path.IsPathFullyQualified(request.ExecutablePath) && File.Exists(request.ExecutablePath)
                ? new FileStream(request.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            return await CaptureCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(ex is UnauthorizedAccessException ? Availability.PermissionRequired : Availability.ReadError,
                "Não foi possível manter o executável selecionado disponível para verificação e captura. " + ex.Message,
                request.OutputCsvPath, null);
        }
    }

    private async Task<PresentMonCaptureResult> CaptureCoreAsync(PresentMonCaptureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(request.ProcessId));
        if (request.Duration < TimeSpan.FromSeconds(1) || request.Duration > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(request.Duration), "A captura deve durar entre 1 e 600 segundos.");
        if (string.IsNullOrWhiteSpace(request.OutputCsvPath) || !Path.IsPathFullyQualified(request.OutputCsvPath) ||
            !Path.GetExtension(request.OutputCsvPath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Forneça um caminho absoluto novo com extensão .csv.", nameof(request.OutputCsvPath));
        var inspection = await InspectAsync(request.ExecutablePath, cancellationToken).ConfigureAwait(false);
        if (inspection.Status != Availability.Available)
            return new(inspection.Status, inspection.Detail, request.OutputCsvPath, inspection);
        if (File.Exists(request.OutputCsvPath) || Directory.Exists(request.OutputCsvPath))
            return new(Availability.ReadError, "O destino já existe. Escolha um arquivo CSV novo para preservar capturas anteriores.", request.OutputCsvPath, inspection);
        var parent = Path.GetDirectoryName(request.OutputCsvPath)!;
        if (!Directory.Exists(parent))
            return new(Availability.ReadError, "A pasta de destino não existe. Selecione uma pasta existente e gravável.", request.OutputCsvPath, inspection);
        try
        {
            using var target = Process.GetProcessById(request.ProcessId);
            if (target.HasExited) return new(Availability.InsufficientData, "O processo selecionado já encerrou.", request.OutputCsvPath, inspection);
        }
        catch (ArgumentException)
        {
            return new(Availability.InsufficientData, "O PID selecionado não está em execução.", request.OutputCsvPath, inspection);
        }
        catch (Win32Exception ex)
        {
            return new(Availability.PermissionRequired, "Não foi possível consultar o processo selecionado: " + ex.Message, request.OutputCsvPath, inspection);
        }

        var prefix = inspection.CliStyle == PresentMonCliStyle.Modern ? "--" : "-";
        var sessionName = "PCAnalyzer-Gaming-" + Guid.NewGuid().ToString("N");
        var arguments = new List<string>
        {
            prefix + "process_id", request.ProcessId.ToString(CultureInfo.InvariantCulture),
            prefix + "output_file", request.OutputCsvPath,
            prefix + "timed", Math.Ceiling(request.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            prefix + "terminate_after_timed", prefix + "session_name", sessionName
        };
        if (inspection.SupportedOptions.Contains(prefix + "no_console_stats", StringComparer.Ordinal)) arguments.Add(prefix + "no_console_stats");
        if (inspection.SupportedOptions.Contains(prefix + "terminate_on_proc_exit", StringComparer.Ordinal)) arguments.Add(prefix + "terminate_on_proc_exit");
        try
        {
            var execution = await RunOwnedCaptureAsync(request.ExecutablePath, arguments, request.Duration + TimeSpan.FromSeconds(30),
                prefix, sessionName, cancellationToken).ConfigureAwait(false);
            if (execution.TimedOut)
                return await PresentMonPartialCapture.ReadFailureAsync(request, inspection,
                    "PresentMon excedeu o prazo. O subprocesso iniciado pelo aplicativo foi encerrado e a ausência de sua sessão ETW exclusiva foi verificada.",
                    exitCode: execution.ExitCode, processOutput: execution.Output).ConfigureAwait(false);
            if (execution.ExitCode != 0 || !File.Exists(request.OutputCsvPath))
            {
                var denied = PermissionPattern().IsMatch(execution.Output);
                return await PresentMonPartialCapture.ReadFailureAsync(request, inspection,
                    denied ? "PresentMon informou falta de permissão para ETW. Execute uma nova captura com as permissões necessárias; nenhuma alteração de segurança foi aplicada." : "PresentMon não concluiu a captura ou não produziu o CSV solicitado. Consulte a saída do subprocesso.",
                    denied ? Availability.PermissionRequired : Availability.ReadError, execution.ExitCode, execution.Output).ConfigureAwait(false);
            }
            var options = (request.ParseOptions ?? new PresentMonParseOptions()) with { ProcessId = request.ProcessId };
            var import = await new PresentMonCsvParser().ImportFileAsync(request.OutputCsvPath, options, cancellationToken).ConfigureAwait(false);
            var status = import.ErrorDetail is not null ? Availability.ReadError :
                import.Streams.Any(x => x.ApplicationStatistics.SampleCount > 0) ? Availability.Available : Availability.InsufficientData;
            return new(status, import.Cancelled ? "Leitura do CSV cancelada; frames já lidos foram preservados." :
                import.ErrorDetail is not null ? "O CSV foi lido parcialmente; frames anteriores ao erro foram preservados. " + import.ErrorDetail :
                !import.Complete ? "Captura encerrada; a importação atingiu seu limite. Estatísticas descrevem os frames retidos." :
                status == Availability.Available ? "Captura concluída. Estatísticas de cada swap chain e tipo de frame permanecem separadas." :
                "Captura concluída sem intervalos válidos do PID selecionado. Confirme que o jogo estava renderizando e repita a coleta.", request.OutputCsvPath, inspection, import, execution.ExitCode, execution.Output, import.Cancelled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await PresentMonPartialCapture.ReadCancelledAsync(request, inspection,
                "Captura cancelada; o subprocesso próprio foi encerrado e sua sessão ETW exclusiva foi verificada.").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or PresentMonFormatException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                var partial = await PresentMonPartialCapture.ReadCancelledAsync(request, inspection,
                    "Captura cancelada com erro de leitura ou encerramento: " + ex.Message).ConfigureAwait(false);
                return partial with { Status = Availability.ReadError };
            }
            return await PresentMonPartialCapture.ReadFailureAsync(request, inspection, $"Falha na captura/importação: {ex.Message}",
                ex is UnauthorizedAccessException ? Availability.PermissionRequired : Availability.ReadError).ConfigureAwait(false);
        }
    }

    private static async Task<ExecutionResult> RunOwnedCaptureAsync(string executablePath, IEnumerable<string> arguments,
        TimeSpan timeout, string prefix, string sessionName, CancellationToken cancellationToken)
    {
        ExecutionResult? result = null;
        Exception? captureFailure = null;
        try { result = await RunAsync(executablePath, arguments, timeout, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { captureFailure = ex; }
        try
        {
            // Only this invocation's generated session name is ever queried or stopped.
            if (TraceEventSession.GetActiveSessionNames().Contains(sessionName, StringComparer.OrdinalIgnoreCase))
            {
                await RunAsync(executablePath, [prefix + "session_name", sessionName, prefix + "terminate_existing_session"],
                    TimeSpan.FromSeconds(8), CancellationToken.None).ConfigureAwait(false);
            }
            if (TraceEventSession.GetActiveSessionNames().Contains(sessionName, StringComparer.OrdinalIgnoreCase))
                throw new IOException("A sessão própria permanece ativa.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            throw new IOException($"Não foi possível verificar/encerrar a sessão ETW própria '{sessionName}'. O CSV parcial foi preservado. Encerre somente essa sessão usando o PresentMon selecionado com '{prefix}session_name {sessionName} {prefix}terminate_existing_session' e as permissões necessárias. Detalhe: {ex.Message}", ex);
        }
        if (captureFailure is not null) ExceptionDispatchInfo.Capture(captureFailure).Throw();
        return result!;
    }

    private static async Task<ExecutionResult> RunAsync(string executablePath, IEnumerable<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Não foi possível iniciar o subprocesso PresentMon.");
        var stdout = DrainBoundedAsync(process.StandardOutput);
        var stderr = DrainBoundedAsync(process.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var timedOut = false;
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (!timedOut) { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); }
        }
        var output = (await stdout.ConfigureAwait(false)) + Environment.NewLine + (await stderr.ConfigureAwait(false));
        return new ExecutionResult(process.ExitCode, output.Trim(), timedOut);
    }

    private static async Task<string> DrainBoundedAsync(StreamReader reader)
    {
        const int maximum = 16_384;
        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            if (output.Length < maximum) output.Append(buffer, 0, Math.Min(count, maximum - output.Length));
        return output.ToString();
    }

    private sealed record ExecutionResult(int ExitCode, string Output, bool TimedOut);
    [GeneratedRegex(@"\bPresentMon\s+v?([0-9]+\.[0-9]+(?:\.[0-9]+)?(?:[-+][A-Za-z0-9.]+)?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
    [GeneratedRegex(@"(?<![\w-])(--?[a-z][a-z0-9_]+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex OptionPattern();
    [GeneratedRegex(@"access\s+(?:is\s+)?denied|permission|privilege|administrator|acesso\s+negado", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PermissionPattern();
}
