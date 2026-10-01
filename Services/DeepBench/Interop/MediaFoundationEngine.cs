using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// Media Foundation Sink Writer 編碼引擎：合成 NV12 幀餵入硬體編碼管線，量 Wall-clock 編碼吞吐與輸出碼率。
/// 只用 MFTEnumEx 預先確認硬體編碼器存在並啟用 MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS；
/// 輸出寫進記憶體內 MP4 位元流，不寫檔、不上傳、不捆綁影片。
/// </summary>
public sealed class MediaFoundationCodecEngine : IGpuCodecEngine
{
    // ── GUID（以 Windows SDK mfapi.h / mfreadwrite.h 逐項核對）──
    private static readonly Guid GuidMfMediaTypeVideo = new(0x73646976, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    private static readonly Guid GuidH264 = new(0x34363248, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71); // FCC('H264')
    private static readonly Guid GuidNv12 = new(0x3231564E, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71); // FCC('NV12')
    private static readonly Guid GuidMajorType = new(0x48eba18e, 0xf8c9, 0x4687, 0xbf, 0x11, 0x0a, 0x74, 0xc9, 0xf9, 0x6a, 0x8f);
    private static readonly Guid GuidSubtype = new(0xf7e34c9a, 0x42e8, 0x4714, 0xb7, 0x4b, 0xcb, 0x29, 0xd7, 0x2c, 0x35, 0xe5);
    private static readonly Guid GuidFrameSize = new(0x1652c33d, 0xd6b2, 0x4012, 0xb8, 0x34, 0x72, 0x03, 0x08, 0x49, 0xa3, 0x7d);
    private static readonly Guid GuidFrameRate = new(0xc459a2e8, 0x3d2c, 0x4e44, 0xb1, 0x32, 0xfe, 0xe5, 0x15, 0x6c, 0x7b, 0xb0);
    private static readonly Guid GuidAvgBitrate = new(0x20332624, 0xfb0d, 0x4d9e, 0xbd, 0x0d, 0xcb, 0xf6, 0x78, 0x6c, 0x10, 0x2e);
    private static readonly Guid GuidInterlaceMode = new(0xe2724bb8, 0xe676, 0x4806, 0xb4, 0xb2, 0xa8, 0xd6, 0xef, 0xb4, 0x4c, 0xcd);
    private static readonly Guid GuidPixelAspectRatio = new(0xc6376a1e, 0x8d0a, 0x4027, 0xbe, 0x45, 0x6d, 0x9a, 0x0a, 0xd3, 0x9b, 0xb6);
    private static readonly Guid GuidEnableHardwareTransforms = new(0xa634a91c, 0x822b, 0x41b9, 0xa4, 0x94, 0x4d, 0xe4, 0x64, 0x36, 0x12, 0xb0);
    private static readonly Guid GuidTranscodeContainerType = new(0x150ff23f, 0x4abc, 0x478b, 0xac, 0x4f, 0xe1, 0x91, 0x6f, 0xba, 0x1c, 0xca);
    private static readonly Guid GuidContainerMpeg4 = new(0xdc6cd05d, 0xb9d0, 0x40ef, 0xbd, 0x35, 0xfa, 0x62, 0x2c, 0x1a, 0xb2, 0x8a);
    private static readonly Guid GuidCategoryVideoEncoder = new(0xf79eac7d, 0xe545, 0x4387, 0xbd, 0xee, 0xd6, 0x47, 0xd7, 0xbd, 0xe4, 0x2a);

    private const uint MftEnumFlagHardware = 0x4;
    private const uint MfStartupVersion = 0x00020070; // MF_VERSION（SDK 2, API 0x70）

    [StructLayout(LayoutKind.Sequential)]
    private struct MftRegisterTypeInfo { public Guid MajorType; public Guid Subtype; }

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    private static extern int MFCreateAttributes(out IntPtr attributes, uint count);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out IntPtr mediaType);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateSample(out IntPtr sample);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMemoryBuffer(uint maxLength, out IntPtr buffer);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMFByteStreamOnStream(IntPtr stream, out IntPtr byteStream);

    [DllImport("mfplat.dll")]
    private static extern int MFTEnumEx(
        Guid category, uint flags,
        IntPtr inputType,
        in MftRegisterTypeInfo outputType,
        out IntPtr activates, out uint count);

