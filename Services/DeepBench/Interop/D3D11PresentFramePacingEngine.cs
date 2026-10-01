using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// D3D11 使用者模式 Present pacing engine。只建立本行程擁有的小型視窗與 swap chain；
/// 不注入目標程序、不掛勾、不讀驅動私有 API。
/// </summary>
public sealed class D3D11PresentFramePacingEngine : IPresentFramePacingEngine
{
    private const uint SwapChainUsageRenderTargetOutput = 0x20;
    private const uint DxgiFormatB8G8R8A8Unorm = 87;
    private const int HdFeatureLevel = 0x0B000;
    private const int FeatureLevel11_1 = 0x0B100;
    private static readonly Guid IidIdxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IidId3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    public Task<PresentFramePacingRun> MeasureAsync(
        PresentFramePacingWorkload workload,
        CancellationToken cancellationToken) =>
        Task.Run(() => Measure(workload, cancellationToken), cancellationToken);

    private static unsafe PresentFramePacingRun Measure(
        PresentFramePacingWorkload workload,
        CancellationToken cancellationToken)
    {
        IntPtr window = CreateMeasurementWindow();
        IntPtr swapChain = IntPtr.Zero;
        IntPtr device = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        IntPtr backBuffer = IntPtr.Zero;
        IntPtr renderTargetView = IntPtr.Zero;
        string adapterName = string.Empty;
        try
        {
            ShowWindow(window, 4); // SW_SHOWNOACTIVATE：顯示但不搶走使用者焦點

            var swapDesc = new DXGI_SWAP_CHAIN_DESC
            {
                BufferDesc = new DXGI_MODE_DESC
                {
                    Width = 96,
                    Height = 54,
                    RefreshRate = new DXGI_RATIONAL(60, 1),
                    Format = DxgiFormatB8G8R8A8Unorm,
                },
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                BufferUsage = SwapChainUsageRenderTargetOutput,
                BufferCount = 2,
                OutputWindow = window,
                Windowed = 1,
                SwapEffect = 0, // DXGI_SWAP_EFFECT_DISCARD：相容性最寬
                Flags = 0,
            };

            uint[] featureLevels = [FeatureLevel11_1, HdFeatureLevel];
            int createHr = D3D11CreateDeviceAndSwapChain(
                IntPtr.Zero,
                1, // D3D_DRIVER_TYPE_HARDWARE：明確排除 WARP
                IntPtr.Zero,
                0,
                featureLevels,
                (uint)featureLevels.Length,
                7,
                ref swapDesc,
                out swapChain,
                out device,
                out uint featureLevel,
                out context);
            if (createHr != 0)
                throw new GpuUnsupportedException($"D3D11CreateDeviceAndSwapChain 失敗，HRESULT=0x{(uint)createHr:X8}。");

            adapterName = ReadAdapterName(device);
            if (GpuFp32ComputeService.IsWarp(adapterName))
                throw new GpuUnsupportedException("偵測到 WARP（Microsoft Basic Render Driver）；本項不輸出硬體 GPU 結果。");

            void* devicePtr = (void*)device;
            void* contextPtr = (void*)context;
            void* swapChainPtr = (void*)swapChain;

            Guid textureIid = IidId3D11Texture2D;
            void* backBufferPointer = null;
            int hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)VTable(swapChain, 9))(
                swapChainPtr, &textureIid, &backBufferPointer);
            backBuffer = (IntPtr)backBufferPointer;
            if (hr != 0 || backBuffer == IntPtr.Zero)
                throw new PresentFramePacingValidationException($"IDXGISwapChain::GetBuffer 失敗，HRESULT=0x{(uint)hr:X8}。");

            void* backBufferPtr = (void*)backBuffer;
            void* renderTargetPtr = null;
            hr = ((delegate* unmanaged[Stdcall]<void*, void*, void*, void**, int>)VTable(device, 9))(
                devicePtr, backBufferPtr, null, &renderTargetPtr);
            if (hr != 0 || renderTargetPtr is null)
                throw new PresentFramePacingValidationException($"ID3D11Device::CreateRenderTargetView 失敗，HRESULT=0x{(uint)hr:X8}。");
            renderTargetView = (IntPtr)renderTargetPtr;

