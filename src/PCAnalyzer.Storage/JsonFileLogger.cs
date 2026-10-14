using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCAnalyzer.Storage;

public enum LogLevel { Information, Warning, Error }

/// <summary>Bounded local JSON-lines logs. Does not inspect process environment or exception Data.</summary>
public sealed class JsonFileLogger
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory;
    private readonly object _gate;
    private readonly int _maxFileBytes;
    private readonly int _retainedFiles;

    public JsonFileLogger(string directory, int maxFileBytes = 1_048_576, int retainedFiles = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (maxFileBytes is < 65_536 or > 10_485_760) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        if (retainedFiles is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(retainedFiles));
        _directory = Path.GetFullPath(directory);
        _gate = Gates.GetOrAdd(_directory, _ => new object());
        _maxFileBytes = maxFileBytes;
        _retainedFiles = retainedFiles;
    }

    public void Log(LogLevel level, string eventName, string message, Exception? exception = null)
    {
        var entry = new
        {
            timestamp = DateTimeOffset.UtcNow,
            level = level.ToString(),
            eventName = SafeText(eventName, 128),
            message = SafeText(message, 2048),
            exceptionType = exception?.GetType().Name,
            // Exception messages can contain unconstrained application data, connection strings or credentials.
            exceptionMessage = exception is null ? null : UserMessage(exception),
            exceptionCode = exception?.HResult,
            stack = exception is null ? null : SafeText(exception.StackTrace ?? "", 2048)
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n");
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "activity.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > _maxFileBytes)
                Rotate(path);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    public bool TryLog(LogLevel level, string eventName, string message, Exception? exception = null)
    {
        try { Log(level, eventName, message, exception); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Suitable for UI error text; raw exception messages belong only in sanitized logs.</summary>
    public static string UserMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            OperationCanceledException => "A operação foi cancelada.",
            UnauthorizedAccessException => "Permissão insuficiente para concluir a operação.",
            IOException => "Não foi possível acessar o arquivo local. Verifique o espaço e as permissões.",
            JsonException or InvalidDataException => "Os dados locais não puderam ser lidos. Consulte o registro de diagnóstico.",
            _ => "A operação não foi concluída. Consulte o registro de diagnóstico para detalhes."
        };
    }

    private void Rotate(string path)
    {
        if (_retainedFiles == 1) { File.Delete(path); return; }
        var oldest = Path.Combine(_directory, $"activity.{_retainedFiles - 1}.jsonl");
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = _retainedFiles - 2; index >= 1; index--)
        {
            var source = Path.Combine(_directory, $"activity.{index}.jsonl");
            if (File.Exists(source)) File.Move(source, Path.Combine(_directory, $"activity.{index + 1}.jsonl"), overwrite: true);
        }
        File.Move(path, Path.Combine(_directory, "activity.1.jsonl"), overwrite: true);
    }

    private static string SafeText(string? value, int maximum)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = value.Length > 32768 ? value[..32768] : value;
        text = Regex.Replace(text, @"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|authorization|cookie)\b\s*[:=]\s*(?:""[^""]*""|'[^']*'|\S+)", "$1=[redigido]", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"(?i)\bBearer\s+[A-Za-z0-9._~+/-]+=*", "Bearer [redigido]", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"(?i)\b(?:gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9_-]{16,})\b", "[credencial redigida]", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"(?:[A-Za-z]:\\|\\\\)[^\r\n""<>|]+", "[caminho redigido]", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"(?<![\w:])/(?:[^\s/]+/)+[^\s]*", "[caminho redigido]", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"\b(?:\d{1,3}\.){3}\d{1,3}\b|\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", "[endereço redigido]", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"(?i)(?<![\w:])(?:[0-9a-f]{0,4}:){2,}[0-9a-f:.%]+(?!\w)", "[endereço redigido]", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @"(?i)\b(?:https?|ftp)://[^\s<>""']+", "[URL redigida]", RegexOptions.None, TimeSpan.FromSeconds(1));
        return text.Length <= maximum ? text : text[..maximum] + "…";
    }
}
