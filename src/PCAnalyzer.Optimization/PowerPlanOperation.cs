using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using PCAnalyzer.Core;

namespace PCAnalyzer.Optimization;

public sealed record PowerPlan(Guid Id, string Name, bool IsActive);

/// <summary>Documented PowrProf user-mode APIs; only installed scheme GUIDs are accepted.</summary>
public static class PowerPlans
{
    private const uint AccessScheme = 16;
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorMoreData = 234;
    public static Guid ReadActive()
    {
        EnsureWindows();
        nint pointer = 0;
        try
        {
            Check(Native.PowerGetActiveScheme(0, out pointer));
            if (pointer == 0) throw new InvalidOperationException("PowrProf retornou GUID nulo.");
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally { if (pointer != 0) Native.LocalFree(pointer); }
    }
    public static IReadOnlyList<PowerPlan> List()
    {
        EnsureWindows();
        var active = ReadActive();
        var plans = new List<PowerPlan>();
        for (uint index = 0; index < 1024; index++)
        {
            var buffer = new byte[16];
            uint size = (uint)buffer.Length;
            var result = Native.PowerEnumerate(0, 0, 0, AccessScheme, index, buffer, ref size);
            if (result == ErrorNoMoreItems) return plans;
            Check(result);
            if (size != 16) throw new InvalidOperationException("PowerEnumerate retornou tamanho de GUID inesperado.");
            var id = new Guid(buffer);
            plans.Add(new(id, ReadName(id), id == active));
        }
        throw new InvalidOperationException("Quantidade de planos excedeu o limite de segurança.");
    }
    public static void SetActive(Guid plan)
    {
        EnsureWindows();
        if (plan == Guid.Empty || !List().Any(x => x.Id == plan))
            throw new ArgumentException("O plano deve existir neste computador.", nameof(plan));
        Check(Native.PowerSetActiveScheme(0, ref plan));
        if (ReadActive() != plan) throw new InvalidOperationException("PowrProf não confirmou o plano ativo solicitado.");
    }
    private static string ReadName(Guid id)
    {
        uint size = 0;
        var result = Native.PowerReadFriendlyName(0, ref id, 0, 0, null, ref size);
        if (result is not (0 or ErrorMoreData)) Check(result);
        if (size is < 2 or > 65536) return id.ToString("D");
        var buffer = new byte[size];
        Check(Native.PowerReadFriendlyName(0, ref id, 0, 0, buffer, ref size));
        return Encoding.Unicode.GetString(buffer, 0, checked((int)size)).TrimEnd('\0');
    }
    private static void Check(uint error)
    {
        if (error != 0) throw new Win32Exception(checked((int)error));
    }
    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Planos de energia exigem Windows.");
    }
    private static class Native
    {
        [DllImport("powrprof.dll", ExactSpelling = true)]
        internal static extern uint PowerGetActiveScheme(nint rootPowerKey, out nint activePolicyGuid);
        [DllImport("powrprof.dll", ExactSpelling = true)]
        internal static extern uint PowerSetActiveScheme(nint rootPowerKey, ref Guid schemeGuid);
        [DllImport("powrprof.dll", ExactSpelling = true)]
        internal static extern uint PowerEnumerate(nint rootPowerKey, nint schemeGuid, nint subGroupGuid,
            uint accessFlags, uint index, [Out] byte[] buffer, ref uint bufferSize);
        [DllImport("powrprof.dll", ExactSpelling = true)]
        internal static extern uint PowerReadFriendlyName(nint rootPowerKey, ref Guid schemeGuid,
            nint subGroupGuid, nint powerSettingGuid, [Out] byte[]? buffer, ref uint bufferSize);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        internal static extern nint LocalFree(nint memory);
    }
}

public sealed class PowerPlanOperation : IReversibleOperation
{
    public string Kind => "active-power-plan";
    public string Target => "active-system-plan";
    public ChangeRisk Risk => ChangeRisk.Low;
    public string RecoveryInstructions =>
        "Restaure pelo histórico para o GUID original se o plano ainda existir. " +
        "Alternativa: Painel de Controle → Opções de Energia → selecionar o plano original. " +
        "Não apagar planos durante a comparação; consumo e temperatura podem variar.";
    public Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PowerPlans.ReadActive().ToString("D"));
    }
    public Task ValidateAsync(string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(value, "D", out var plan) || plan == Guid.Empty ||
            !string.Equals(plan.ToString("D"), value, StringComparison.Ordinal) || !PowerPlans.List().Any(x => x.Id == plan))
            throw new ArgumentException("Informe GUID canônico de um plano instalado neste computador.", nameof(value));
        return Task.CompletedTask;
    }
    public async Task WriteAsync(string value, CancellationToken cancellationToken)
    {
        await ValidateAsync(value, cancellationToken);
        PowerPlans.SetActive(Guid.ParseExact(value, "D"));
    }
}
