using System.ComponentModel;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using PCAnalyzer.Core;

namespace PCAnalyzer.Hardware;

internal sealed record AuditPnpDevice(string InstanceId, string Name, string Class, string ClassGuid,
    string Service, string HardwareIds, string Enumerator, string Location, string ParentId,
    uint? Status, uint? Problem, IReadOnlyList<string> Limitations, uint DevInst = 0, string DriverKey = "");

internal sealed record DevicePropertyRead(uint? Value, Availability Status, string? Detail = null);
internal sealed record AllocatedInterrupt(uint Number, ushort Group, ulong Affinity, ushort Flags);
internal sealed record InterruptResourceRead(IReadOnlyList<AllocatedInterrupt> Resources, Availability Status, string? Detail = null);

/// <summary>Present devices only. SetupAPI enumeration and ConfigMgr getters; no installer or setter entry points.</summary>
[SupportedOSPlatform("windows")]
internal static class DeviceAuditNative
{
    internal static IReadOnlyList<AuditPnpDevice> Read(CancellationToken ct)
    {
        var set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 2 | 4); // DIGCF_PRESENT | DIGCF_ALLCLASSES
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var devices = new List<AuditPnpDevice>();
            for (uint index = 0; index < 4096; index++)
            {
                ct.ThrowIfCancellationRequested();
                var info = new DeviceInfoData { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) return devices;
                    throw new Win32Exception(error);
                }
                var instance = DeviceId(info.DevInst);
                var limitations = new List<string>();
                string Property(uint property)
                {
                    var buffer = new byte[65536];
                    if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out var type, buffer, (uint)buffer.Length, out var length))
                    {
                        var error = Marshal.GetLastWin32Error();
                        // ERROR_INVALID_DATA also represents a property not exposed by this device.
                        if (error != 13) limitations.Add($"Propriedade PnP {property}: Win32 {error}.");
                        return "";
                    }
                    if (length > buffer.Length || (length & 1) != 0 || type is not (1 or 7))
                    {
                        limitations.Add($"Propriedade PnP {property}: tipo/tamanho não textual esperado.");
                        return "";
                    }
                    return string.Join("; ", Encoding.Unicode.GetString(buffer, 0, checked((int)length)).Split('\0', StringSplitOptions.RemoveEmptyEntries));
                }
                var className = Property(7);
                var service = Property(4);
                var enumerator = Property(22);
                if (!Relevant(instance, className, enumerator)) continue;
                var name = Property(12);
                if (name.Length == 0) name = Property(0);
                if (name.Length == 0) name = className.Length > 0 ? className : "Dispositivo PnP";
                var parent = "";
                var parentResult = CM_Get_Parent(out var parentNode, info.DevInst, 0);
                if (parentResult == 0)
                {
                    try { parent = DeviceId(parentNode); }
                    catch (Exception error) { limitations.Add("ID do pai indisponível: " + error.Message); }
                }
                else limitations.Add($"Relação pai indisponível (ConfigMgr {parentResult}).");
                uint? status = null, problem = null;
                var statusResult = CM_Get_DevNode_Status(out var statusValue, out var problemValue, info.DevInst, 0);
                if (statusResult == 0) { status = statusValue; problem = problemValue; }
                else limitations.Add($"Estado PnP indisponível (ConfigMgr {statusResult}).");
                var location = Property(13);
                var paths = Property(35);
                devices.Add(new(instance, name, className, info.ClassGuid.ToString(), service, Property(1), enumerator,
                    string.Join("; ", new[] { location, paths }.Where(value => value.Length > 0)), parent, status, problem, limitations,
                    info.DevInst, Property(9)));
            }
            throw new InvalidDataException("Enumeração PnP excedeu o limite de 4096 dispositivos presentes.");
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    internal static bool Relevant(string instance, string className, string enumerator) =>
        instance.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
        instance.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
        instance.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) ||
        instance.StartsWith("HDAUDIO\\", StringComparison.OrdinalIgnoreCase) ||
        new[] { "Display", "Net", "USB", "HIDClass", "Keyboard", "Mouse", "SCSIAdapter", "HDC", "MEDIA" }
            .Contains(className, StringComparer.OrdinalIgnoreCase) || enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase);

    internal static DevicePropertyRead ReadPciProperty(uint device, uint property)
    {
        var key = new DevicePropertyKey { Format = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62"), Id = property };
        var bytes = new byte[4];
        uint length = 4;
        var result = CM_Get_DevNode_PropertyW(device, ref key, out var type, bytes, ref length, 0);
        if (result != 0) return new(null, ConfigStatus(result), $"ConfigMgr {result}; propriedade PCI {property} não retornada.");
        if (type != 7 || length != 4) return new(null, Availability.ReadError, "Propriedade PCI não retornou DEVPROP_TYPE_UINT32 de 4 bytes.");
        return new(BinaryPrimitives.ReadUInt32LittleEndian(bytes), Availability.Available);
    }

    internal static InterruptResourceRead ReadInterrupts(uint device, CancellationToken ct)
    {
        // ALLOC_LOG_CONF: currently allocated resources, not requirements or a boot fallback.
        var result = CM_Get_First_Log_Conf(out var configuration, device, 2);
        if (result != 0) return new([], ConfigStatus(result), $"Configuração de recursos alocados indisponível (ConfigMgr {result}).");
        var resources = new List<AllocatedInterrupt>();
        nuint previous = 0;
        try
        {
            for (var index = 0; index < 1024; index++)
            {
                ct.ThrowIfCancellationRequested();
                result = CM_Get_Next_Res_Des(out var next, previous == 0 ? configuration : previous, 4, out _, 0);
                if (previous != 0) { CM_Free_Res_Des_Handle(previous); previous = 0; }
                if (result == 15) return resources.Count > 0 ? new(resources, Availability.Available) :
                    new([], Availability.InsufficientData, "Nenhum descritor IRQ foi exposto nesta configuração; isso não comprova ausência de interrupções.");
                if (result != 0) return new(resources, ConfigStatus(result), $"Enumeração IRQ interrompida (ConfigMgr {result}).");
                previous = next;
                // Request the explicit 64-bit ABI even if called by an x86 process.
                result = CM_Get_Res_Des_Data_Size(out var size, next, 2);
                if (result != 0) return new(resources, ConfigStatus(result), $"Tamanho IRQ indisponível (ConfigMgr {result}).");
                if (size is < 24 or > 1048576) return new(resources, Availability.ReadError, "Comprimento inesperado do descritor IRQ.");
                var bytes = new byte[size];
                result = CM_Get_Res_Des_Data(next, bytes, size, 2);
                if (result != 0) return new(resources, ConfigStatus(result), $"Descritor IRQ indisponível (ConfigMgr {result}).");
                try { resources.Add(InterruptResourceDecoder.Parse(bytes)); }
                catch (InvalidDataException error) { return new(resources, Availability.ReadError, error.Message); }
            }
            return new(resources, Availability.ReadError, "Limite de 1024 descritores IRQ atingido.");
        }
        finally
        {
            if (previous != 0) CM_Free_Res_Des_Handle(previous);
            CM_Free_Log_Conf_Handle(configuration);
        }
    }

    internal static Availability ConfigStatus(uint result) => result switch
    {
        0 => Availability.Available,
        0x33 => Availability.PermissionRequired,
        0x25 or 0x34 or 0x35 => Availability.Unsupported,
        0x0d or 0x0e or 0x0f => Availability.InsufficientData,
        _ => Availability.ReadError
    };

    private static string DeviceId(uint device)
    {
        var result = CM_Get_Device_ID_Size(out var length, device, 0);
        if (result != 0 || length is 0 or > 32768) throw new InvalidDataException($"Tamanho do ID PnP indisponível (ConfigMgr {result}).");
        var id = new StringBuilder(checked((int)length + 1));
        result = CM_Get_Device_IDW(device, id, length + 1, 0);
        if (result != 0) throw new InvalidDataException($"ID PnP indisponível (ConfigMgr {result}).");
        return id.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DevInst;
        internal UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevicePropertyKey { internal Guid Format; internal uint Id; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_PropertyW(uint device, ref DevicePropertyKey key, out uint type,
        [Out] byte[] buffer, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_First_Log_Conf(out nuint configuration, uint device, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Next_Res_Des(out nuint descriptor, nuint previous, uint type, out uint actualType, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Res_Des_Data_Size(out uint size, nuint descriptor, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Res_Des_Data(nuint descriptor, [Out] byte[] buffer, uint size, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Free_Res_Des_Handle(nuint descriptor);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Free_Log_Conf_Handle(nuint configuration);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfoData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfoData data, uint property,
        out uint type, [Out] byte[] buffer, uint bufferSize, out uint requiredSize);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Device_ID_Size(out uint length, uint device, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_IDW(uint device, StringBuilder buffer, uint length, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint device, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint device, uint flags);
}

internal static class InterruptResourceDecoder
{
    // IRQ_DES_64 with NT_PROCESSOR_GROUPS, cfgmgr32.h. The group field is not part of Flags.
    internal static AllocatedInterrupt Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24) throw new InvalidDataException("Descritor IRQ de 64 bits truncado.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var rangeSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (count > 0 && (rangeSize != 12 || count > (bytes.Length - 24) / 12))
            throw new InvalidDataException("Intervalos IRQ truncados ou incompatíveis com o contrato nativo.");
        return new(BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));
    }
}