            var setRenderTargets = (delegate* unmanaged[Stdcall]<void*, uint, void**, void*, void>)VTable(context, 33);
            var clearRenderTarget = (delegate* unmanaged[Stdcall]<void*, void*, float*, void>)VTable(context, 50);
            var present = (delegate* unmanaged[Stdcall]<void*, uint, uint, int>)VTable(swapChain, 8);

            void* renderTargetForSet = renderTargetPtr;
            setRenderTargets(contextPtr, 1, &renderTargetForSet, null);

            var rounds = new List<PresentFramePacingRound>(workload.Profiles.Sum(profile => profile.Rounds));
            foreach (PresentFramePacingProfile profile in workload.Profiles)
            {
                for (int round = 0; round < profile.Rounds; round++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rounds.Add(MeasureRound(
                        profile,
                        round,
                        contextPtr,
                        swapChainPtr,
                        renderTargetPtr,
                        clearRenderTarget,
                        present,
                        cancellationToken));
                }
            }

            return new PresentFramePacingRun(adapterName, featureLevel, rounds);
        }
        finally
        {
            SafeRelease(renderTargetView);
            SafeRelease(backBuffer);
            SafeRelease(context);
            SafeRelease(swapChain);
            SafeRelease(device);
            if (window != IntPtr.Zero) DestroyWindow(window);
        }
    }

    private static unsafe PresentFramePacingRound MeasureRound(
        PresentFramePacingProfile profile,
        int round,
        void* context,
        void* swapChain,
        void* renderTarget,
        delegate* unmanaged[Stdcall]<void*, void*, float*, void> clearRenderTarget,
        delegate* unmanaged[Stdcall]<void*, uint, uint, int> present,
        CancellationToken cancellationToken)
    {
        var samples = new List<PresentFramePacingSample>(profile.FramesPerRound);
        long lastTimestamp = 0;
        int baseHue = round * 17;
        for (int frame = 0; frame < profile.FramesPerRound; frame++)
        {
            if (frame % 8 == 0) cancellationToken.ThrowIfCancellationRequested();

            long started = Stopwatch.GetTimestamp();
            float channel = (frame + baseHue) / (float)profile.FramesPerRound;
            float[] color = new float[4]
            {
                0.10f + 0.70f * (channel - MathF.Floor(channel)),
                0.15f + 0.60f * MathF.Sqrt(Math.Clamp(channel, 0f, 1f)),
                0.20f + 0.50f * (1f - Math.Clamp(channel, 0f, 1f)),
                1f,
            };
            fixed (float* colorPtr = color)
            {
                clearRenderTarget(context, renderTarget, colorPtr);
            }

            int hr = present(swapChain, (uint)profile.SyncInterval, 0);
            long ended = Stopwatch.GetTimestamp();
            if (hr != 0)
            {
                if (hr is unchecked((int)0x887A0005) or unchecked((int)0x887A0006)
                    or unchecked((int)0x887A0007) or unchecked((int)0x887A000A))
                    throw new GpuDeviceRemovedException((uint)hr);
                throw new PresentFramePacingValidationException(
                    $"IDXGISwapChain::Present({profile.SyncInterval}) 失敗，HRESULT=0x{(uint)hr:X8}。");
            }

            double intervalMs = frame == 0 ? 0 : Stopwatch.GetElapsedTime(lastTimestamp, ended).TotalMilliseconds;
            double durationMs = Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds;
            samples.Add(new PresentFramePacingSample(Math.Max(0, intervalMs), Math.Max(durationMs, 1d / Stopwatch.Frequency * 1000d)));
            lastTimestamp = ended;
        }

        return new PresentFramePacingRound(profile.Mode, profile.SyncInterval, samples);
    }

    private static unsafe string ReadAdapterName(IntPtr device)
    {
        Guid dxgiDeviceIid = IidIdxgiDevice;
        void* dxgiDevicePtr = null;
        int hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)VTable(device, 0))(
            (void*)device, &dxgiDeviceIid, &dxgiDevicePtr);
        if (hr != 0 || dxgiDevicePtr is null)
            throw new PresentFramePacingValidationException($"QueryInterface(IDXGIDevice) 失敗，HRESULT=0x{(uint)hr:X8}。");

        IntPtr dxgiDevice = (IntPtr)dxgiDevicePtr;
        try
        {
            void* adapterPtr = null;
            hr = ((delegate* unmanaged[Stdcall]<void*, void**, int>)VTable(dxgiDevice, 7))((void*)dxgiDevice, &adapterPtr);
            if (hr != 0 || adapterPtr is null)
                throw new PresentFramePacingValidationException($"IDXGIDevice::GetAdapter 失敗，HRESULT=0x{(uint)hr:X8}。");

            IntPtr adapter = (IntPtr)adapterPtr;
            try
            {
                var description = new DXGIAdapterDescription();
                hr = ((delegate* unmanaged[Stdcall]<void*, DXGIAdapterDescription*, int>)VTable(adapter, 8))(
                    (void*)adapter, &description);
                if (hr != 0)
                    throw new PresentFramePacingValidationException($"IDXGIAdapter::GetDesc 失敗，HRESULT=0x{(uint)hr:X8}。");

                DXGIAdapterDescription copy = description;
                char* text = copy.Description;
                var span = new ReadOnlySpan<char>(text, 128);
                int end = span.IndexOf('\0');
                return end < 0 ? new string(text, 0, 128).Trim() : new string(text, 0, end);
            }
            finally
            {
                SafeRelease(adapter);
            }
        }
        finally
        {
            SafeRelease(dxgiDevice);
        }
    }

    private static IntPtr CreateMeasurementWindow()
    {
        IntPtr instance = GetModuleHandleW(null);
        var windowClass = new WNDCLASSW
        {
            Style = 0,
            LpfnWndProc = DefWindowProc,
            HInstance = instance,
            LpszClassName = "XinSpect-PresentPacing",
        };
        ushort atom = RegisterClassW(ref windowClass);
        if (atom == 0)
            throw new PresentFramePacingValidationException($"RegisterClassW 失敗，HRESULT=0x{Marshal.GetHRForLastWin32Error():X8}。");

        IntPtr window = CreateWindowExW(
            0x08000000 | 0x00000008, // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            windowClass.LpszClassName,
            "XinSpect Present pacing",
            0x80000000, // WS_POPUP
            16,
            16,
            96,
            54,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (window == IntPtr.Zero)
            throw new PresentFramePacingValidationException($"CreateWindowExW 失敗，HRESULT=0x{Marshal.GetHRForLastWin32Error():X8}。");
        return window;
    }

    private static IntPtr DefWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam) =>
        DefWindowProcW(hWnd, message, wParam, lParam);

    private static IntPtr VTable(IntPtr comObject, int slot)
    {
        IntPtr vtable = Marshal.ReadIntPtr(comObject);
        return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
    }

    private static void SafeRelease(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero) _ = Marshal.Release(comObject);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;

        public DXGI_RATIONAL(uint numerator, uint denominator)
        {
            Numerator = numerator;
            Denominator = denominator;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_SAMPLE_DESC
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_MODE_DESC
    {
        public uint Width;
        public uint Height;
        public DXGI_RATIONAL RefreshRate;
        public uint Format;
        public uint ScanlineOrdering;
        public uint Scaling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_SWAP_CHAIN_DESC
    {
        public DXGI_MODE_DESC BufferDesc;
        public DXGI_SAMPLE_DESC SampleDesc;
        public uint BufferUsage;
        public uint BufferCount;
        public IntPtr OutputWindow;
        public int Windowed;
        public uint SwapEffect;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DXGIAdapterDescription
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint Style;
        public WndProc LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        public string? LpszMenuName;
        public string LpszClassName;
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDeviceAndSwapChain(
        IntPtr adapter,
        uint driverType,
        IntPtr software,
        uint flags,
        uint[] featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        ref DXGI_SWAP_CHAIN_DESC swapChainDesc,
        out IntPtr swapChain,
        out IntPtr device,
        out uint featureLevel,
        out IntPtr immediateContext);
}