    [DllImport("mfreadwrite.dll")]
    private static extern int MFCreateSinkWriterFromURL(
        IntPtr url, IntPtr byteStream, IntPtr attributes, out IntPtr sinkWriter);

    [DllImport("shlwapi.dll")]
    private static extern IntPtr SHCreateMemStream(IntPtr init, uint cbInit);

    public Task<GpuCodecMeasurement> MeasureAsync(GpuCodecContext context, CancellationToken cancellationToken)
        => Task.Run(() => Measure(context.Workload, context, cancellationToken), cancellationToken);

    private static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
            throw new GpuCodecException($"{operation} 失敗，HRESULT=0x{(uint)hr:X8}。");
    }

    private static void SafeRelease(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero)
            Marshal.Release(comObject);
    }

    /// <summary>只取硬體 H.264 編碼器數量；不接觸 activate 物件（GetAllocatedString 在測試主機內觸發原生崩潰，名稱非必要不冒險）。</summary>
    internal static int CountHardwareH264Encoders()
    {
        var h264Output = new MftRegisterTypeInfo { MajorType = GuidMfMediaTypeVideo, Subtype = GuidH264 };
        int hr = MFTEnumEx(GuidCategoryVideoEncoder, MftEnumFlagHardware, IntPtr.Zero, h264Output, out IntPtr activates, out uint count);
        if (hr != 0)
            return 0;
        if (activates != IntPtr.Zero)
            Marshal.FreeCoTaskMem(activates);
        return (int)count;
    }

    private static unsafe GpuCodecMeasurement Measure(GpuCodecWorkload workload, GpuCodecContext context, CancellationToken cancellationToken)
    {
        int encoderCount = CountHardwareH264Encoders();
        if (encoderCount == 0)
            throw new GpuUnsupportedException("MFTEnumEx 未找到硬體 H.264 編碼器；本項不以軟體編碼冒充硬體 codec 吞吐。");
        string encoderNames = $"{encoderCount} 個硬體 H.264 編碼器（MFTEnumEx）";

        ThrowIfFailed(MFStartup(MfStartupVersion, 0), "MFStartup");
        IntPtr stream = IntPtr.Zero;
        IntPtr byteStream = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        IntPtr sinkWriter = IntPtr.Zero;
        IntPtr outputType = IntPtr.Zero;
        IntPtr inputType = IntPtr.Zero;
        try
        {
            stream = SHCreateMemStream(IntPtr.Zero, 0);
            if (stream == IntPtr.Zero)
                throw new GpuCodecException("SHCreateMemStream 失敗。");
            ThrowIfFailed(MFCreateMFByteStreamOnStream(stream, out byteStream), "MFCreateMFByteStreamOnStream");
            ThrowIfFailed(MFCreateAttributes(out attributes, 2), "MFCreateAttributes");
            SetAttributeUint32(attributes, GuidEnableHardwareTransforms, 1);
            SetAttributeGuid(attributes, GuidTranscodeContainerType, GuidContainerMpeg4);
            ThrowIfFailed(MFCreateSinkWriterFromURL(IntPtr.Zero, byteStream, attributes, out sinkWriter), "MFCreateSinkWriterFromURL");

            outputType = CreateVideoType(GuidH264, workload);
            inputType = CreateVideoType(GuidNv12, workload);
            // IMFSinkWriter：SetInputMediaType=4、BeginWriting=5、WriteSample=6、Finalize=11（mfreadwrite.h）。
            var addStream = (delegate* unmanaged[Stdcall]<void*, void*, uint*, int>)GetVtableSlot((void*)sinkWriter, 3);
            // SetInputMediaType(4)：宣告流 0 的未壓縮輸入型別。
            var setInputType = (delegate* unmanaged[Stdcall]<void*, uint, void*, void*, int>)GetVtableSlot((void*)sinkWriter, 4);
            var beginWriting = (delegate* unmanaged[Stdcall]<void*, int>)GetVtableSlot((void*)sinkWriter, 5);
            var writeSample = (delegate* unmanaged[Stdcall]<void*, uint, void*, int>)GetVtableSlot((void*)sinkWriter, 6);
            var finalize = (delegate* unmanaged[Stdcall]<void*, int>)GetVtableSlot((void*)sinkWriter, 11);
                        uint streamIndex;
            ThrowIfFailed(addStream((void*)sinkWriter, (void*)outputType, &streamIndex), "AddStream");
            ThrowIfFailed(setInputType((void*)sinkWriter, 0, (void*)inputType, null), "SetInputMediaType");
            ThrowIfFailed(beginWriting((void*)sinkWriter), "BeginWriting");

            int lumaSize = workload.Width * workload.Height;
            int chromaSize = lumaSize / 2;
            int frameBytes = lumaSize + chromaSize;
            long sampleDurationTicks = 10_000_000L * workload.FramerateDenominator / workload.FramerateNumerator;
            byte[] frame = new byte[frameBytes];

            long timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int frameIndex = 0; frameIndex < workload.FrameCount; frameIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FillSyntheticNv12(frame, workload.Width, workload.Height, frameIndex);
                IntPtr sample = IntPtr.Zero;
                IntPtr buffer = IntPtr.Zero;
                try
                {
                    ThrowIfFailed(MFCreateSample(out sample), "MFCreateSample");
                    ThrowIfFailed(MFCreateMemoryBuffer((uint)frameBytes, out buffer), "MFCreateMemoryBuffer");
                    // IMFMediaBuffer：Lock=3、Unlock=4、SetCurrentLength=6（mfobjects.h）。
                    var lockBuffer = (delegate* unmanaged[Stdcall]<void*, byte**, uint*, uint*, int>)GetVtableSlot((void*)buffer, 3);
                    var unlockBuffer = (delegate* unmanaged[Stdcall]<void*, int>)GetVtableSlot((void*)buffer, 4);
                    var setCurrentLength = (delegate* unmanaged[Stdcall]<void*, uint, int>)GetVtableSlot((void*)buffer, 6);
                    byte* data;
                    uint maxLength;
                    uint currentLength;
                    ThrowIfFailed(lockBuffer((void*)buffer, &data, &maxLength, &currentLength), "Lock");
                    try
                    {
                        fixed (byte* source = frame)
                            System.Buffer.MemoryCopy(source, data, maxLength, frameBytes);
                    }
                    finally
                    {
                        ThrowIfFailed(unlockBuffer((void*)buffer), "Unlock");
                    }
                    ThrowIfFailed(setCurrentLength((void*)buffer, (uint)frameBytes), "SetCurrentLength");

                    // IMFSample：SetSampleTime=36、SetSampleDuration=38、AddBuffer=42（mfobjects.h）。
                    var setSampleTime = (delegate* unmanaged[Stdcall]<void*, long, int>)GetVtableSlot((void*)sample, 36);
                    var setSampleDuration = (delegate* unmanaged[Stdcall]<void*, long, int>)GetVtableSlot((void*)sample, 38);
                    var addBuffer = (delegate* unmanaged[Stdcall]<void*, void*, int>)GetVtableSlot((void*)sample, 42);
                    ThrowIfFailed(setSampleTime((void*)sample, frameIndex * sampleDurationTicks), "SetSampleTime");
                    ThrowIfFailed(setSampleDuration((void*)sample, sampleDurationTicks), "SetSampleDuration");
                    ThrowIfFailed(addBuffer((void*)sample, (void*)buffer), "AddBuffer");
                    ThrowIfFailed(writeSample((void*)sinkWriter, 0, (void*)sample), "WriteSample");
                }
                finally
                {
                    SafeRelease(buffer);
                    SafeRelease(sample);
                }

                if (frameIndex % 16 == 0)
                {
                    context.Progress.Report(new DeepBenchProgress(
                        GpuCodecThroughputService.TestId, frameIndex, workload.FrameCount,
                        0.05 + 0.9 * frameIndex / workload.FrameCount, $"編碼 {frameIndex}/{workload.FrameCount} 幀"));
                }
            }

            ThrowIfFailed(finalize((void*)sinkWriter), "Finalize");
            double elapsedSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(timestamp).TotalSeconds;
            // IMFByteStream：GetLength=4（mfobjects.h）。
            var getLength = (delegate* unmanaged[Stdcall]<void*, long*, int>)GetVtableSlot((void*)byteStream, 4);
            long outputBytes;
            ThrowIfFailed(getLength((void*)byteStream, &outputBytes), "GetLength");

            context.Progress.Report(new DeepBenchProgress(
                GpuCodecThroughputService.TestId, workload.FrameCount, workload.FrameCount, 0.98, "編碼完成"));

            double fps = workload.FrameCount / elapsedSeconds;
            double mbps = outputBytes * 8d / elapsedSeconds / 1_000_000d;
            if (!double.IsFinite(fps) || fps <= 0 || outputBytes <= 0)
                throw new GpuCodecException($"編碼輸出異常：fps={fps}、outputBytes={outputBytes}；整場拒收。");
            return new GpuCodecMeasurement(
                new GpuCodecRun(
                    encoderNames, workload.FrameCount, elapsedSeconds, fps, mbps, outputBytes));
        }
        finally
        {
            SafeRelease(inputType);
            SafeRelease(outputType);
            SafeRelease(sinkWriter);
            SafeRelease(attributes);
            SafeRelease(byteStream);
            if (stream != IntPtr.Zero)
                Marshal.Release(stream);
            _ = MFShutdown();
        }
    }

    private static IntPtr CreateVideoType(Guid subtype, GpuCodecWorkload workload)
    {
        ThrowIfFailed(MFCreateMediaType(out IntPtr mediaType), "MFCreateMediaType");
        try
        {
            SetAttributeGuid(mediaType, GuidMajorType, GuidMfMediaTypeVideo);
            SetAttributeGuid(mediaType, GuidSubtype, subtype);
            SetAttributeUint64(mediaType, GuidFrameSize, ((ulong)workload.Width << 32) | (uint)workload.Height);
            SetAttributeUint64(mediaType, GuidFrameRate,
                ((ulong)workload.FramerateNumerator << 32) | (uint)workload.FramerateDenominator);
            SetAttributeUint32(mediaType, GuidAvgBitrate, (uint)workload.BitrateBitsPerSecond);
            SetAttributeUint32(mediaType, GuidInterlaceMode, 2); // MFVideoInterlace_Progressive
            SetAttributeUint64(mediaType, GuidPixelAspectRatio, 1UL << 32 | 1u);
            IntPtr result = mediaType;
            mediaType = IntPtr.Zero;
            return result;
        }
        finally
        {
            SafeRelease(mediaType);
        }
    }

    private static unsafe void SetAttributeUint32(IntPtr attributes, Guid key, uint value)
    {
        var setUint32 = (delegate* unmanaged[Stdcall]<void*, Guid*, uint, int>)GetVtableSlot((void*)attributes, 21);
        ThrowIfFailed(setUint32((void*)attributes, &key, value), "IMFAttributes.SetUINT32");
    }

    private static unsafe void SetAttributeUint64(IntPtr attributes, Guid key, ulong value)
    {
        var setUint64 = (delegate* unmanaged[Stdcall]<void*, Guid*, ulong, int>)GetVtableSlot((void*)attributes, 22);
        ThrowIfFailed(setUint64((void*)attributes, &key, value), "IMFAttributes.SetUINT64");
    }

    private static unsafe void SetAttributeGuid(IntPtr attributes, Guid key, Guid value)
    {
        var setGuid = (delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, int>)GetVtableSlot((void*)attributes, 24);
        ThrowIfFailed(setGuid((void*)attributes, &key, &value), "IMFAttributes.SetGUID");
    }

    /// <summary>合成 NV12 幀：對角漸層亮度＋每幀位移，確定性且逐幀有變化，逼編碼器做實際工作。</summary>
    internal static void FillSyntheticNv12(byte[] frame, int width, int height, int frameIndex)
    {
        int lumaSize = width * height;
        int chromaSize = lumaSize / 2;
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                frame[rowOffset + x] = (byte)((x + y + frameIndex * 7) & 0xFF);
            }
        }

        byte chroma = (byte)(128 + (frameIndex * 13) % 32);
        Array.Fill(frame, chroma, lumaSize, chromaSize);
    }

    private static unsafe IntPtr GetVtableSlot(void* comObject, int slot)
    {
        // COM 物件前 8 bytes 是 vtable 指標；先解參考再取 slot。
        void** vtable = *(void***)comObject;
        return (IntPtr)vtable[slot];
    }
}

public sealed class GpuCodecException(string message) : InvalidOperationException(message);
