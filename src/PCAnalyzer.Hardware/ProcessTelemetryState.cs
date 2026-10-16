using System.Diagnostics;
using PCAnalyzer.Core;

namespace PCAnalyzer.Hardware;

/// <summary>A denied CPU/start-time field must not discard accessible PID, name or working set.</summary>
internal sealed class ProcessTelemetryState
{
    private readonly Dictionary<(int Pid, long Start), (TimeSpan Cpu, long Ticks)> _baselines = [];
    private readonly HashSet<(int Pid, long Start)> _seen = [];

    internal void BeginBatch() => _seen.Clear();
    internal void EndBatch()
    {
        foreach (var key in _baselines.Keys.Where(key => !_seen.Contains(key)).ToArray()) _baselines.Remove(key);
    }
    internal void Reset() { _baselines.Clear(); _seen.Clear(); }

    internal ProcessSample Read(int pid, Func<string> readName, Func<long> readStart, Func<TimeSpan> readCpu,
        Func<long> readMemory, DateTimeOffset timestamp, long ticks, int processors, out bool partial)
    {
        var problems = new List<string>();
        string name;
        try { name = readName(); } catch (Exception error) when (CanSkip(error)) { name = "PID " + pid; problems.Add("nome indisponível"); }
        long? memory = null, start = null;
        TimeSpan? cpu = null;
        try { memory = readMemory(); } catch (Exception error) when (CanSkip(error)) { problems.Add("memória indisponível"); }
        try { start = readStart(); } catch (Exception error) when (CanSkip(error)) { problems.Add("criação do processo indisponível; CPU não calculada"); }
        try { cpu = readCpu(); } catch (Exception error) when (CanSkip(error)) { problems.Add("tempo CPU indisponível"); }
        double? percent = null;
        if (start.HasValue && cpu.HasValue)
        {
            var key = (pid, start.Value);
            if (_baselines.TryGetValue(key, out var previous) && ticks > previous.Ticks && cpu >= previous.Cpu)
                percent = Math.Clamp((cpu.Value - previous.Cpu).TotalSeconds / Stopwatch.GetElapsedTime(previous.Ticks, ticks).TotalSeconds /
                    Math.Max(1, processors) * 100, 0, 100);
            _seen.Add(key); _baselines[key] = (cpu.Value, ticks);
        }
        partial = problems.Count > 0;
        var detail = percent.HasValue ? "CPU normalizada pelos processadores lógicos ativos; working set inclui páginas compartilhadas." :
            "CPU requer duas amostras com o mesmo PID e instante de criação; não há valor zero presumido.";
        if (partial) detail += " Leitura parcial: " + string.Join("; ", problems) + ".";
        return new(pid, name, percent, memory, timestamp, detail);
    }

    internal static bool CanSkip(Exception error) => error is System.ComponentModel.Win32Exception or InvalidOperationException
        or NotSupportedException or UnauthorizedAccessException or System.Security.SecurityException;
}
