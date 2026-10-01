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
    private const uint D3D11CpuAccessWrite = 0x10000;
    private const uint D3D11MapRead = 1;
    private const uint D3D11MapWrite = 2;

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
                // CSSetShader(This, ID3D11ComputeShader*, ID3D11ClassInstance** ppClassInstances, UINT NumClassInstances)
                var setShader = (delegate* unmanaged[Stdcall]<void*, void*, void**, uint, void>)GetVTableSlot(context, 69);
                var dispatch = (delegate* unmanaged[Stdcall]<void*, uint, uint, uint, void>)GetVTableSlot(context, 41);
                var copyResource = (delegate* unmanaged[Stdcall]<void*, void*, void*, void>)GetVTableSlot(context, 47);
                var map = (delegate* unmanaged[Stdcall]<void*, void*, uint, uint, uint, D3D11MappedSubresource*, int>)GetVTableSlot(context, 14);
                var unmap = (delegate* unmanaged[Stdcall]<void*, void*, uint, void>)GetVTableSlot(context, 15);
                var flush = (delegate* unmanaged[Stdcall]<void*, void>)GetVTableSlot(context, 111);

                void* uavPtrForSet = (void*)unorderedAccessView;
                void* shaderPtrForSet = (void*)computeShader;
                setUav(contextPtr, 0, 1, &uavPtrForSet, (uint*)null);
                setShader(contextPtr, shaderPtrForSet, null, 0);
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
                    float[] copy = [];
                    try
                    {
                        if (mapped.Data == IntPtr.Zero || mapped.RowPitch < (uint)byteWidth)
                            throw new InvalidOperationException("D3D11 staging readback 指標或 row pitch 無效。");
                        var values = new ReadOnlySpan<float>((void*)mapped.Data, elementCount);
                        // 保留 readback 原始值：全零判定與 CPU 參考比對都靠它，checksum 對全零資料永遠非零
                        copy = values.ToArray();
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
                    samples.Add(new GpuFp32Sample(throughputGflops, latencyMs, checksum, copy));
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

    public static async Task<GpuVramBandwidthRun> MeasureVramAsync(GpuVramBandwidthWorkload workload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => MeasureVram(workload, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static GpuVramBandwidthRun MeasureVram(GpuVramBandwidthWorkload workload, CancellationToken cancellationToken)
    {
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
                out _,
                out context);
            if (createHr != 0)
                ThrowDeviceFailure(createHr, "D3D11CreateDevice");

            unsafe
            {
                void* devicePtr = (void*)device;
                var createBuffer = (delegate* unmanaged[Stdcall]<void*, D3D11BufferDesc*, D3D11SubresourceData*, void**, int>)GetVTableSlot(device, 3);
                var createUav = (delegate* unmanaged[Stdcall]<void*, void*, D3D11UnorderedAccessViewDesc*, void**, int>)GetVTableSlot(device, 8);
                var createComputeShader = (delegate* unmanaged[Stdcall]<void*, byte*, nuint, void*, void**, int>)GetVTableSlot(device, 18);
                var getFeatureLevel = (delegate* unmanaged[Stdcall]<void*, uint>)GetVTableSlot(device, 37);
                var getDeviceRemovedReason = (delegate* unmanaged[Stdcall]<void*, int>)GetVTableSlot(device, 39);
                uint featureLevel = getFeatureLevel(devicePtr);

                // 顯示記憶體降級梯：256 → 64 → 16 MiB；配置結果如實反映在回傳 BufferBytes。
                long[] ladder = [256L * 1024 * 1024, 64L * 1024 * 1024, 16L * 1024 * 1024];
                foreach (long candidate in ladder)
                {
                    if (candidate > workload.BufferBytes) continue;
                    GpuVramBandwidthWorkload actual = GpuVramBandwidthService.FromBuffer(
                        candidate, workload.Samples, workload.PassesPerSample);
                    int byteWidth = checked(actual.ElementCount * sizeof(uint));
                    var desc = new D3D11BufferDesc
                    {
                        ByteWidth = (uint)byteWidth,
                        Usage = D3D11UsageDefault,
                        BindFlags = D3D11BindUnorderedAccess,
                        CPUAccessFlags = 0,
                        MiscFlags = D3D11MiscBufferStructured,
                        StructureByteStride = sizeof(uint),
                    };
                    uint[] zeros = new uint[actual.ElementCount];
                    void* bufferPtr = null;
                    int hr;
                    fixed (uint* zerosPtr = zeros)
                    {
                        var init = new D3D11SubresourceData { SysMem = (IntPtr)zerosPtr };
                        hr = createBuffer(devicePtr, &desc, &init, &bufferPtr);
                    }

                    if (hr == 0 && bufferPtr is not null)
                    {
                        defaultBuffer = (IntPtr)bufferPtr;
                        workload = actual;
                        break;
                    }
                    if (bufferPtr is not null) SafeRelease((IntPtr)bufferPtr);
                    const int eOutofmemory = unchecked((int)0x8007000E);
                    const int dxgiErrorOutOfMemory = unchecked((int)0x887A0005);
                    if (hr is not (eOutofmemory or dxgiErrorOutOfMemory))
                        ThrowDeviceFailure(hr, getDeviceRemovedReason(devicePtr), "CreateBuffer");
                }

                if (defaultBuffer == IntPtr.Zero)
                    throw new GpuUnsupportedException("顯示記憶體不足：256／64／16 MiB 工作集都無法配置；本項不輸出結果。");

                int bufferBytes = checked(workload.ElementCount * sizeof(uint));
                var stagingDesc = new D3D11BufferDesc
                {
                    ByteWidth = (uint)bufferBytes,
                    Usage = D3D11UsageStaging,
                    BindFlags = 0,
                    CPUAccessFlags = D3D11CpuAccessRead,
                    MiscFlags = 0,
                    StructureByteStride = 0,
                };
                void* stagingBufferPtr = null;
                int stagingHr = createBuffer(devicePtr, &stagingDesc, null, &stagingBufferPtr);
                if (stagingHr != 0 || stagingBufferPtr is null)
                    ThrowDeviceFailure(stagingHr, getDeviceRemovedReason(devicePtr), "CreateStagingBuffer");
                stagingBuffer = (IntPtr)stagingBufferPtr;

                var uavDesc = new D3D11UnorderedAccessViewDesc
                {
                    Format = 0,
                    ViewDimension = 1,
                    FirstElement = 0,
                    NumElements = (uint)workload.ElementCount,
                    Flags = 0,
                };
                void* uavPtr = null;
                int uavHr = createUav(devicePtr, (void*)defaultBuffer, &uavDesc, &uavPtr);
                if (uavHr != 0 || uavPtr is null)
                    ThrowDeviceFailure(uavHr, getDeviceRemovedReason(devicePtr), "CreateUnorderedAccessView");
                unorderedAccessView = (IntPtr)uavPtr;

                D3D11ShaderCompilation compilation = CompileShader(
                    GpuVramBandwidthService.HlslSource,
                    "CSMain",
                    "cs_5_0");
                if (!compilation.Succeeded || compilation.Bytecode is null)
                    throw new InvalidOperationException(compilation.Error);

                fixed (byte* bytecode = compilation.Bytecode)
                {
                    void* shaderPtr = null;
                    int shaderHr = createComputeShader(devicePtr, bytecode, (nuint)compilation.Bytecode.Length, null, &shaderPtr);
                    if (shaderHr != 0 || shaderPtr is null)
                        ThrowDeviceFailure(shaderHr, getDeviceRemovedReason(devicePtr), "CreateComputeShader");
                    computeShader = (IntPtr)shaderPtr;
                }

                var contextPtr = (void*)context;
                var setUav = (delegate* unmanaged[Stdcall]<void*, uint, uint, void**, uint*, void>)GetVTableSlot(context, 68);
                var setShader = (delegate* unmanaged[Stdcall]<void*, void*, void**, uint, void>)GetVTableSlot(context, 69);
                var dispatch = (delegate* unmanaged[Stdcall]<void*, uint, uint, uint, void>)GetVTableSlot(context, 41);
                var copyResource = (delegate* unmanaged[Stdcall]<void*, void*, void*, void>)GetVTableSlot(context, 47);
                var map = (delegate* unmanaged[Stdcall]<void*, void*, uint, uint, uint, D3D11MappedSubresource*, int>)GetVTableSlot(context, 14);
                var unmap = (delegate* unmanaged[Stdcall]<void*, void*, uint, void>)GetVTableSlot(context, 15);
                var flush = (delegate* unmanaged[Stdcall]<void*, void>)GetVTableSlot(context, 111);

                void* uavPtrForSet = (void*)unorderedAccessView;
                void* shaderPtrForSet = (void*)computeShader;
                setUav(contextPtr, 0, 1, &uavPtrForSet, (uint*)null);
                setShader(contextPtr, shaderPtrForSet, null, 0);
                flush(contextPtr);

                var samples = new List<GpuVramBandwidthSample>(workload.Samples);
                for (int index = 0; index < workload.Samples; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long timestamp = Stopwatch.GetTimestamp();
                    for (int pass = 0; pass < workload.PassesPerSample; pass++)
                    {
                        dispatch(contextPtr, (uint)workload.DispatchX, (uint)workload.DispatchY, 1);
                    }

                    flush(contextPtr);
                    double elapsedSeconds = Stopwatch.GetElapsedTime(timestamp).TotalSeconds;
                    if (elapsedSeconds <= 0)
                        elapsedSeconds = 1d / Stopwatch.Frequency;

                    copyResource(contextPtr, stagingBufferPtr, (void*)defaultBuffer);
                    flush(contextPtr);
                    var mapped = new D3D11MappedSubresource();
                    int mapHr = map(contextPtr, stagingBufferPtr, 0, D3D11MapRead, 0, &mapped);
                    if (mapHr != 0)
                        ThrowDeviceFailure(mapHr, getDeviceRemovedReason(devicePtr), "Map");

                    uint checksum;
                    uint[] window;
                    try
                    {
                        if (mapped.Data == IntPtr.Zero || mapped.RowPitch < (uint)bufferBytes)
                            throw new InvalidOperationException("D3D11 staging readback 指標或 row pitch 無效。");
                        var windowSpan = new ReadOnlySpan<uint>((void*)mapped.Data, GpuVramBandwidthService.WindowLength);
                        window = windowSpan.ToArray();
                        checksum = 2166136261u;
                        foreach (uint value in windowSpan)
                        {
                            checksum ^= value;
                            checksum *= 16777619u;
                        }
                    }
                    finally
                    {
                        unmap(contextPtr, stagingBufferPtr, 0);
                    }

                    double bytes = (double)workload.PassesPerSample * bufferBytes * 2d;
                    double bandwidthGBps = bytes / elapsedSeconds / 1_000_000_000d;
                    double latencyMs = elapsedSeconds * 1000d;
                    samples.Add(new GpuVramBandwidthSample(bandwidthGBps, latencyMs, checksum, window));
                }

                return new GpuVramBandwidthRun(ReadAdapterName(description), featureLevel, workload.BufferBytes, samples);
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

    public static async Task<GpuPcieTransferRun> MeasurePcieTransferAsync(GpuPcieTransferWorkload workload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => MeasurePcieTransfer(workload, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<GpuDispatchJitterRun> MeasureDispatchJitterAsync(GpuDispatchJitterWorkload workload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => MeasureDispatchJitter(workload, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static GpuDispatchJitterRun MeasureDispatchJitter(GpuDispatchJitterWorkload workload, CancellationToken cancellationToken)
    {
        (IntPtr adapter, DXGIAdapterDesc1 description) = SelectHardwareAdapter();
        IntPtr device = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        IntPtr defaultBuffer = IntPtr.Zero;
        IntPtr readStaging = IntPtr.Zero;
        IntPtr computeShader = IntPtr.Zero;
        IntPtr unorderedAccessView = IntPtr.Zero;
        try
        {
            uint[] featureLevels = [0x0B100, 0x0B000];
            int createHr = D3D11CreateDevice(
                adapter,
                0,
                IntPtr.Zero,
                0,
                featureLevels,
                (uint)featureLevels.Length,
                7,
                out device,
                out _,
                out context);
            if (createHr != 0)
                ThrowDeviceFailure(createHr, "D3D11CreateDevice");

            unsafe
            {
                void* devicePtr = (void*)device;
                var createBuffer = (delegate* unmanaged[Stdcall]<void*, D3D11BufferDesc*, D3D11SubresourceData*, void**, int>)GetVTableSlot(device, 3);
                var createUav = (delegate* unmanaged[Stdcall]<void*, void*, D3D11UnorderedAccessViewDesc*, void**, int>)GetVTableSlot(device, 8);
                var createComputeShader = (delegate* unmanaged[Stdcall]<void*, byte*, nuint, void*, void**, int>)GetVTableSlot(device, 18);
                var getFeatureLevel = (delegate* unmanaged[Stdcall]<void*, uint>)GetVTableSlot(device, 37);
                var getDeviceRemovedReason = (delegate* unmanaged[Stdcall]<void*, int>)GetVTableSlot(device, 39);
                uint featureLevel = getFeatureLevel(devicePtr);

                const int sentinelCount = 8;
                int bufferBytes = sentinelCount * sizeof(uint);
                var defaultDesc = new D3D11BufferDesc
                {
                    ByteWidth = (uint)bufferBytes,
                    Usage = D3D11UsageDefault,
                    BindFlags = D3D11BindUnorderedAccess,
                    CPUAccessFlags = 0,
                    MiscFlags = D3D11MiscBufferStructured,
                    StructureByteStride = sizeof(uint),
                };
                uint[] zeros = new uint[sentinelCount];
                fixed (uint* zerosPtr = zeros)
                {
                    var init = new D3D11SubresourceData { SysMem = (IntPtr)zerosPtr };
                    int hr = createBuffer(devicePtr, &defaultDesc, &init, (void**)&defaultBuffer);
                    if (hr != 0 || defaultBuffer == IntPtr.Zero)
                        ThrowDeviceFailure(hr, getDeviceRemovedReason(devicePtr), "CreateBuffer");
                }

                var stagingDesc = new D3D11BufferDesc
                {
                    ByteWidth = (uint)bufferBytes,
                    Usage = D3D11UsageStaging,
                    BindFlags = 0,
                    CPUAccessFlags = D3D11CpuAccessRead,
                    MiscFlags = 0,
                    StructureByteStride = 0,
                };
                int stagingHr = createBuffer(devicePtr, &stagingDesc, null, (void**)&readStaging);
                if (stagingHr != 0 || readStaging == IntPtr.Zero)
                    ThrowDeviceFailure(stagingHr, getDeviceRemovedReason(devicePtr), "CreateStagingBuffer");

                var uavDesc = new D3D11UnorderedAccessViewDesc
                {
                    Format = 0,
                    ViewDimension = 1,
                    FirstElement = 0,
                    NumElements = (uint)sentinelCount,
                    Flags = 0,
                };
                int uavHr = createUav(devicePtr, (void*)defaultBuffer, &uavDesc, (void**)&unorderedAccessView);
                if (uavHr != 0 || unorderedAccessView == IntPtr.Zero)
                    ThrowDeviceFailure(uavHr, getDeviceRemovedReason(devicePtr), "CreateUnorderedAccessView");

                D3D11ShaderCompilation compilation = CompileShader(
                    GpuDispatchJitterService.HlslSource,
                    "CSMain",
                    "cs_5_0");
                if (!compilation.Succeeded || compilation.Bytecode is null)
                    throw new InvalidOperationException(compilation.Error);

                fixed (byte* bytecode = compilation.Bytecode)
                {
                    int shaderHr = createComputeShader(devicePtr, bytecode, (nuint)compilation.Bytecode.Length, null, (void**)&computeShader);
                    if (shaderHr != 0 || computeShader == IntPtr.Zero)
                        ThrowDeviceFailure(shaderHr, getDeviceRemovedReason(devicePtr), "CreateComputeShader");
                }

                var contextPtr = (void*)context;
                var setUav = (delegate* unmanaged[Stdcall]<void*, uint, uint, void**, uint*, void>)GetVTableSlot(context, 68);
                var setShader = (delegate* unmanaged[Stdcall]<void*, void*, void**, uint, void>)GetVTableSlot(context, 69);
                var dispatch = (delegate* unmanaged[Stdcall]<void*, uint, uint, uint, void>)GetVTableSlot(context, 41);
                var copyResource = (delegate* unmanaged[Stdcall]<void*, void*, void*, void>)GetVTableSlot(context, 47);
                var map = (delegate* unmanaged[Stdcall]<void*, void*, uint, uint, uint, D3D11MappedSubresource*, int>)GetVTableSlot(context, 14);
                var unmap = (delegate* unmanaged[Stdcall]<void*, void*, uint, void>)GetVTableSlot(context, 15);
                var flush = (delegate* unmanaged[Stdcall]<void*, void>)GetVTableSlot(context, 111);

                void* uavPtr = (void*)unorderedAccessView;
                void* shaderPtr = (void*)computeShader;
                setUav(contextPtr, 0, 1, &uavPtr, (uint*)null);
                setShader(contextPtr, shaderPtr, null, 0);
                for (int index = 0; index < 8; index++)
                    dispatch(contextPtr, 1, 1, 1);
                flush(contextPtr);

                var samples = new List<GpuDispatchJitterSample>(workload.Samples);
                for (int sampleIndex = 0; sampleIndex < workload.Samples; sampleIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long timestamp = Stopwatch.GetTimestamp();
                    for (int dispatchIndex = 0; dispatchIndex < workload.DispatchesPerSample; dispatchIndex++)
                        dispatch(contextPtr, 1, 1, 1);
                    flush(contextPtr);
                    double elapsedSeconds = Stopwatch.GetElapsedTime(timestamp).TotalSeconds;
                    if (elapsedSeconds <= 0)
                        elapsedSeconds = 1d / Stopwatch.Frequency;

                    copyResource(contextPtr, (void*)readStaging, (void*)defaultBuffer);
                    flush(contextPtr);
                    var mapped = new D3D11MappedSubresource();
                    int mapHr = map(contextPtr, (void*)readStaging, 0, D3D11MapRead, 0, &mapped);
                    if (mapHr != 0)
                        ThrowDeviceFailure(mapHr, getDeviceRemovedReason(devicePtr), "Map");

                    uint checksum;
                    uint sentinel;
                    try
                    {
                        if (mapped.Data == IntPtr.Zero || mapped.RowPitch < (uint)bufferBytes)
                            throw new InvalidOperationException("D3D11 dispatch readback 指標或 row pitch 無效。");
                        var values = new ReadOnlySpan<uint>((void*)mapped.Data, sentinelCount);
                        sentinel = values[0];
                        checksum = 2166136261u;
                        foreach (uint value in values)
                        {
                            checksum ^= value;
                            checksum *= 16777619u;
                        }
                    }
                    finally
                    {
                        unmap(contextPtr, (void*)readStaging, 0);
                    }

                    samples.Add(new GpuDispatchJitterSample(
                        elapsedSeconds * 1_000_000d / workload.DispatchesPerSample,
                        checksum,
                        sentinel));
                }

                return new GpuDispatchJitterRun(
                    ReadAdapterName(description),
                    featureLevel,
                    workload.DispatchesPerSample,
                    description.DedicatedVideoMemory,
                    samples);
            }
        }
        finally
        {
            // COM 建立順序：device、default、readStaging、UAV、shader、context；反向釋放。
            SafeRelease(computeShader);
            SafeRelease(unorderedAccessView);
            SafeRelease(readStaging);
            SafeRelease(defaultBuffer);
            SafeRelease(context);
            SafeRelease(device);
            SafeRelease(adapter);
        }
    }

    private static GpuPcieTransferRun MeasurePcieTransfer(GpuPcieTransferWorkload workload, CancellationToken cancellationToken)
    {
        (IntPtr adapter, DXGIAdapterDesc1 description) = SelectHardwareAdapter();
        IntPtr device = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        IntPtr defaultBuffer = IntPtr.Zero;
        IntPtr readStaging = IntPtr.Zero;
        IntPtr writeStaging = IntPtr.Zero;
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
                out _,
                out context);
            if (createHr != 0)
                ThrowDeviceFailure(createHr, "D3D11CreateDevice");

            unsafe
            {
                void* devicePtr = (void*)device;
                var createBuffer = (delegate* unmanaged[Stdcall]<void*, D3D11BufferDesc*, D3D11SubresourceData*, void**, int>)GetVTableSlot(device, 3);
                var getFeatureLevel = (delegate* unmanaged[Stdcall]<void*, uint>)GetVTableSlot(device, 37);
                var getDeviceRemovedReason = (delegate* unmanaged[Stdcall]<void*, int>)GetVTableSlot(device, 39);
                uint featureLevel = getFeatureLevel(devicePtr);

                // 降級梯：256 → 64 → 16 MiB；default+讀 staging+寫 staging 三個都要配置成功。
                long[] ladder = [256L * 1024 * 1024, 64L * 1024 * 1024, 16L * 1024 * 1024];
                GpuPcieTransferWorkload chosen = workload;
                foreach (long candidate in ladder)
                {
                    if (candidate > workload.BufferBytes) continue;
                    GpuPcieTransferWorkload candidateWorkload = GpuPcieTransferService.FromBuffer(candidate, workload.Samples);
                    int byteWidth = checked(candidateWorkload.ElementCount * sizeof(uint));
                    IntPtr candidateDefault = IntPtr.Zero;
                    IntPtr candidateRead = IntPtr.Zero;
                    IntPtr candidateWrite = IntPtr.Zero;
                    try
                    {
                        candidateDefault = CreateStagingBuffer(
                            createBuffer, devicePtr, getDeviceRemovedReason, byteWidth,
                            D3D11UsageDefault, 0, withZeros: true);
                        candidateRead = CreateStagingBuffer(
                            createBuffer, devicePtr, getDeviceRemovedReason, byteWidth,
                            D3D11UsageStaging, D3D11CpuAccessRead, withZeros: false);
                        candidateWrite = CreateStagingBuffer(
                            createBuffer, devicePtr, getDeviceRemovedReason, byteWidth,
                            D3D11UsageStaging, D3D11CpuAccessWrite, withZeros: false);
                        defaultBuffer = candidateDefault;
                        readStaging = candidateRead;
                        writeStaging = candidateWrite;
                        chosen = candidateWorkload;
                        candidateDefault = candidateRead = candidateWrite = IntPtr.Zero;
                    }
                    catch (GpuOutOfMemoryException)
                    {
                        SafeRelease(candidateDefault);
                        SafeRelease(candidateRead);
                        SafeRelease(candidateWrite);
                    }

                    if (defaultBuffer != IntPtr.Zero)
                        break;
                }

                if (defaultBuffer == IntPtr.Zero)
                    throw new GpuUnsupportedException("顯示記憶體不足：256／64／16 MiB 工作集都無法配置；本項不輸出結果。");

                var contextPtr = (void*)context;
                var copyResource = (delegate* unmanaged[Stdcall]<void*, void*, void*, void>)GetVTableSlot(context, 47);
                var map = (delegate* unmanaged[Stdcall]<void*, void*, uint, uint, uint, D3D11MappedSubresource*, int>)GetVTableSlot(context, 14);
                var unmap = (delegate* unmanaged[Stdcall]<void*, void*, uint, void>)GetVTableSlot(context, 15);
                var flush = (delegate* unmanaged[Stdcall]<void*, void>)GetVTableSlot(context, 111);

                int bufferBytes = checked(chosen.ElementCount * sizeof(uint));
                var uploads = new List<GpuPcieTransferSample>(chosen.Samples);
                for (int index = 0; index < chosen.Samples; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mapped = new D3D11MappedSubresource();
                    int mapHr = map(contextPtr, (void*)writeStaging, 0, D3D11MapWrite, 0, &mapped);
                    if (mapHr != 0)
                        ThrowDeviceFailure(mapHr, getDeviceRemovedReason(devicePtr), "Map(writeStaging)");
                    try
                    {
                        if (mapped.Data == IntPtr.Zero || mapped.RowPitch < (uint)bufferBytes)
                            throw new InvalidOperationException("D3D11 寫入 staging 指標或 row pitch 無效。");
                        var span = new Span<uint>((void*)mapped.Data, chosen.ElementCount);
                        for (int i = 0; i < span.Length; i++)
                            span[i] = GpuPcieTransferService.UploadPatternBase ^ (uint)i;
                    }
                    finally
                    {
                        unmap(contextPtr, (void*)writeStaging, 0);
                    }

                    long timestamp = Stopwatch.GetTimestamp();
                    copyResource(contextPtr, (void*)defaultBuffer, (void*)writeStaging);
                    flush(contextPtr);
                    double elapsedSeconds = Stopwatch.GetElapsedTime(timestamp).TotalSeconds;
                    if (elapsedSeconds <= 0)
                        elapsedSeconds = 1d / Stopwatch.Frequency;
                    uploads.Add(new GpuPcieTransferSample(
                        bufferBytes / elapsedSeconds / 1_000_000_000d,
                        elapsedSeconds * 1000d));
                }

                var downloads = new List<GpuPcieTransferSample>(chosen.Samples);
                for (int index = 0; index < chosen.Samples; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long timestamp = Stopwatch.GetTimestamp();
                    copyResource(contextPtr, (void*)readStaging, (void*)defaultBuffer);
                    flush(contextPtr);
                    double elapsedSeconds = Stopwatch.GetElapsedTime(timestamp).TotalSeconds;
                    if (elapsedSeconds <= 0)
                        elapsedSeconds = 1d / Stopwatch.Frequency;

                    var mapped = new D3D11MappedSubresource();
                    int mapHr = map(contextPtr, (void*)readStaging, 0, D3D11MapRead, 0, &mapped);
                    if (mapHr != 0)
                        ThrowDeviceFailure(mapHr, getDeviceRemovedReason(devicePtr), "Map(readStaging)");
                    uint checksum;
                    uint[] window;
                    try
                    {
                        if (mapped.Data == IntPtr.Zero || mapped.RowPitch < (uint)bufferBytes)
                            throw new InvalidOperationException("D3D11 讀取 staging 指標或 row pitch 無效。");
                        var windowSpan = new ReadOnlySpan<uint>((void*)mapped.Data, GpuPcieTransferService.WindowLength);
                        window = windowSpan.ToArray();
                        checksum = 2166136261u;
                        foreach (uint value in windowSpan)
                        {
                            checksum ^= value;
                            checksum *= 16777619u;
                        }
                    }
                    finally
                    {
                        unmap(contextPtr, (void*)readStaging, 0);
                    }

                    downloads.Add(new GpuPcieTransferSample(
                        bufferBytes / elapsedSeconds / 1_000_000_000d,
                        elapsedSeconds * 1000d,
                        checksum,
                        window));
                }

                return new GpuPcieTransferRun(
                    ReadAdapterName(description),
                    featureLevel,
                    chosen.BufferBytes,
                    description.DedicatedVideoMemory,
                    uploads,
                    downloads);
            }
        }
        finally
        {
            // COM 建立順序：device、default、readStaging、writeStaging、context；反向釋放。
            SafeRelease(writeStaging);
            SafeRelease(readStaging);
            SafeRelease(defaultBuffer);
            SafeRelease(context);
            SafeRelease(device);
            SafeRelease(adapter);
        }
    }

    private static unsafe IntPtr CreateStagingBuffer(
        delegate* unmanaged[Stdcall]<void*, D3D11BufferDesc*, D3D11SubresourceData*, void**, int> createBuffer,
        void* devicePtr,
        delegate* unmanaged[Stdcall]<void*, int> getDeviceRemovedReason,
        int byteWidth,
        uint usage,
        uint cpuAccessFlags,
        bool withZeros)
    {
        var desc = new D3D11BufferDesc
        {
            ByteWidth = (uint)byteWidth,
            Usage = usage,
            BindFlags = 0,
            CPUAccessFlags = cpuAccessFlags,
            MiscFlags = 0,
            StructureByteStride = 0,
        };
        void* bufferPtr = null;
        int hr;
        if (withZeros)
        {
            uint[] zeros = new uint[byteWidth / sizeof(uint)];
            fixed (uint* zerosPtr = zeros)
            {
                var init = new D3D11SubresourceData { SysMem = (IntPtr)zerosPtr };
                hr = createBuffer(devicePtr, &desc, &init, &bufferPtr);
            }
        }
        else
        {
            hr = createBuffer(devicePtr, &desc, null, &bufferPtr);
        }

        if (hr == 0 && bufferPtr is not null)
            return (IntPtr)bufferPtr;
        const int eOutofmemory = unchecked((int)0x8007000E);
        const int dxgiErrorOutOfMemory = unchecked((int)0x887A0005);
        if (hr is eOutofmemory or dxgiErrorOutOfMemory)
            throw new GpuOutOfMemoryException($"緩衝配置 {byteWidth} bytes 記憶體不足，HRESULT=0x{hr:X8}。");
        ThrowDeviceFailure(hr, getDeviceRemovedReason(devicePtr), "CreateBuffer");
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Viewport
    {
        public float TopLeftX;
        public float TopLeftY;
        public float Width;
        public float Height;
        public float MinDepth;
        public float MaxDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11SamplerDesc
    {
        public uint Filter;
        public uint AddressU;
        public uint AddressV;
        public uint AddressW;
        public float MipLODBias;
        public uint MaxAnisotropy;
        public uint ComparisonFunc;
        public float Border0, Border1, Border2, Border3;
        public float MinLOD;
        public float MaxLOD;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11RenderTargetViewDesc
    {
        public uint Format;
        public uint ViewDimension;
        public uint MipSlice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11ShaderResourceViewDesc
    {
        public uint Format;
        public uint ViewDimension;
        public uint MostDetailedMip;
        public uint MipLevels;
    }

    public static async Task<GpuRasterMeasurement> MeasureRasterAsync(
        GpuRasterContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => MeasureRaster(context), cancellationToken).ConfigureAwait(false);
    }

    private static GpuRasterMeasurement MeasureRaster(GpuRasterContext context)
    {
        GpuRasterWorkload workload = context.Workload;
        int texBytes = workload.TextureSize * workload.TextureSize * 4;
        byte[] textureData = new byte[texBytes];
        uint prng = 0x12345678u;
        for (int pixel = 0; pixel < texBytes / 4; pixel++)
        {
            prng ^= prng << 13; prng ^= prng >> 17; prng ^= prng << 5;
            bool checker = (pixel / 64) % 2 == 0;
            textureData[pixel * 4 + 0] = (byte)((prng & 0x3F) + (uint)(checker ? 96 : 32));
            textureData[pixel * 4 + 1] = (byte)((prng >> 8) & 0x7F);
            textureData[pixel * 4 + 2] = (byte)((prng >> 16) & 0x7F);
            textureData[pixel * 4 + 3] = 0xFF;
        }

        (IntPtr adapter, DXGIAdapterDesc1 description) = SelectHardwareAdapter();
        IntPtr device = IntPtr.Zero;
        IntPtr contextPtr = IntPtr.Zero;
        var created = new List<IntPtr>();
        try
        {
            uint[] featureLevels = [0x0B100, 0x0B000];
            int hr = D3D11CreateDevice(
                adapter, 0, IntPtr.Zero, 0, featureLevels, (uint)featureLevels.Length, 7,
                out device, out uint featureLevel, out contextPtr);
            if (hr != 0)
                ThrowDeviceFailure(hr, "D3D11CreateDevice");

            unsafe
            {
                void* dev = (void*)device;
                void* ctx = (void*)contextPtr;
                var createTexture2D = (delegate* unmanaged[Stdcall]<void*, D3D11Texture2DDesc*, D3D11SubresourceData*, void**, int>)GetVTableSlot(device, 5);
                var createSrv = (delegate* unmanaged[Stdcall]<void*, void*, D3D11ShaderResourceViewDesc*, void**, int>)GetVTableSlot(device, 7);
                var createRtv = (delegate* unmanaged[Stdcall]<void*, void*, D3D11RenderTargetViewDesc*, void**, int>)GetVTableSlot(device, 9);
                var createVs = (delegate* unmanaged[Stdcall]<void*, byte*, nuint, void*, void**, int>)GetVTableSlot(device, 12);
                var createPs = (delegate* unmanaged[Stdcall]<void*, byte*, nuint, void*, void**, int>)GetVTableSlot(device, 15);
                var createSampler = (delegate* unmanaged[Stdcall]<void*, D3D11SamplerDesc*, void**, int>)GetVTableSlot(device, 23);
                var getRemoved = (delegate* unmanaged[Stdcall]<void*, int>)GetVTableSlot(device, 39);

                var rtDesc = new D3D11Texture2DDesc
                {
                    Width = (uint)workload.Width, Height = (uint)workload.Height, MipLevels = 1, ArraySize = 1,
                    Format = 87, SampleCount = 1, SampleQuality = 0, Usage = 0,
                    BindFlags = 0x20, CPUAccessFlags = 0, MiscFlags = 0,
                };
                void* rtPtr = null;
                hr = createTexture2D(dev, &rtDesc, null, &rtPtr);
                if (hr != 0) ThrowDeviceFailure(hr, getRemoved(dev), "CreateTexture2D(RT)");
                IntPtr renderTarget = (IntPtr)rtPtr; created.Add(renderTarget);

                var stagingDesc = rtDesc;
                stagingDesc.Usage = 3; stagingDesc.BindFlags = 0; stagingDesc.CPUAccessFlags = 0x20000;
                void* stagingPtr = null;
                hr = createTexture2D(dev, &stagingDesc, null, &stagingPtr);
                if (hr != 0) ThrowDeviceFailure(hr, getRemoved(dev), "CreateTexture2D(Staging)");
                IntPtr staging = (IntPtr)stagingPtr; created.Add(staging);

                var texDesc = new D3D11Texture2DDesc
                {
                    Width = (uint)workload.TextureSize, Height = (uint)workload.TextureSize, MipLevels = 1, ArraySize = 1,
                    Format = 87, SampleCount = 1, SampleQuality = 0, Usage = 0,
                    BindFlags = 0x8, CPUAccessFlags = 0, MiscFlags = 0,
                };
                var texData = new D3D11SubresourceData { SysMem = IntPtr.Zero, SysMemPitch = (uint)(workload.TextureSize * 4), SysMemSlicePitch = 0 };
                IntPtr texture;
                void* texPtr = null;
                fixed (byte* texBytesPtr = textureData)
                {
                    texData.SysMem = (IntPtr)texBytesPtr;
                    hr = createTexture2D(dev, &texDesc, &texData, &texPtr);
                }
                if (hr != 0) ThrowDeviceFailure(hr, getRemoved(dev), "CreateTexture2D(Data)");
                texture = (IntPtr)texPtr; created.Add(texture);

                var rtvDesc = new D3D11RenderTargetViewDesc { Format = 87, ViewDimension = 4, MipSlice = 0 };
                void* rtvPtr = null;
                hr = createRtv(dev, rtPtr, &rtvDesc, &rtvPtr);
                if (hr != 0) ThrowDeviceFailure(hr, getRemoved(dev), "CreateRenderTargetView");
                IntPtr rtv = (IntPtr)rtvPtr; created.Add(rtv);

                var srvDesc = new D3D11ShaderResourceViewDesc { Format = 87, ViewDimension = 4, MostDetailedMip = 0, MipLevels = 1 };
                void* srvPtr = null;
                hr = createSrv(dev, texPtr, &srvDesc, &srvPtr);
                if (hr != 0) ThrowDeviceFailure(hr, getRemoved(dev), "CreateShaderResourceView");
                IntPtr srv = (IntPtr)srvPtr; created.Add(srv);

                var samplerDesc = new D3D11SamplerDesc
                {
                    Filter = 0x15, AddressU = 3, AddressV = 3, AddressW = 3, MipLODBias = 0,
                    MaxAnisotropy = 1, ComparisonFunc = 1, Border0 = 0, Border1 = 0, Border2 = 0, Border3 = 0,
                    MinLOD = 0, MaxLOD = 3.402823466e+38f,
                };
                void* samplerPtr = null;
                hr = createSampler(dev, &samplerDesc, &samplerPtr);
                if (hr != 0) ThrowDeviceFailure(hr, getRemoved(dev), "CreateSamplerState");
                IntPtr sampler = (IntPtr)samplerPtr; created.Add(sampler);

                var vsCompilation = CompileShader(GpuRasterTextureService.HlslSource, "VSMain", "vs_5_0");
                if (!vsCompilation.Succeeded || vsCompilation.Bytecode is null)
                    throw new InvalidOperationException(vsCompilation.Error);
                var fillCompilation = CompileShader(GpuRasterTextureService.HlslSource, "PSFill", "ps_5_0");
                var singleCompilation = CompileShader(GpuRasterTextureService.HlslSource, "PSTextureSingle", "ps_5_0");
                var multiCompilation = CompileShader(GpuRasterTextureService.HlslSource, "PSTextureMulti", "ps_5_0");
                if (!fillCompilation.Succeeded || !singleCompilation.Succeeded || !multiCompilation.Succeeded)
                    throw new InvalidOperationException(
                        fillCompilation.Error + singleCompilation.Error + multiCompilation.Error);

                void* vsPtr = null;
                fixed (byte* vsCode = vsCompilation.Bytecode)
                {
                    hr = createVs(dev, vsCode, (nuint)vsCompilation.Bytecode.Length, null, &vsPtr);
                }
                if (hr != 0) ThrowDeviceFailure(hr, getRemoved(dev), "CreateVertexShader");
                IntPtr vertexShader = (IntPtr)vsPtr; created.Add(vertexShader);

                IntPtr fillShader = CreateRasterPs(dev, createPs, fillCompilation, created);
                IntPtr singleShader = CreateRasterPs(dev, createPs, singleCompilation, created);
                IntPtr multiShader = CreateRasterPs(dev, createPs, multiCompilation, created);

                // ID3D11DeviceContext：PSSetSR=8、PSSetShader=9、PSSetSamplers=10、VSSetShader=11、Draw=13、
                // Map=14、Unmap=15、IASetPrimitiveTopology=24、OMSetRenderTargets=33、RSSetViewports=44、
                // CopyResource=47、ClearRenderTargetView=50、Flush=111（以 Windows SDK d3d11.h 逐項核對）。
                var psSetSrv = (delegate* unmanaged[Stdcall]<void*, uint, uint, void**, void>)GetVTableSlot(contextPtr, 8);
                var psSetShader = (delegate* unmanaged[Stdcall]<void*, void*, void**, uint, void>)GetVTableSlot(contextPtr, 9);
                var psSetSampler = (delegate* unmanaged[Stdcall]<void*, uint, uint, void**, void>)GetVTableSlot(contextPtr, 10);
                var vsSetShader = (delegate* unmanaged[Stdcall]<void*, void*, void**, uint, void>)GetVTableSlot(contextPtr, 11);
                var draw = (delegate* unmanaged[Stdcall]<void*, uint, uint, void>)GetVTableSlot(contextPtr, 13);
                var map = (delegate* unmanaged[Stdcall]<void*, void*, uint, uint, uint, D3D11MappedSubresource*, int>)GetVTableSlot(contextPtr, 14);
                var unmap = (delegate* unmanaged[Stdcall]<void*, void*, uint, void>)GetVTableSlot(contextPtr, 15);
                var setTopology = (delegate* unmanaged[Stdcall]<void*, uint, void>)GetVTableSlot(contextPtr, 24);
                var omSetRtv = (delegate* unmanaged[Stdcall]<void*, uint, void**, void*, void>)GetVTableSlot(contextPtr, 33);
                var rsSetViewport = (delegate* unmanaged[Stdcall]<void*, uint, D3D11Viewport*, void>)GetVTableSlot(contextPtr, 44);
                var copyResource = (delegate* unmanaged[Stdcall]<void*, void*, void*, void>)GetVTableSlot(contextPtr, 47);
                var clearRtv = (delegate* unmanaged[Stdcall]<void*, void*, float*, void>)GetVTableSlot(contextPtr, 50);
                var flush = (delegate* unmanaged[Stdcall]<void*, void>)GetVTableSlot(contextPtr, 111);

                var viewport = new D3D11Viewport { TopLeftX = 0, TopLeftY = 0, Width = workload.Width, Height = workload.Height, MinDepth = 0, MaxDepth = 1 };
                rsSetViewport(ctx, 1, &viewport);
                setTopology(ctx, 4); // D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST
                void* rtvForSet = (void*)rtv;
                omSetRtv(ctx, 1, &rtvForSet, null);
                void* srvForSet = (void*)srv;
                psSetSrv(ctx, 0, 1, &srvForSet);
                void* samplerForSet = (void*)sampler;
                psSetSampler(ctx, 0, 1, &samplerForSet);
                void* vsForSet = (void*)vertexShader;
                vsSetShader(ctx, vsForSet, null, 0);
                Span<float> clearColorSpan = [0f, 0f, 0f, 1f];
                fixed (float* clearColor = clearColorSpan)
                {
                    clearRtv(ctx, rtvPtr, clearColor);
                }
                flush(ctx);

                var results = new List<GpuRasterScenarioResult>();
                (IntPtr Shader, string Id)[] scenarioShaders =
                    [(fillShader, "fill"), (singleShader, "texture-single"), (multiShader, "texture-8tap")];
                int totalSteps = scenarioShaders.Length * (workload.WarmupSamples + workload.MeasureSamples);
                int completed = 0;
                foreach ((IntPtr scenarioShader, string scenarioId) in scenarioShaders)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    void* psForSet = (void*)scenarioShader;
                    psSetShader(ctx, psForSet, null, 0);
                    flush(ctx);

                    var samples = new List<GpuRasterSample>();
                    int passes = workload.WarmupSamples + workload.MeasureSamples;
                    for (int pass = 0; pass < passes; pass++)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        bool warmup = pass < workload.WarmupSamples;
                        context.Progress.Report(new DeepBenchProgress(
                            GpuRasterTextureService.TestId, completed, Math.Max(totalSteps, 1),
                            0.05 + 0.92 * completed / Math.Max(totalSteps, 1),
                            warmup ? $"warmup {scenarioId}" : $"量測 {scenarioId}（第 {pass - workload.WarmupSamples + 1}/{workload.MeasureSamples} 樣本）"));
                        long timestamp = Stopwatch.GetTimestamp();
                        for (int frame = 0; frame < workload.FramesPerSample; frame++)
                            draw(ctx, 3, 0);
                        flush(ctx);
                        double elapsedSeconds = Stopwatch.GetElapsedTime(timestamp).TotalSeconds;
                        if (elapsedSeconds <= 0)
                            elapsedSeconds = 1d / Stopwatch.Frequency;
                        double gpix = (double)workload.Width * workload.Height * workload.FramesPerSample / elapsedSeconds / 1_000_000_000d;
                        if (!warmup)
                            samples.Add(new GpuRasterSample(gpix, elapsedSeconds * 1000d / workload.FramesPerSample));
                        completed++;
                    }

                    copyResource(ctx, stagingPtr, rtPtr);
                    flush(ctx);
                    uint checksum = VerifyRasterReadback(
                        map, unmap, ctx, stagingPtr, workload, scenarioId == "fill");

                    results.Add(new GpuRasterScenarioResult(
                        scenarioId,
                        new GpuRasterRun(
                            ReadAdapterName(description), featureLevel, workload.Width, workload.Height,
                            samples, checksum)));
                }

                context.Progress.Report(new DeepBenchProgress(
                    GpuRasterTextureService.TestId, totalSteps, Math.Max(totalSteps, 1), 0.98, "光柵場景完成"));
                return new GpuRasterMeasurement(results);
            }
        }
        finally
        {
            for (int index = created.Count - 1; index >= 0; index--)
                SafeRelease(created[index]);
            SafeRelease(contextPtr);
            SafeRelease(device);
            SafeRelease(adapter);
        }
    }

    private static unsafe IntPtr CreateRasterPs(
        void* device,
        delegate* unmanaged[Stdcall]<void*, byte*, nuint, void*, void**, int> createPs,
        D3D11ShaderCompilation compilation,
        List<IntPtr> created)
    {
        void* shaderPtr = null;
        fixed (byte* bytecode = compilation.Bytecode)
        {
            int hr = createPs(device, bytecode, (nuint)compilation.Bytecode.Length, null, &shaderPtr);
            if (hr != 0 || shaderPtr is null)
                ThrowDeviceFailure(hr, "CreatePixelShader");
        }
        IntPtr shader = (IntPtr)shaderPtr;
        created.Add(shader);
        return shader;
    }

    /// <summary>readback 驗證：alpha 全 255、checksum 非零；fill 場景另對 CPU 參考色逐位元組驗證。</summary>
    private static unsafe uint VerifyRasterReadback(
        delegate* unmanaged[Stdcall]<void*, void*, uint, uint, uint, D3D11MappedSubresource*, int> map,
        delegate* unmanaged[Stdcall]<void*, void*, uint, void> unmap,
        void* ctx,
        void* stagingPtr,
        GpuRasterWorkload workload,
        bool verifyUniformFill)
    {
        var mapped = new D3D11MappedSubresource();
        int hr = map(ctx, stagingPtr, 0, D3D11MapRead, 0, &mapped);
        if (hr != 0)
            ThrowDeviceFailure(hr, "Map(RasterReadback)");
        try
        {
            if (mapped.Data == IntPtr.Zero)
                throw new GpuRasterValidationException("光柵 readback 指標無效。");
            int pixelCount = workload.Width * workload.Height;
            var pixels = new ReadOnlySpan<byte>((void*)mapped.Data, pixelCount * 4);
            uint checksum = 2166136261u;
            int distinctPixels = 0;
            byte firstB = pixels[0], firstG = pixels[1], firstR = pixels[2];
            for (int pixel = 0; pixel < pixelCount; pixel++)
            {
                byte b = pixels[pixel * 4 + 0];
                byte g = pixels[pixel * 4 + 1];
                byte r = pixels[pixel * 4 + 2];
                byte a = pixels[pixel * 4 + 3];
                if (a != 0xFF)
                    throw new GpuRasterValidationException($"光柵 readback alpha={a}（位置 {pixel}）；raster 未按預期輸出，整場拒收。");
                if (b != firstB || g != firstG || r != firstR)
                    distinctPixels++;
                checksum ^= (uint)(b | (g << 8) | (r << 16) | (a << 24));
                checksum *= 16777619u;
            }

            if (verifyUniformFill)
            {
                // CPU 參考：PSFill 輸出 float4(0.25, 0.5, 0.75, 1.0) → BGRA8 = (191, 128, 64, 255)。
                if (firstB is < 189 or > 193 || firstG is < 126 or > 130 || firstR is < 62 or > 66 || distinctPixels != 0)
                    throw new GpuRasterValidationException(
                        $"填充率 readback 與 CPU 參考色不符（B={firstB}, G={firstG}, R={firstR}, 非一致像素 {distinctPixels}）。");
            }
            else if (distinctPixels == 0)
            {
                throw new GpuRasterValidationException("紋理場景 readback 全單色；取樣未實際執行，整場拒收。");
            }

            return checksum;
        }
        finally
        {
            unmap(ctx, stagingPtr, 0);
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
                        {
                            candidates.Add($"desc fail=0x{(uint)hr:X8}");
                            SafeRelease(adapter);
                            continue;
                        }

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
