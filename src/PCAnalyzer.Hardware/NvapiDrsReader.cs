using System.Runtime.InteropServices;
using System.Text;

namespace PCAnalyzer.Hardware;

// Public NVAPI declarations/IDs: NVIDIA/nvapi 70d337db9186e968eab622f7e786de7e437faf3d (MIT).
// This adapter deliberately contains no settings mutation or save entry point.
internal interface INvapiDrsReader : IDisposable
{
    NvDrsProfile GetGlobalProfile();
    NvDrsProfile GetBaseProfile();
    NvDrsApplication FindApplication(string executable);
    NvDrsSetting ReadSetting(nint profile, uint settingId);
}

internal sealed record NvDrsProfile(nint Handle, string Name, bool Predefined);
internal sealed record NvDrsApplication(NvDrsProfile Profile, string Executable, bool Predefined,
    string FileInFolder, uint Flags, string CommandLine, int StructureVersion);
internal sealed record NvDrsSetting(uint Id, uint Type, uint Location, bool CurrentPredefined,
    bool PredefinedValid, uint CurrentValue, uint PredefinedValue);

internal sealed class NvDrsException(string operation, int status) : Exception($"{operation}: NVAPI {status}.")
{
    public int Status { get; } = status;
}

/// <summary>Owned DRS snapshot loaded from the installed display driver. No persistent settings are changed.</summary>
internal sealed class NvapiDrsReader : INvapiDrsReader
{
    internal const int UnicodeBytes = 4096;
    internal const int SettingBytes = 12320;
    internal const int SettingPredefinedOffset = 4120;
    internal const int SettingCurrentOffset = 8220;
    internal const int ProfileBytes = 4116;
    internal static int ApplicationBytes(int version) => version switch
    { 1 => 12296, 2 => 16392, 3 => 16396, 4 => 20492, _ => throw new ArgumentOutOfRangeException(nameof(version)) };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint QueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateSession(out nint session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SessionOperation(nint session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetProfile(nint session, out nint profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProfileInfo(nint session, nint profile, nint info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindApp(nint session, nint name, out nint profile, nint application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSetting(nint session, nint profile, uint settingId, nint setting);

    private nint _library, _session;
    private bool _initialized;
    private readonly QueryInterface _query;
    private readonly NoArgs _unload;
    private readonly SessionOperation _destroy;
    private readonly GetProfile _global, _base;
    private readonly ProfileInfo _profileInfo;
    private readonly FindApp _findApp;
    private readonly GetSetting _getSetting;

    public NvapiDrsReader()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("A leitura NVAPI requer Windows x64.");
        var path = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
        if (!File.Exists(path)) throw new DllNotFoundException("NVAPI ausente no driver instalado em System32.");
        // Absolute trusted OS location; do not search the application folder or PATH.
        _library = NativeLibrary.Load(path);
        try
        {
            _query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(NativeLibrary.GetExport(_library, "nvapi_QueryInterface"));
            var initialize = Resolve<NoArgs>(0x0150e828);
            _unload = Resolve<NoArgs>(0xd22bdd7e);
            _destroy = Resolve<SessionOperation>(0xdad9cff8);
            _global = Resolve<GetProfile>(0x617bff9f);
            _base = Resolve<GetProfile>(0xda8466a0);
            _profileInfo = Resolve<ProfileInfo>(0x61cd6fd6);
            _findApp = Resolve<FindApp>(0xeee566b2);
            _getSetting = Resolve<GetSetting>(0x73bf8338);
            var create = Resolve<CreateSession>(0x0694d52e);
            var load = Resolve<SessionOperation>(0x375dbd6b);
            Check(initialize(), "Initialize");
            _initialized = true;
            Check(create(out _session), "DRS_CreateSession");
            Check(load(_session), "DRS_LoadSettings");
        }
        catch { Dispose(); throw; }
    }

    private T Resolve<T>(uint id) where T : Delegate
    {
        var address = _query(id);
        return address == 0 ? throw new EntryPointNotFoundException($"NVAPI pública 0x{id:X8} não disponível.")
            : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    public NvDrsProfile GetGlobalProfile() { EnsureOpen(); Check(_global(_session, out var profile), "DRS_GetCurrentGlobalProfile"); return ReadProfile(profile); }
    public NvDrsProfile GetBaseProfile() { EnsureOpen(); Check(_base(_session, out var profile), "DRS_GetBaseProfile"); return ReadProfile(profile); }

    private NvDrsProfile ReadProfile(nint profile)
    {
        using var data = new Buffer(ProfileBytes, 1);
        Check(_profileInfo(_session, profile, data.Address), "DRS_GetProfileInfo");
        return new(profile, Unicode(data.Address, 4), UInt32(data.Address, 4104) != 0);
    }

    public NvDrsApplication FindApplication(string executable)
    {
        EnsureOpen();
        if (string.IsNullOrWhiteSpace(executable) || executable.Length >= 2048 || executable.Contains('\0'))
            throw new ArgumentException("Nome ou caminho inválido para a consulta NVAPI.", nameof(executable));
        using var name = new Buffer(UnicodeBytes);
        var encoded = Encoding.Unicode.GetBytes(executable);
        Marshal.Copy(encoded, 0, name.Address, encoded.Length);
        for (var version = 4; version >= 1; version--)
        {
            using var data = new Buffer(ApplicationBytes(version), version);
            var status = _findApp(_session, name.Address, out var profile, data.Address);
            if (status == -9 && version > 1) continue; // Explicit ABI fallback only.
            Check(status, "DRS_FindApplicationByName");
            return new(ReadProfile(profile), Unicode(data.Address, 8), UInt32(data.Address, 4) != 0,
                version >= 2 ? Unicode(data.Address, 12296) : "",
                version >= 3 ? UInt32(data.Address, 16392) : 0,
                version >= 4 ? Unicode(data.Address, 16396) : "", version);
        }
        throw new InvalidOperationException("Nenhuma versão de NVDRS_APPLICATION aceita.");
    }

    public NvDrsSetting ReadSetting(nint profile, uint settingId)
    {
        EnsureOpen();
        using var data = new Buffer(SettingBytes, 1);
        Check(_getSetting(_session, profile, settingId, data.Address), "DRS_GetSetting");
        return DecodeSetting(data.Address, settingId);
    }

    internal static NvDrsSetting DecodeSetting(nint address, uint expectedId)
    {
        var id = UInt32(address, 4100);
        if (id != expectedId) throw new InvalidDataException("NVAPI retornou um identificador diferente do consultado.");
        return new(id, UInt32(address, 4104), UInt32(address, 4108), UInt32(address, 4112) != 0,
            UInt32(address, 4116) != 0, UInt32(address, SettingCurrentOffset), UInt32(address, SettingPredefinedOffset));
    }

    internal static string Unicode(nint address, int offset)
    {
        var bytes = new byte[UnicodeBytes];
        Marshal.Copy(address + offset, bytes, 0, bytes.Length);
        var text = Encoding.Unicode.GetString(bytes);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text; // Bounded even if the driver forgot the terminator.
    }

    private static uint UInt32(nint address, int offset) => unchecked((uint)Marshal.ReadInt32(address, offset));
    private static void Check(int status, string operation) { if (status != 0) throw new NvDrsException(operation, status); }
    private void EnsureOpen() => ObjectDisposedException.ThrowIf(_session == 0, this);

    public void Dispose()
    {
        if (_session != 0) { _destroy(_session); _session = 0; }
        if (_initialized) { _unload(); _initialized = false; }
        if (_library != 0) { NativeLibrary.Free(_library); _library = 0; }
    }

    private sealed class Buffer : IDisposable
    {
        public nint Address { get; }
        public Buffer(int bytes, int version = 0)
        {
            Address = Marshal.AllocHGlobal(bytes);
            Marshal.Copy(new byte[bytes], 0, Address, bytes);
            if (version != 0) Marshal.WriteInt32(Address, bytes | (version << 16));
        }
        public void Dispose() => Marshal.FreeHGlobal(Address);
    }
}
