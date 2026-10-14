using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PCAnalyzer.Core;

namespace PCAnalyzer.Hardware;

[SupportedOSPlatform("windows")]
internal static class NativeWindows
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal,
            KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { public uint Low, High; public readonly ulong Value => ((ulong)High << 32) | Low; }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(ref PerformanceInformation information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out NativeFileTime idle, out NativeFileTime kernel, out NativeFileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out uint firmwareType);

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    internal static uint LogicalProcessorCount => GetActiveProcessorCount(ushort.MaxValue);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(nint rootPowerKey, out nint activePolicyGuid);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    internal static PerformanceInformation ReadMemory()
    {
        var information = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        if (!GetPerformanceInfo(ref information, information.Size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return information;
    }

    internal static (ulong Idle, ulong Total) ReadCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (idle.Value, kernel.Value + user.Value);
    }

    internal static bool TryGetFirmwareType(out uint firmware) => GetFirmwareType(out firmware);

    internal static bool TryGetPowerScheme(out Guid scheme)
    {
        scheme = default;
        if (PowerGetActiveScheme(0, out var pointer) != 0) return false;
        try { scheme = Marshal.PtrToStructure<Guid>(pointer); return true; }
        finally { LocalFree(pointer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    // DEVMODEW display union; all members are required to preserve native offsets.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPixel, Width, Height, DisplayFlags, Frequency, IcmMethod, IcmIntent, MediaType, DitherType,
            Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string device, int modeNumber, ref DisplayMode mode);

    internal static IReadOnlyList<HardwareComponent> GetDisplays()
    {
        var output = new List<HardwareComponent>();
        for (uint index = 0; index < 32; index++)
        {
            var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref device, 0)) break;
            if ((device.StateFlags & 1) == 0) continue; // attached to desktop
            var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>() };
            var facts = new List<HardwareFact>
            {
                new("Dispositivo de exibição", device.Name, "EnumDisplayDevicesW"),
                new("Principal", ((device.StateFlags & 4) != 0).ToString(), "EnumDisplayDevicesW"),
                new("Observação", "Modo de desktop; não mede refresh físico nem FPS do jogo", "Win32")
            };
            if (EnumDisplaySettings(device.Name, -1, ref mode))
            {
                facts.Add(new("Resolução atual", $"{mode.Width} × {mode.Height}", "EnumDisplaySettingsW ENUM_CURRENT_SETTINGS"));
                facts.Add(mode.Frequency is > 1 ? new("Frequência atual Hz", mode.Frequency.ToString(), "EnumDisplaySettingsW", Limitation:
                    "Hz inteiro reportado pelo Windows; VRR e mudanças durante jogos não são medidos.") :
                    new("Frequência atual Hz", null, "EnumDisplaySettingsW", Availability.Unsupported, "O driver retornou valor padrão 0/1."));
                var refresh = new SortedSet<uint>();
                for (var modeIndex = 0; modeIndex < 512; modeIndex++)
                {
                    var supported = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>() };
                    if (!EnumDisplaySettings(device.Name, modeIndex, ref supported)) break;
                    if (supported.Width == mode.Width && supported.Height == mode.Height && supported.Frequency > 1)
                        refresh.Add(supported.Frequency);
                }
                facts.Add(new("Hz suportados na resolução atual", string.Join(", ", refresh), "EnumDisplaySettingsW",
                    Limitation: "Modos enumerados pelo driver; não prova suporte físico para todas as configurações."));
            }
            else facts.Add(new("Modo atual", null, "EnumDisplaySettingsW", Availability.ReadError, "O driver não retornou modo de desktop."));
            output.Add(new("display." + index, "Monitor", device.Description, facts));
        }
        return output;
    }
}
