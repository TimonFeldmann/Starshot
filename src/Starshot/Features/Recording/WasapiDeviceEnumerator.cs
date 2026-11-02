using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Starshot.Features.Recording;

/// <summary>一个可选的音频采集（render 端点）设备，供设置页列表使用。</summary>
public sealed class WasapiRenderDevice
{
    public WasapiRenderDevice(string id, string name, int channels, int sampleRate, bool isDefault)
    {
        Id = id;
        Name = name;
        Channels = channels;
        SampleRate = sampleRate;
        IsDefault = isDefault;
    }

    public string Id { get; }

    public string Name { get; }

    public int Channels { get; }

    public int SampleRate { get; }

    public bool IsDefault { get; }

    /// <summary>设置页 ComboBox 显示文本：名称 + 混音格式（系统默认项无格式）。</summary>
    public string DisplayText =>
        Channels > 0 && SampleRate > 0 ? $"{Name} ({Channels}ch/{SampleRate}Hz)" : Name;
}

/// <summary>
/// WASAPI render 端点枚举（设置页"录音设备"列表 + 默认端点标记）。
/// 与 WasapiLoopbackSource 同路线：RCW/ComImport vtable 互操作，按 OS build 分两代：
///   Modern (26300+, WASAPI 2.0):
///     - EnumAudioEndpoints(flow, dwStateMask) 返回新接口 IMMDeviceCollection (GetCount/Item,
///       不再继承 IEnumUnknown)；
///     - GetId 返回 HSTRING (UTF-16 空结尾，无 BSTR 长度前缀)——按 BSTR 读会在
///       .NET 8/10 编组器里读垃圾长度而崩，必须裸指针手读；
///     - IPropertyStore 新 vtable (GetCount/GetAt/GetValue) + 新 IID；字符串属性值
///       为 HSTRING 指针 (32 位类型 31, union@8)，裸读 UTF-16 (VirtualQuery 防 AV)。
///   Legacy (19041~26100):
///     - 经典 EnumAudioEndpoints(flow, role) + IEnumUnknown (Next)；
///     - GetId 返回真 BSTR (out string 编组可用)；
///     - 经典 IPropertyStore (GetPropertyCount/GetProperties/GetValue→BSTR)。
/// 整段枚举在单线程内完成 (设置页后台线程调用)，COM 引用不跨线程。
/// </summary>
public static class WasapiDeviceEnumerator
{
    private static readonly Guid IidClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    // 经典 PKEY_Device_FriendlyName (DE9A1591-9494-4840-9993-25EC7122C6D8, pid 14)
    private static readonly Guid PkeyDeviceFriendlyName = new(
        "DE9A1591-9494-4840-9993-25EC7122C6D8"
    );

    // 26300+ 的 DEVPROPKEY Device 属性组: FriendlyName = pid 2, FriendlyNameEx = pid 14
    private static readonly Guid PkeyDeviceGroup = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");

    // ===== COM 接口 (vtable 顺序与 SDK 头文件一致, 方法按槽位声明) =====

    [
        ComImport,
        Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"),
        ClassInterface(ClassInterfaceType.None)
    ]
    private class MmDevEnumClass { }

