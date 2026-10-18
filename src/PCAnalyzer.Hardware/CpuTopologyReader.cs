using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PCAnalyzer.Hardware;

internal sealed record CpuCore(int Index, ushort Group, ulong Mask, bool Smt, byte EfficiencyClass)
{
    internal IReadOnlyList<int> LogicalProcessors => InterruptAffinityDecoder.Processors(Mask);
}

/// <summary>Decodes the published variable length Windows topology ABI, without assuming SMT adjacency.</summary>
internal static class CpuTopologyReader
{
    [SupportedOSPlatform("windows")]
    internal static IReadOnlyList<CpuCore> Read()
    {
        // WOW64 folds masks on machines with >32 CPUs; do not publish an inaccurate map.
        if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("O mapa de afinidade requer o aplicativo x64 neste Windows x64.");
        uint length = 0;
        GetLogicalProcessorInformationEx(0, null, ref length);
        var error = Marshal.GetLastWin32Error();
        if (error != 122) throw new Win32Exception(error);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (length is < 32 or > 4 * 1024 * 1024) throw new InvalidDataException("Tamanho inesperado da topologia CPU.");
            var buffer = new byte[length];
            if (GetLogicalProcessorInformationEx(0, buffer, ref length))
            {
                if (length > buffer.Length) throw new InvalidDataException("Topologia excedeu o buffer.");
                return Parse(buffer.AsSpan(0, checked((int)length)), IntPtr.Size);
            }
            error = Marshal.GetLastWin32Error();
            if (error != 122) throw new Win32Exception(error);
        }
        throw new InvalidDataException("A topologia mudou repetidamente durante a leitura.");
    }

    internal static IReadOnlyList<CpuCore> Parse(ReadOnlySpan<byte> bytes, int pointerSize)
    {
        if (pointerSize is not (4 or 8)) throw new ArgumentOutOfRangeException(nameof(pointerSize));
        var cores = new List<CpuCore>();
        var seen = new HashSet<(ushort, int)>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8) throw new InvalidDataException("Cabeçalho truncado da topologia CPU.");
            var header = bytes[offset..];
            var relation = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (size < 8 || size > bytes.Length - offset) throw new InvalidDataException("Comprimento inválido da topologia CPU.");
            var record = header[..checked((int)size)];
            if (relation != 0) throw new InvalidDataException("A consulta de núcleos retornou outra relação de topologia.");
            // Header (8), PROCESSOR_RELATIONSHIP prefix (24), GROUP_AFFINITY (KAFFINITY + WORD + WORD[3]).
            if (record.Length < 32 + pointerSize + 8) throw new InvalidDataException("Relação de núcleo truncada.");
            var groupCount = BinaryPrimitives.ReadUInt16LittleEndian(record[30..]);
            if (groupCount != 1) throw new InvalidDataException("Relação de núcleo deve conter exatamente um grupo.");
            var mask = pointerSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(record[32..]) : BinaryPrimitives.ReadUInt32LittleEndian(record[32..]);
            var group = BinaryPrimitives.ReadUInt16LittleEndian(record[(32 + pointerSize)..]);
            if (mask == 0) throw new InvalidDataException("Núcleo retornou máscara vazia.");
            foreach (var logical in InterruptAffinityDecoder.Processors(mask))
                if (!seen.Add((group, logical))) throw new InvalidDataException("Processador lógico repetido em núcleos distintos.");
            cores.Add(new(cores.Count, group, mask, (record[8] & 1) != 0, record[9]));
            offset += checked((int)size);
        }
        if (cores.Count == 0) throw new InvalidDataException("Nenhum núcleo físico retornado.");
        return cores;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(uint relationship, [Out] byte[]? buffer, ref uint returnedLength);
}

internal static class InterruptAffinityDecoder
{
    internal static IReadOnlyList<int> Processors(ulong mask) => Enumerable.Range(0, 64).Where(bit => (mask & (1UL << bit)) != 0).ToArray();

    internal static ulong DecodeBinary(ReadOnlySpan<byte> bytes, int pointerSize)
    {
        if (pointerSize is not (4 or 8)) throw new ArgumentOutOfRangeException(nameof(pointerSize));
        if (bytes.Length == 0 || bytes.Length > pointerSize)
            throw new InvalidDataException("AssignmentSetOverride REG_BINARY deve ter 1 a " + pointerSize.ToString(CultureInfo.InvariantCulture) + " bytes nesta plataforma.");
        ulong mask = 0;
        for (var index = 0; index < bytes.Length; index++) mask |= (ulong)bytes[index] << (8 * index);
        return mask;
    }

    internal static string Policy(uint value) => value switch
    {
        0 => "Padrão do sistema",
        1 => "Todos os processadores próximos (NUMA)",
        2 => "Um processador próximo (NUMA)",
        3 => "Todos os processadores da máquina",
        4 => "Processadores especificados por máscara",
        5 => "Distribuir mensagens entre processadores, se possível",
        6 => "Reservada para uso do sistema",
        _ => "Valor fora da enumeração documentada"
    };
}
