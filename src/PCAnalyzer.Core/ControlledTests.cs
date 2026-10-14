using System.Diagnostics;
using System.Security.Cryptography;

namespace PCAnalyzer.Core;

public sealed record ControlledTestResult(string Name, TimeSpan Duration, long Operations, long Bytes,
    double? ThroughputMiBPerSecond, IReadOnlyList<string> Limitations);

public static class ControlledTests
{
    public static Task<ControlledTestResult> CpuAsync(IProgress<double>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var clock = Stopwatch.StartNew();
        var block = new byte[4096];
        RandomNumberGenerator.Fill(block);
        long rounds = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            ct.ThrowIfCancellationRequested();
            SHA256.HashData(block, block.AsSpan(0, 32));
            rounds++;
            if (rounds % 1024 == 0) progress?.Report(clock.Elapsed.TotalSeconds / 5);
        }
        progress?.Report(1);
        return new ControlledTestResult("CPU single-worker SHA-256", clock.Elapsed, rounds, 0, null,
            ["Carga de um worker por 5 s; instruções SHA/aceleração e agendamento influenciam o resultado.",
             "Não é um benchmark padronizado nem diagnóstico térmico; sensores podem estar indisponíveis."]);
    }, ct);

    public static Task<ControlledTestResult> MemoryAsync(IProgress<double>? progress, CancellationToken ct,
        long? availablePhysicalMemoryBytes = null) => Task.Run(() =>
    {
        const int size = 32 * 1024 * 1024;
        ct.ThrowIfCancellationRequested();
        if (availablePhysicalMemoryBytes is < size * 4L)
            throw new InvalidOperationException("Memória física disponível insuficiente: são necessários pelo menos 128 MiB para iniciar este teste de 32 MiB.");
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (available > 0 && available < size * 4L) throw new InvalidOperationException("Memória insuficiente para o teste conservador de 32 MiB.");
        var buffer = new byte[size];
        var clock = Stopwatch.StartNew();
        long bytes = 0;
        for (var pass = 0; pass < 4; pass++)
        {
            for (var offset = 0; offset < size; offset += 65536)
            {
                ct.ThrowIfCancellationRequested();
                buffer.AsSpan(offset, 65536).Fill((byte)pass);
                foreach (var value in buffer.AsSpan(offset, 65536))
                    if (value != pass) throw new InvalidOperationException("Divergência observada no buffer de teste.");
                bytes += 65536;
            }
            progress?.Report((pass + 1) / 4d);
        }
        clock.Stop();
        return new ControlledTestResult("RAM: alocação/verificação de 32 MiB", clock.Elapsed, 4, bytes,
            clock.Elapsed.TotalSeconds > 0 ? bytes / 1048576d / clock.Elapsed.TotalSeconds : null,
            ["Exercita um buffer virtual pequeno; não comprova ausência de erros físicos na RAM.",
             "Inclui trabalho da CPU/cache e não mede largura de banda DRAM isoladamente."]);
    }, ct);

    public static async Task<ControlledTestResult> ReadAsync(string path, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var limit = Math.Min(file.Length, 64L * 1024 * 1024);
        if (limit == 0) throw new InvalidOperationException("O arquivo escolhido está vazio.");
        var buffer = new byte[65536];
        var clock = Stopwatch.StartNew();
        long bytes = 0, operations = 0;
        while (bytes < limit)
        {
            ct.ThrowIfCancellationRequested();
            var count = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - bytes)), ct);
            if (count == 0) break;
            bytes += count; operations++;
            progress?.Report(bytes / (double)limit);
        }
        return new ControlledTestResult("Leitura de arquivo (até 64 MiB)", clock.Elapsed, operations, bytes,
            clock.Elapsed.TotalSeconds > 0 ? bytes / 1048576d / clock.Elapsed.TotalSeconds : null,
            ["Leitura com cache do Windows; throughput não representa desempenho NVMe bruto.",
             "Não grava dados e não testa SMART, desgaste ou integridade física do dispositivo."]);
    }
}
