using System.Text.Json;
using PCAnalyzer.Core;
using PCAnalyzer.Diagnostics;

if (!OperatingSystem.IsWindows() || args.Length != 2 || args[0] != "--request" ||
    !Guid.TryParseExact(args[1], "N", out var id)) return 2;
Directory.CreateDirectory(PrivilegedProtocol.RequestDirectory);
for (var directory = new DirectoryInfo(PrivilegedProtocol.RequestDirectory); directory is not null; directory = directory.Parent)
    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return 6;
var responsePath = PrivilegedProtocol.ResponsePath(id);
if (File.Exists(responsePath)) return 3; // Não repetir uma requisição já consumida.
FileStream input;
try { input = new FileStream(PrivilegedProtocol.RequestPath(id), FileMode.Open, FileAccess.Read, FileShare.None); }
catch (IOException) { return 4; } // Um consumidor concorrente não publica resposta sobre a operação do proprietário.
catch (UnauthorizedAccessException) { return 5; }
await using var requestLock = input;
PrivilegedResponse response;
try
{
    if (input.Length > 16384) throw new InvalidOperationException("Requisição excede 16 KiB.");
    var request = await JsonSerializer.DeserializeAsync<PrivilegedRequest>(input) ?? throw new InvalidOperationException("Requisição vazia.");
    if (DateTimeOffset.UtcNow - request.CreatedAt > TimeSpan.FromMinutes(5) || request.CreatedAt > DateTimeOffset.UtcNow.AddSeconds(30))
        throw new InvalidOperationException("Requisição expirada.");
    if (File.Exists(PrivilegedProtocol.CancellationPath(id))) throw new OperationCanceledException("Cancelado antes da execução.");
    response = await PrivilegedProtocol.DispatchReadOnlyAsync(request, async capture =>
    {
        using var cancellation = new CancellationTokenSource();
        using var watchdog = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            try
            {
                while (!watchdog.IsCancellationRequested)
                {
                    if (File.Exists(PrivilegedProtocol.CancellationPath(id))) { cancellation.Cancel(); break; }
                    await Task.Delay(200, watchdog.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            var result = await new EtwTraceService().CaptureAsync(new EtwCaptureOptions(TimeSpan.FromSeconds(capture.DurationSeconds)), cancellation.Token);
            return new(true, result.Detail, JsonSerializer.Serialize(result));
        }
        finally { watchdog.Cancel(); await watch; }
    });
}
catch (Exception ex) { response = new(false, ex.GetType().Name + ": " + ex.Message); }
var temporary = responsePath + ".tmp";
await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
{
    await JsonSerializer.SerializeAsync(output, response);
    await output.FlushAsync();
    output.Flush(flushToDisk: true);
}
File.Move(temporary, responsePath, overwrite: false);
return response.Success ? 0 : 1;