    // IMMDeviceEnumerator — 槽 0 两代语义不同 (role vs dwStateMask), 分别声明
    [
        ComImport,
        Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IEnumLegacy
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int role, [Out] out IntPtr ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, [Out] out IntPtr ppDevice);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, [Out] out IntPtr ppDevice);
    }

    [
        ComImport,
        Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IEnumModern
    {
        // WASAPI 2.0: 第二参是 DWORD dwStateMask (不是 ERole);
        // 0 会 E_INVALIDARG, DEVICE_STATEMASK_ALL = 0x0F; 出参为新 IMMDeviceCollection
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, [Out] out IntPtr ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, [Out] out IntPtr ppDevice);
    }

    // IMMDevice — 槽位两代相同, 仅 GetId 出参编组不同 (BSTR vs HSTRING)
    [
        ComImport,
        Guid("D666063F-1587-4E43-81F1-B948E807363F"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IMMDeviceLegacy
    {
        [PreserveSig]
        int Activate(ref Guid riid, uint dwClsCtx, IntPtr pActivationParams, [Out] out IntPtr ppv);

        [PreserveSig]
        int OpenPropertyStore(int stgmAccess, [Out] out IntPtr ppProperties);

        // 经典 build 返回真 BSTR, out string 默认 BSTR 编组可用
        [PreserveSig]
        int GetId([Out] out string ppstrId);

        [PreserveSig]
        int GetState(out int pwState);
    }

    [
        ComImport,
        Guid("D666063F-1587-4E43-81F1-B948E807363F"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IMMDeviceModern
    {
        [PreserveSig]
        int Activate(ref Guid riid, uint dwClsCtx, IntPtr pActivationParams, [Out] out IntPtr ppv);

        [PreserveSig]
        int OpenPropertyStore(int stgmAccess, [Out] out IntPtr ppProperties);

        // WASAPI 2.0 返回 HSTRING (UTF-16 空结尾), 裸指针手读
        [PreserveSig]
        int GetId([Out] out IntPtr ppstrId);

        [PreserveSig]
        int GetState(out int pwState);
    }

    // WASAPI 2.0 新 IMMDeviceCollection (GetCount/Item; 不支持 QI 到 IEnumUnknown)
    [
        ComImport,
        Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, [Out] out IntPtr ppDevice);
    }

    // 经典 build 的枚举器 (IEnumUnknown)
    [
        ComImport,
        Guid("00000100-0000-0000-C000-000000000046"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IEnumUnknown
    {
        [PreserveSig]
        int Next(int celt, [Out] out IntPtr pelt, out int pFetched);

        [PreserveSig]
        int Skip(int celt);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone([Out] out IntPtr ppenum);
    }

    // PKEY (C++ PROPERTYKEY = 20 字节: GUID + DWORD, Pack=4;
    // C# 默认对齐补到 24 字节会让 GetAt/GetProperties 返回 E_INVALIDARG)
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PKey
    {
        public Guid fmtid;

        public uint pid;
    }

    // 26300+ 新 IPropertyStore (新 IID + 新 vtable: GetCount/GetAt/GetValue)
    [
        ComImport,
        Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IStoreModern
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint iProp, [Out] IntPtr pkey);

        // 出参按裸 PROPVARIANT 指针读 (out object 编组对现代 HSTRING 变体抛 ArgumentException)
        [PreserveSig]
        int GetValue(ref PKey key, [Out] IntPtr pv);
    }

    // 经典 IPropertyStore
    [
        ComImport,
        Guid("886D8EEB-8EAC-4456-8B59-ACCBBE055204"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IStoreLegacy
    {
        [PreserveSig]
        int GetPropertyCount(out uint count);

        [PreserveSig]
        int GetProperties(uint nProperties, IntPtr pKeys);

        [PreserveSig]
        int GetValue(ref PKey key, [Out] out object pv);
    }

    // IAudioClient 槽 0~5 (Initialize 两代签名不同但只调 GetMixFormat, 槽位相同)
    [
        ComImport,
        Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(uint shareMode, uint streamFlags, IntPtr pFormat, IntPtr sessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint bufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint padding);

        [PreserveSig]
        int IsFormatSupported(uint shareMode, IntPtr pFormat, IntPtr ppClosestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr ppDeviceFormat);
    }

    /// <summary>
    /// 枚举全部 render 端点 (仅 ACTIVE 状态), 带友好名/混音格式/默认标记。
    /// 任何一步失败都静默降级 (少项/无名), 不抛——设置页列表允许不完整。
    /// </summary>
    public static List<WasapiRenderDevice> EnumerateRenderDevices()
    {
        var devices = new List<WasapiRenderDevice>();
        int build = Environment.OSVersion.Version.Build;
        if (build < 19041)
            return devices;
        bool modern = build >= 26300;

        object? enRcw = null;
        object? collRcw = null;
        object? enumRcw = null;
        try
        {
            enRcw = new MmDevEnumClass();

            // 默认端点 ID (列表里标 IsDefault)
            string defaultId = "";
            IntPtr defPtr;
            int defHr = modern
                ? ((IEnumModern)enRcw).GetDefaultAudioEndpoint(0, 0, out defPtr)
                : ((IEnumLegacy)enRcw).GetDefaultAudioEndpoint(0, 0, out defPtr);
            if (defHr == 0 && defPtr != IntPtr.Zero)
            {
                if (modern)
                {
                    var def = (IMMDeviceModern)
                        Marshal.GetTypedObjectForIUnknown(defPtr, typeof(IMMDeviceModern));
                    defaultId = def.GetId(out IntPtr defHstr) == 0 ? ReadHString(defHstr) : "";
                    Marshal.ReleaseComObject(def);
                }
                else
                {
                    var def = (IMMDeviceLegacy)
                        Marshal.GetTypedObjectForIUnknown(defPtr, typeof(IMMDeviceLegacy));
                    defaultId = def.GetId(out string sid) == 0 ? sid ?? "" : "";
                    Marshal.ReleaseComObject(def);
                }
            }

            // 枚举端点集合
            IntPtr collPtr;
            int hr = modern
                ? ((IEnumModern)enRcw).EnumAudioEndpoints(
                    0 /* eRender */
                    ,
                    0x0F /* DEVICE_STATEMASK_ALL */
                    ,
                    out collPtr
                )
                : ((IEnumLegacy)enRcw).EnumAudioEndpoints(
                    0,
                    0 /* eConsole */
                    ,
                    out collPtr
                );
            if (hr != 0 || collPtr == IntPtr.Zero)
                return devices;

            if (modern)
            {
                var coll = (IDeviceCollection)
                    Marshal.GetTypedObjectForIUnknown(collPtr, typeof(IDeviceCollection));
                collRcw = coll;
                if (coll.GetCount(out uint count) == 0)
                {
                    for (uint i = 0; i < count; i++)
                    {
                        if (coll.Item(i, out IntPtr devPtr) != 0 || devPtr == IntPtr.Zero)
                            continue;
                        var dev = (IMMDeviceModern)
                            Marshal.GetTypedObjectForIUnknown(devPtr, typeof(IMMDeviceModern));
                        AddDevice(dev, defaultId, devices);
                        Marshal.ReleaseComObject(dev);
                    }
                }
            }
            else
            {
                var iter = (IEnumUnknown)
                    Marshal.GetTypedObjectForIUnknown(collPtr, typeof(IEnumUnknown));
                enumRcw = iter;
                while (iter.Next(1, out IntPtr devPtr, out _) == 0 && devPtr != IntPtr.Zero)
                {
                    var dev = (IMMDeviceLegacy)
                        Marshal.GetTypedObjectForIUnknown(devPtr, typeof(IMMDeviceLegacy));
                    AddDevice(dev, defaultId, devices);
                    Marshal.ReleaseComObject(dev);
                }
            }
            return devices;
        }
        finally
        {
            if (enumRcw is not null)
                Marshal.ReleaseComObject(enumRcw);
            if (collRcw is not null)
                Marshal.ReleaseComObject(collRcw);
            if (enRcw is not null)
                Marshal.ReleaseComObject(enRcw);
        }
    }

    private static void AddDevice(
        IMMDeviceModern dev,
        string defaultId,
        List<WasapiRenderDevice> devices
    )
    {
        if (
            dev.GetState(out int state) != 0
            || state != 1 /* DEVICE_STATE_ACTIVE */
        )
            return;
        if (dev.GetId(out IntPtr hstr) != 0 || ReadHString(hstr) is not { } id || id.Length == 0)
            return;
        string name = GetFriendlyNameModern(dev) ?? id;
        (int ch, int sr) = GetMixFormat(dev);
        devices.Add(new WasapiRenderDevice(id, name, ch, sr, id == defaultId));
    }

    private static void AddDevice(
        IMMDeviceLegacy dev,
        string defaultId,
        List<WasapiRenderDevice> devices
    )
    {
        if (dev.GetState(out int state) != 0 || state != 1)
            return;
        string id = dev.GetId(out string sid) == 0 ? sid ?? "" : "";
        if (id.Length == 0)
            return;
        string name = GetFriendlyNameLegacy(dev) ?? id;
        (int ch, int sr) = GetMixFormat(dev);
        devices.Add(new WasapiRenderDevice(id, name, ch, sr, id == defaultId));
    }

    /// <summary>设备混音格式 (ch, 采样率); 失败 = (0, 0)。</summary>
    private static (int, int) GetMixFormat(IMMDeviceModern dev)
    {
        Guid riid = IidClient;
        if (
            dev.Activate(
                ref riid,
                1 /* CLSCTX_INPROC_SERVER */
                ,
                IntPtr.Zero,
                out IntPtr clientPtr
            ) != 0
            || clientPtr == IntPtr.Zero
        )
            return (0, 0);
        var client = (IAudioClient)
            Marshal.GetTypedObjectForIUnknown(clientPtr, typeof(IAudioClient));
        try
        {
            return GetMixFormat(client);
        }
        finally
        {
            Marshal.ReleaseComObject(client);
        }
    }

    private static (int, int) GetMixFormat(IMMDeviceLegacy dev)
    {
        Guid riid = IidClient;
        if (
            dev.Activate(ref riid, 1, IntPtr.Zero, out IntPtr clientPtr) != 0
            || clientPtr == IntPtr.Zero
        )
            return (0, 0);
        var client = (IAudioClient)
            Marshal.GetTypedObjectForIUnknown(clientPtr, typeof(IAudioClient));
        try
        {
            return GetMixFormat(client);
        }
        finally
        {
            Marshal.ReleaseComObject(client);
        }
    }

    private static (int Channels, int SampleRate) GetMixFormat(IAudioClient client)
    {
        if (client.GetMixFormat(out IntPtr wf) != 0)
            return (0, 0);
        try
        {
            // WAVEFORMATEX: wFormatTag(2) nChannels(2) nSamplesPerSec(4)
            return ((int)Marshal.ReadInt16(wf, 2), Marshal.ReadInt32(wf, 4));
        }
        finally
        {
            Marshal.FreeCoTaskMem(wf);
        }
    }

    /// <summary>
    /// 现代 build 的友好名：属性值裸读 (32 位类型 @0 + union @8),
    /// 字符串为 HSTRING 指针 (类型 31); 优先取 Device 属性组的 FriendlyName (pid 2),
    /// 取不到退回第一个字符串属性。
    /// </summary>
    private static string? GetFriendlyNameModern(IMMDeviceModern dev)
    {
        if (dev.OpenPropertyStore(0, out IntPtr storePtr) != 0 || storePtr == IntPtr.Zero)
            return null;
        var store = (IStoreModern)Marshal.GetTypedObjectForIUnknown(storePtr, typeof(IStoreModern));
        try
        {
            if (store.GetCount(out uint count) != 0 || count == 0)
                return null;
            IntPtr keyPtr = Marshal.AllocCoTaskMem(20);
            IntPtr pvPtr = Marshal.AllocCoTaskMem(64);
            try
            {
                string? first = null;
                for (uint i = 0; i < count; i++)
                {
                    if (store.GetAt(i, keyPtr) != 0)
                        continue;
                    PKey key = Marshal.PtrToStructure<PKey>(keyPtr);
                    if (store.GetValue(ref key, pvPtr) != 0)
                        continue;
                    long val = Marshal.ReadInt64(pvPtr, 8);
                    string? s = null;
                    if (val >= 0x10000 && IsCommitted(new IntPtr(val)))
                        s = ReadUtf16Safe(new IntPtr(val));
                    if (string.IsNullOrEmpty(s))
                        continue;
                    if (key.fmtid == PkeyDeviceGroup && key.pid == 2)
                        return s;
                    first ??= s;
                }
                return first;
            }
            finally
            {
                Marshal.FreeCoTaskMem(keyPtr);
                Marshal.FreeCoTaskMem(pvPtr);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>经典 build 的友好名：PKEY_Device_FriendlyName (BSTR 变体), 取不到退回第一个字符串。</summary>
    private static string? GetFriendlyNameLegacy(IMMDeviceLegacy dev)
    {
        if (dev.OpenPropertyStore(0, out IntPtr storePtr) != 0 || storePtr == IntPtr.Zero)
            return null;
        var store = (IStoreLegacy)Marshal.GetTypedObjectForIUnknown(storePtr, typeof(IStoreLegacy));
        try
        {
            if (store.GetPropertyCount(out uint count) != 0 || count == 0)
                return null;
            var keys = new PKey[count];
            var pin = GCHandle.Alloc(keys, GCHandleType.Pinned);
            string? result;
            try
            {
                string? first = null;
                if (store.GetProperties(count, pin.AddrOfPinnedObject()) == 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        PKey key = keys[i];
                        string? s = null;
                        try
                        {
                            // 个别 VARIANT 类型编组器不支持, 单条跳过不影响整体
                            if (store.GetValue(ref key, out object pv) == 0)
                                s = pv as string;
                        }
                        catch { }
                        if (string.IsNullOrEmpty(s))
                            continue;
                        if (key.fmtid == PkeyDeviceFriendlyName && key.pid == 14)
                            return s;
                        first ??= s;
                    }
                }
                result = first;
            }
            finally
            {
                pin.Free();
            }
            return result;
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>读 HSTRING (UTF-16 空结尾); 越界/不可读安全返回空。不主动释放 (少量泄漏可接受)。</summary>
    private static string ReadHString(IntPtr hstr)
    {
        if (hstr == IntPtr.Zero)
            return "";
        try
        {
            int len = 0;
            while (len < 512 && Marshal.ReadInt16(hstr, len * 2) != 0)
                len++;
            return len > 0 ? Marshal.PtrToStringUni(hstr, len) : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>读指针指向的 UTF-16 空结尾串 (最多 256 字), VirtualQuery 防 AV。</summary>
    private static string ReadUtf16Safe(IntPtr p)
    {
        try
        {
            int len = 0;
            while (
                len < 256
                && IsCommitted(new IntPtr(p.ToInt64() + len * 2))
                && Marshal.ReadInt16(p, len * 2) != 0
            )
            {
                len++;
            }
            return len >= 256 ? "" : Marshal.PtrToStringUni(p, len);
        }
        catch
        {
            return "";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;

        public IntPtr AllocationBase;

        public uint AllocationProtect;

        public uint __alignment1;

        public ulong RegionSize;

        public uint State;

        public uint Protect;

        public uint Type;

        public uint __alignment2;
    }

    [DllImport("kernel32.dll")]
    private static extern uint VirtualQuery(
        IntPtr lpAddress,
        out MemoryBasicInformation mbi,
        uint dwLength
    );

    private static bool IsCommitted(IntPtr p)
    {
        return VirtualQuery(
                p,
                out MemoryBasicInformation mbi,
                (uint)Marshal.SizeOf<MemoryBasicInformation>()
            ) != 0
            && mbi.State
                == 0x1000 /* MEM_COMMIT */
        ;
    }
}
