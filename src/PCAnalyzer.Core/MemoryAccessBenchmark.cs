using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PCAnalyzer.Core;

public sealed record MemoryAccessMeasurement(int BufferMiB, string Operation, long Operations,
    long PayloadBytes, double DurationSeconds, double? MiBPerSecond, double? NanosecondsPerAccess);
public sealed record MemoryAccessResult(IReadOnlyList<MemoryAccessMeasurement> Measurements,
    bool Cancelled, IReadOnlyList<string> Limitations);

/// <summary>Explicit process-local experiment. Results include CPU, runtime, cache, TLB and scheduling.</summary>
public static class MemoryAccessBenchmark
{
    private static long _sink;
    public static Task<MemoryAccessResult> RunAsync(long? availablePhysicalBytes, IProgress<double>? progress,
        CancellationToken ct, IReadOnlyList<int>? bufferSizesMiB = null) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var sizes = bufferSizesMiB?.ToArray() ?? [1, 16, 64];
        if (sizes.Length == 0 || sizes.Length > 3 || sizes.Distinct().Count() != sizes.Length || sizes.Any(s => s is not (1 or 16 or 64)))
            throw new ArgumentException("Escolha buffers distintos de 1, 16 ou 64 MiB.", nameof(bufferSizesMiB));
        const long minimumAvailable = 512L * 1024 * 1024;
        if (availablePhysicalBytes is null or < minimumAvailable)
            throw new InvalidOperationException("Este teste exige uma leitura válida de pelo menos 512 MiB de RAM disponível.");
        var gcAvailable = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (gcAvailable > 0 && gcAvailable < minimumAvailable)
            throw new InvalidOperationException("O limite de memória do processo é insuficiente para este teste.");
        var measurements = new List<MemoryAccessMeasurement>();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        var token = bounded.Token;
        var cancelled = false;
        var completed = 0;
        // Allocate once and reuse prefixes; live arrays total at most 128 MiB.
        var maximum = sizes.Max() * 1024 * 1024 / sizeof(int);
        var source = new int[maximum];
        var destination = new int[maximum];
        try
        {
            foreach (var size in sizes)
            {
                var count = size * 1024 * 1024 / sizeof(int);
                for (var start = 0; start < count; start += 16384)
                {
                    token.ThrowIfCancellationRequested();
                    var length = Math.Min(16384, count - start);
                    source.AsSpan(start, length).Fill(0x5a5a);
                    destination.AsSpan(start, length).Fill(0x5a5a);
                }
                // Warm JIT methods before measuring their full workload; no cache flush is claimed.
                Volatile.Write(ref _sink, Read(source, Math.Min(count, 16384), token));
                Write(destination, Math.Min(count, 16384), token);
                foreach (var operation in new[] { "Leitura sequencial", "Escrita sequencial", "Cópia" })
                {
                    token.ThrowIfCancellationRequested();
                    const int passes = 4;
                    var timer = Stopwatch.StartNew();
                    long checksum = 0;
                    for (var pass = 0; pass < passes; pass++)
                    {
                        if (operation == "Leitura sequencial") checksum += Read(source, count, token);
                        else if (operation == "Escrita sequencial") Write(destination, count, token);
                        else for (var start = 0; start < count; start += 16384)
                        {
                            token.ThrowIfCancellationRequested();
                            source.AsSpan(start, Math.Min(16384, count - start)).CopyTo(destination.AsSpan(start));
                        }
                    }
                    timer.Stop();
                    Volatile.Write(ref _sink, checksum + destination[count - 1]);
                    var payload = (long)count * sizeof(int) * passes;
                    measurements.Add(new(size, operation, passes, payload, timer.Elapsed.TotalSeconds,
                        timer.Elapsed.TotalSeconds > 0 ? payload / 1048576d / timer.Elapsed.TotalSeconds : null, null));
                    progress?.Report(++completed / (sizes.Length * 4d));
                }

                // One random cyclic chain with a node per 64 bytes. This spans the full selected
                // buffer without allocating a second permutation array or timing chain setup.
                var nodes = count / 16;
                source[0] = 0;
                var random = new Random(1729);
                for (var node = 1; node < nodes; node++)
                {
                    if ((node & 4095) == 0) token.ThrowIfCancellationRequested();
                    var previous = random.Next(node) * 16;
                    source[node * 16] = source[previous];
                    source[previous] = node * 16;
                }
                Volatile.Write(ref _sink, Chase(source, 1024, token));
                const int accesses = 1_048_576;
                var latencyTimer = Stopwatch.StartNew();
                var end = Chase(source, accesses, token);
                latencyTimer.Stop();
                Volatile.Write(ref _sink, end);
                measurements.Add(new(size, "Acesso dependente aleatório", accesses, (long)accesses * sizeof(int),
                    latencyTimer.Elapsed.TotalSeconds, null, latencyTimer.Elapsed.TotalSeconds * 1e9 / accesses));
                progress?.Report(++completed / (sizes.Length * 4d));
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        GC.KeepAlive(source); GC.KeepAlive(destination);
        return new MemoryAccessResult(measurements, cancelled,
        ["Mede buffers virtuais do processo em 1, 16 e 64 MiB (ou subconjunto registrado). Não isola a latência DRAM e não lê CL/SPD/XMP.",
         "Inclui CPU, cache, TLB, runtime, agendamento e possíveis faltas de página. Afinidade e prioridades não são alteradas.",
         "Taxas representam bytes úteis percorridos por quatro passagens; cópia não mede o tráfego físico de leitura+escrita do barramento.",
         "Acesso dependente: cadeia aleatória com nós a cada 64 bytes e 1.048.576 saltos. ns/acesso é média observada, sem subtração de overhead.",
         "Não é teste de integridade da RAM. Repita sob a mesma carga; os resultados não garantem desempenho em jogos.",
         cancelled ? "Teste interrompido ou limite de 30 s atingido; somente etapas concluídas foram preservadas." : "Todas as etapas selecionadas foram concluídas."]);
    }, ct);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Read(int[] buffer, int count, CancellationToken ct)
    {
        long sum = 0;
        for (var start = 0; start < count; start += 16384)
        {
            ct.ThrowIfCancellationRequested();
            var end = Math.Min(start + 16384, count);
            for (var i = start; i < end; i++) sum += buffer[i];
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Write(int[] buffer, int count, CancellationToken ct)
    {
        for (var start = 0; start < count; start += 16384)
        {
            ct.ThrowIfCancellationRequested();
            var end = Math.Min(start + 16384, count);
            for (var i = start; i < end; i++) buffer[i] = i;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Chase(int[] links, int count, CancellationToken ct)
    {
        var index = 0;
        for (var start = 0; start < count; start += 1024)
        {
            ct.ThrowIfCancellationRequested();
            for (var step = 0; step < Math.Min(1024, count - start); step++) index = links[index];
        }
        return index;
    }
}
