using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace XinSpect;

public sealed record D3D11ShaderCompilation(
    bool Succeeded,
    string Error,
    int BytecodeLength,
    byte[]? Bytecode);

/// <summary>
/// D3D11 最小原生橋：只暴露 Deep Bench GPU compute 需要的 API。
/// 不使用 SharpDX/DirectX NuGet 套件；COM vtable 槽位以 Windows SDK d3d11.h 逐項核對。
/// </summary>
public static class D3D11Native
{
    private const uint D3D11BindUnorderedAccess = 0x80;
    private const uint D3D11MiscBufferStructured = 0x40;
    private const uint D3D11UsageDefault = 0;
    private const uint D3D11UsageStaging = 3;
    private const uint D3D11CpuAccessRead = 0x20000;
    private const uint D3D11MapRead = 1;

    private static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11BufferDesc
    {
        public uint ByteWidth;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
        public uint StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11SubresourceData
    {
        public IntPtr SysMem;
        public uint SysMemPitch;
        public uint SysMemSlicePitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11UnorderedAccessViewDesc
    {
        public uint Format;             // DXGI_FORMAT_UNKNOWN
        public uint ViewDimension;      // D3D11_UAV_DIMENSION_BUFFER = 1
        public uint FirstElement;
        public uint NumElements;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11MappedSubresource
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DXGIAdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    public static D3D11ShaderCompilation CompileShader(
        string source,
        string entryPoint,
        string target)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(source);
        int hr = D3DCompile(
            utf8,
            (UIntPtr)utf8.Length,
            "XinSpectDeepBench.hlsl",
            IntPtr.Zero,
            IntPtr.Zero,
            entryPoint,
            target,
            0,
            0,
            out IntPtr code,
            out IntPtr errors);

        try
        {
            if (hr != 0)
            {
                string error = ReadBlobString(errors);
                return new D3D11ShaderCompilation(false, $"D3DCompile 失敗，HRESULT=0x{hr:X8}；{error}", 0, null);
            }

            int length = checked((int)GetBlobSize(code));
            var bytecode = new byte[length];
            unsafe
            {
                void* pointer = GetBlobPointer(code);
                fixed (byte* destination = bytecode)
                {
                    Buffer.MemoryCopy(pointer, destination, bytecode.Length, length);
                }
            }

            return new D3D11ShaderCompilation(true, string.Empty, length, bytecode);
        }
        finally
        {
            SafeRelease(errors);
            SafeRelease(code);
        }
    }

    public static async Task<GpuFp32Run> MeasureFp32Async(GpuFp32Workload workload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => MeasureFp32(workload, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static GpuFp32Run MeasureFp32(GpuFp32Workload workload, CancellationToken cancellationToken)
    {
        int elementCount = workload.ElementCount;
        int byteWidth = checked(elementCount * sizeof(float));

        (IntPtr adapter, DXGIAdapterDesc1 description) = SelectHardwareAdapter();
        IntPtr device = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        IntPtr defaultBuffer = IntPtr.Zero;
        IntPtr stagingBuffer = IntPtr.Zero;
        IntPtr unorderedAccessView = IntPtr.Zero;
        IntPtr computeShader = IntPtr.Zero;
        try
        {
            uint[] featureLevels = [0x0B100, 0x0B000];
            int createHr = D3D11CreateDevice(
                adapter,
                0, // D3D_DRIVER_TYPE_UNKNOWN: 已明確傳入硬體 adapter
                IntPtr.Zero,
                0,
                featureLevels,
                (uint)featureLevels.Length,
                7,
                out device,
                out uint featureLevel,
                out context);
            if (createHr != 0)
                ThrowDeviceFailure(createHr, "D3D11CreateDevice");

            unsafe
            {
                void* devicePtr = (void*)device;
                // ID3D11Device vtable 只繼承 IUnknown：CreateBuffer=3、UAV=8、ComputeShader=18、
                // FeatureLevel=37、DeviceRemovedReason=39；不要把 ID3D11DeviceChild 的四個槽誤加進去。
                var createBuffer = (delegate* unmanaged[Stdcall]<void*, D3D11BufferDesc*, D3D11SubresourceData*, void**, int>)GetVTableSlot(device, 3);
                var createUav = (delegate* unmanaged[Stdcall]<void*, void*, D3D11UnorderedAccessViewDesc*, void**, int>)GetVTableSlot(device, 8);
                var createComputeShader = (delegate* unmanaged[Stdcall]<void*, byte*, nuint, void*, void**, int>)GetVTableSlot(device, 18);
                var getFeatureLevel = (delegate* unmanaged[Stdcall]<void*, uint>)GetVTableSlot(device, 37);
                var getDeviceRemovedReason = (delegate* unmanaged[Stdcall]<void*, int>)GetVTableSlot(device, 39);
                featureLevel = getFeatureLevel(devicePtr);

                var defaultDesc = new D3D11BufferDesc
                {
                    ByteWidth = (uint)byteWidth,
                    Usage = D3D11UsageDefault,
                    BindFlags = D3D11BindUnorderedAccess,
                    CPUAccessFlags = 0,
                    MiscFlags = D3D11MiscBufferStructured,
                    StructureByteStride = sizeof(float),
                };
                void* defaultBufferPtr = null;
                int hr = createBuffer(devicePtr, &defaultDesc, null, &defaultBufferPtr);
                if (hr != 0 || defaultBufferPtr is null)
                    ThrowDeviceFailure(hr, getDeviceRemovedReason(devicePtr), "CreateBuffer");
                defaultBuffer = (IntPtr)defaultBufferPtr;

                var stagingDesc = new D3D11BufferDesc
                {
                    ByteWidth = (uint)byteWidth,
                    Usage = D3D11UsageStaging,
                    BindFlags = 0,
                    CPUAccessFlags = D3D11CpuAccessRead,
                    MiscFlags = 0,
                    StructureByteStride = 0,
                };
                void* stagingBufferPtr = null;
                hr = createBuffer(devicePtr, &stagingDesc, null, &stagingBufferPtr);
                if (hr != 0 || stagingBufferPtr is null)
                    ThrowDeviceFailure(hr, getDeviceRemovedReason(devicePtr), "CreateStagingBuffer");
                stagingBuffer = (IntPtr)stagingBufferPtr;

                var uavDesc = new D3D11UnorderedAccessViewDesc
                {
                    Format = 0,
                    ViewDimension = 1,
                    FirstElement = 0,
                    NumElements = (uint)elementCount,
                    Flags = 0,
                };
                void* uavPtr = null;
                hr = createUav(devicePtr, defaultBufferPtr, &uavDesc, &uavPtr);
                if (hr != 0 || uavPtr is null)
                    ThrowDeviceFailure(hr, getDeviceRemovedReason(devicePtr), "CreateUnorderedAccessView");
                unorderedAccessView = (IntPtr)uavPtr;

                D3D11ShaderCompilation compilation = CompileShader(
                    GpuFp32ComputeService.HlslSource,
                    "CSMain",
                    "cs_5_0");
                if (!compilation.Succeeded || compilation.Bytecode is null)
                    throw new InvalidOperationException(compilation.Error);

                fixed (byte* bytecode = compilation.Bytecode)
                {
                    void* shaderPtr = null;
                    hr = createComputeShader(devicePtr, bytecode, (nuint)compilation.Bytecode.Length, null, &shaderPtr);
                    if (hr != 0 || shaderPtr is null)
                        ThrowDeviceFailure(hr, getDeviceRemovedReason(devicePtr), "CreateComputeShader");
                    computeShader = (IntPtr)shaderPtr;
                }

                var contextPtr = (void*)context;
                // ID3D11DeviceContext 繼承 IUnknown + ID3D11DeviceChild：Map=14、Dispatch=41、
                // CopyResource=47、CSSetUAV=68、CSSetShader=69、Flush=111。
                var setUav = (delegate* unmanaged[Stdcall]<void*, uint, uint, void**, uint*, void>)GetVTableSlot(context, 68);
                var setShader = (delegate* unmanaged[Stdcall]<void*, uint, uint, void*, void*, uint, void>)GetVTableSlot(context, 69);
                var dispatch = (delegate* unmanaged[Stdcall]<void*, uint, uint, uint, void>)GetVTableSlot(context, 41);
                var copyResource = (delegate* unmanaged[Stdcall]<void*, void*, void*, void>)GetVTableSlot(context, 47);
                var map = (delegate* unmanaged[Stdcall]<void*, void*, uint, uint, uint, D3D11MappedSubresource*, int>)GetVTableSlot(context, 14);
                var unmap = (delegate* unmanaged[Stdcall]<void*, void*, uint, void>)GetVTableSlot(context, 15);
                var flush = (delegate* unmanaged[Stdcall]<void*, void>)GetVTableSlot(context, 111);

                void* uavPtrForSet = (void*)unorderedAccessView;
                void* shaderPtrForSet = (void*)computeShader;
                setUav(contextPtr, 0, 1, &uavPtrForSet, (uint*)null);
                setShader(contextPtr, 0, 1, shaderPtrForSet, null, 0);
                flush(contextPtr);

                var samples = new List<GpuFp32Sample>(workload.Samples);
                for (int index = 0; index < workload.Samples; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long timestamp = Stopwatch.GetTimestamp();

                    dispatch(contextPtr, (uint)workload.DispatchX, 1, 1);
                    copyResource(contextPtr, stagingBufferPtr, defaultBufferPtr);
                    flush(contextPtr);

                    var mapped = new D3D11MappedSubresource();
                    hr = map(contextPtr, stagingBufferPtr, 0, D3D11MapRead, 0, &mapped);
                    if (hr != 0)
                        ThrowDeviceFailure(hr, getDeviceRemovedReason(devicePtr), "Map");

                    uint checksum;
                    try
                    {
                        if (mapped.Data == IntPtr.Zero || mapped.RowPitch < (uint)byteWidth)
                            throw new InvalidOperationException("D3D11 staging readback 指標或 row pitch 無效。");
                        var values = new ReadOnlySpan<float>((void*)mapped.Data, elementCount);
                        checksum = 2166136261u;
                        foreach (float value in values)
                        {
                            checksum ^= BitConverter.SingleToUInt32Bits(value);
                            checksum *= 16777619u;
                        }
                    }
                    finally
                    {
                        unmap(contextPtr, stagingBufferPtr, 0);
                    }

                    double elapsedSeconds = Stopwatch.GetElapsedTime(timestamp).TotalSeconds;
                    if (elapsedSeconds <= 0)
                        elapsedSeconds = 1d / Stopwatch.Frequency;
                    double flops = (double)elementCount * workload.FmaCount * 2d;
                    double throughputGflops = flops / elapsedSeconds / 1_000_000_000d;
                    double latencyMs = elapsedSeconds * 1000d;
                    samples.Add(new GpuFp32Sample(throughputGflops, latencyMs, checksum));
                }

                return new GpuFp32Run(ReadAdapterName(description), featureLevel, samples);
            }
        }
        finally
        {
            // COM 建立順序：device、default、staging、UAV、shader、context；反向釋放。
            SafeRelease(computeShader);
            SafeRelease(unorderedAccessView);
            SafeRelease(stagingBuffer);
            SafeRelease(defaultBuffer);
            SafeRelease(context);
            SafeRelease(device);
            SafeRelease(adapter);
        }
    }

    private static (IntPtr Adapter, DXGIAdapterDesc1 Description) SelectHardwareAdapter()
    {
        Guid factoryIid = IidDxgiFactory1;
        int hr = CreateDXGIFactory1(ref factoryIid, out IntPtr factory);
        if (hr != 0)
            throw new GpuUnsupportedException($"無法建立 DXGI Factory，HRESULT=0x{hr:X8}。");

        var candidates = new List<string>();
        try
        {
            unsafe
            {
                void* factoryPtr = (void*)factory;
                // IDXGIFactory1：EnumAdapters1 是 slot 12。
                var enumAdapters1 = (delegate* unmanaged[Stdcall]<void*, uint, void**, int>)GetVTableSlot(factory, 12);
                var getDesc1 = (delegate* unmanaged[Stdcall]<void*, DXGIAdapterDesc1*, int>)(IntPtr.Zero);

                for (uint index = 0; ; index++)
                {
                    void* adapterPtr = null;
                    hr = enumAdapters1(factoryPtr, index, &adapterPtr);
                    if (hr != 0)
                    {
                        candidates.Add($"enum stop=0x{(uint)hr:X8}");
                        break;
                    }

                    IntPtr adapter = (IntPtr)adapterPtr;
                    try
                    {
                        var desc = new DXGIAdapterDesc1();
                        getDesc1 = (delegate* unmanaged[Stdcall]<void*, DXGIAdapterDesc1*, int>)GetVTableSlot(adapter, 10);
                        hr = getDesc1(adapterPtr, &desc);
                        if (hr != 0)
                            continue;

                        string name = ReadAdapterName(desc);
                        bool softwareFlag = (desc.Flags & 0x2) != 0;
                        candidates.Add($"{name} flags=0x{desc.Flags:X8}");
                        bool warpName = GpuFp32ComputeService.IsWarp(name);
                        if (!softwareFlag && !warpName)
                            return (adapter, desc);
                    }
                    catch
                    {
                        SafeRelease(adapter);
                        throw;
                    }

                    SafeRelease(adapter);
                }
            }
        }
        finally
        {
            SafeRelease(factory);
        }

        throw new GpuUnsupportedException("沒有可用的 D3D11 硬體配接器；已排除 WARP 與軟體渲染。診斷：" + string.Join("；", candidates));
    }

    private static unsafe string ReadAdapterName(in DXGIAdapterDesc1 description)
    {
        // in struct 的 fixed 緩衝需要先複製到 local 才能固定。
        DXGIAdapterDesc1 copy = description;
        char* text = copy.Description;
        var span = new ReadOnlySpan<char>(text, 128);
        int end = span.IndexOf('\0');
        return end < 0 ? new string(text, 0, 128).Trim() : new string(text, 0, end);
    }

    private static void ThrowDeviceFailure(int hresult, string operation)
    {
        // 建立裝置前尚無 device 可查 device-removed reason。
        ThrowDeviceFailure(hresult, hresult, operation, false);
    }

    private static void ThrowDeviceFailure(int hresult, int removedReason, string operation) =>
        ThrowDeviceFailure(hresult, removedReason, operation, true);

    private static void ThrowDeviceFailure(int hresult, int removedReason, string operation, bool inspectRemoved)
    {
        if (inspectRemoved &&
            (removedReason == unchecked((int)0x887A0005) ||
             removedReason == unchecked((int)0x887A0006) ||
             removedReason == unchecked((int)0x887A0007) ||
             removedReason == unchecked((int)0x887A000A) ||
             hresult == unchecked((int)0x887A0005) ||
             hresult == unchecked((int)0x887A0006) ||
             hresult == unchecked((int)0x887A0007) ||
             hresult == unchecked((int)0x887A000A)))
        {
            throw new GpuDeviceRemovedException((uint)removedReason);
        }

        throw new InvalidOperationException($"{operation} 失敗，HRESULT=0x{(uint)hresult:X8}。");
    }

    private static unsafe int GetBlobSize(IntPtr blob) =>
        (int)((delegate* unmanaged[Stdcall]<void*, nuint>)GetVTableSlot(blob, 4))((void*)blob);

    private static unsafe void* GetBlobPointer(IntPtr blob) =>
        ((delegate* unmanaged[Stdcall]<void*, void*>)GetVTableSlot(blob, 3))((void*)blob);

    private static string ReadBlobString(IntPtr blob)
    {
        if (blob == IntPtr.Zero)
            return string.Empty;
        int size = GetBlobSize(blob);
        unsafe
        {
            void* pointer = GetBlobPointer(blob);
            int length = checked((int)Math.Min(size, int.MaxValue));
            return Encoding.UTF8.GetString((byte*)pointer, length).TrimEnd('\0');
        }
    }

    private static IntPtr GetVTableSlot(IntPtr comObject, int slot)
    {
        IntPtr vtable = Marshal.ReadIntPtr(comObject);
        return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
    }

    private static void SafeRelease(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero)
            _ = Marshal.Release(comObject);
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        uint driverType,
        IntPtr software,
        uint flags,
        uint[] featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out uint featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(
        byte[] source,
        UIntPtr sourceSize,
        string sourceName,
        IntPtr defines,
        IntPtr include,
        string entryPoint,
        string target,
        uint flags1,
        uint flags2,
        out IntPtr code,
        out IntPtr errorBlob);
}
