using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

internal static class ContextRecommendation
{
    internal static readonly int[] Tiers = [49152, 65536, 98304, 131072];
    internal const int Qwen35NineBMaximum = 131072;
    internal static int ForMemory(ulong? bytes) => bytes is null ? 65536 : bytes < 16UL * 1073741824 ? 49152 : bytes < 24UL * 1073741824 ? 65536 : bytes < 48UL * 1073741824 ? 98304 : 131072;
    internal static int? Maximum(string model, int? capacity)
    {
        if (capacity is not >= 1024) return null;
        return IsQwen35NineB(model) ? Math.Min(capacity.Value, Qwen35NineBMaximum) : capacity;
    }
    internal static int[] AvailableTiers(string model, int? capacity)
    {
        var maximum = Maximum(model, capacity);
        return maximum is null ? [] : Tiers.Where(t => t <= maximum.Value).ToArray();
    }
    internal static bool IsTier(int context) => Tiers.Contains(context);
    internal static string BaseModel(string model) => Regex.Replace(model, @"-\d+k$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static string DisplayName(string model) => BaseModel(model);
    static bool IsQwen35NineB(string model) => BaseModel(model).Split('/').Last().Equals("qwen3.5:9b", StringComparison.OrdinalIgnoreCase);
    internal static readonly ulong? DedicatedBytes = ReadDedicated();
    // DXGI reports dedicated VRAM as SIZE_T; WMI AdapterRAM truncates values above 4 GiB.
    static ulong? ReadDedicated()
    {
        object? factory = null;
        try
        {
            var iid = typeof(IDXGIFactory).GUID;
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out factory));
            var memories = new List<ulong?>();
            for (uint i = 0; ; i++)
            {
                var result = ((IDXGIFactory)factory).EnumAdapters(i, out var adapter);
                if (result == unchecked((int)0x887A0002)) break;
                Marshal.ThrowExceptionForHR(result);
                try
                {
                    adapter.GetDesc(out var desc);
                    if (desc.VendorId != 0x1414 && (desc.DedicatedVideoMemory.ToUInt64() > 0 || desc.SharedSystemMemory.ToUInt64() > 0))
                        memories.Add(desc.DedicatedVideoMemory.ToUInt64() > 0 && IsDiscrete(adapter) == true ? desc.DedicatedVideoMemory.ToUInt64() : null);
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
            return memories.Count == 1 ? memories[0] : null;
        }
        catch { return null; }
        finally { if (factory is not null && Marshal.IsComObject(factory)) Marshal.ReleaseComObject(factory); }
    }
    // UMA devices may report a dedicated reservation backed by system RAM. Do not treat it as VRAM.
    static bool? IsDiscrete(IDXGIAdapter adapter)
    {
        object? device = null;
        try
        {
            var iid = typeof(ID3D12Device).GUID;
            Marshal.ThrowExceptionForHR(D3D12CreateDevice(adapter, 0xb000, ref iid, out device));
            var api = (ID3D12Device)device;
            if (api.GetNodeCount() != 1) return null;
            var architecture = new Architecture();
            Marshal.ThrowExceptionForHR(api.CheckFeatureSupport(1, ref architecture, (uint)Marshal.SizeOf<Architecture>()));
            return architecture.Uma == 0;
        }
        catch { return null; }
        finally { if (device is not null && Marshal.IsComObject(device)) Marshal.ReleaseComObject(device); }
    }
    [DllImport("d3d12.dll")] static extern int D3D12CreateDevice([MarshalAs(UnmanagedType.IUnknown)] object adapter, uint minimumFeatureLevel, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object device);
    [StructLayout(LayoutKind.Sequential)] struct Architecture { public uint NodeIndex; public int TileBasedRenderer, Uma, CacheCoherentUma; }
    [ComImport, Guid("189819f1-1db6-4b57-be54-1821339b85f7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ID3D12Device
    {
        void GetPrivateData(); void SetPrivateData(); void SetPrivateDataInterface(); void SetName();
        [PreserveSig] uint GetNodeCount();
        void CreateCommandQueue(); void CreateCommandAllocator(); void CreateGraphicsPipelineState(); void CreateComputePipelineState(); void CreateCommandList();
        [PreserveSig] int CheckFeatureSupport(int feature, ref Architecture data, uint size);
    }
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct Desc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long Luid;
    }
    [ComImport, Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IDXGIFactory
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        [PreserveSig] int EnumAdapters(uint index, out IDXGIAdapter adapter);
    }
    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IDXGIAdapter
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumOutputs(); void GetDesc(out Desc desc);
    }
}
