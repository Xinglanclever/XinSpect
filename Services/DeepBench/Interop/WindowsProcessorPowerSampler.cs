using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>Bounded legacy level-11 scope. Only one active processor group is supported.</summary>
internal sealed class ProcessorPowerScope
{
    internal const int RecordSize = 24;
    internal int RecordCount { get; }
    internal int BufferBytes { get; }
    internal ulong ProcessorMask { get; }
    internal int PointerBits { get; }

    private ProcessorPowerScope(int count, ulong mask, int pointerBits)
    {
        RecordCount = count;
        BufferBytes = checked(count * RecordSize);
        ProcessorMask = mask;
        PointerBits = pointerBits;
    }

    internal static ProcessorPowerScope Create(ushort activeGroups, uint count, ulong mask, int pointerBits)
    {
        if (activeGroups != 1 || pointerBits is not (32 or 64)
            || count == 0 || count > (uint)pointerBits || mask == 0
            || (pointerBits == 32 && (mask >> 32) != 0)
            || BitOperations.PopCount(mask) != count)
            throw new ProcessorPowerUnsupportedException(
                "電源取樣只支援已確認的單一 active processor group、完整 count/mask；不猜測多群組覆蓋。");
        return new ProcessorPowerScope(checked((int)count), mask, pointerBits);
    }

    internal void ValidateWorkerCount(int workerCount)
    {
        if (workerCount != RecordCount)
            throw new ProcessorPowerUnsupportedException(
                $"managed worker budget W={workerCount} 與 native processor scope N={RecordCount} 不符；不啟動 all-core 負載、不截斷取樣。");
    }

    internal bool SameAs(ProcessorPowerScope other) =>
        RecordCount == other.RecordCount && ProcessorMask == other.ProcessorMask && PointerBits == other.PointerBits;
}

internal sealed class ProcessorPowerUnsupportedException(string message) : Exception(message);

internal readonly record struct ProcessorPowerRange(double CurrentMin, double CurrentMax, double LimitMin, double LimitMax)
{
    // Completion of CallNtPowerInformation, before post-scope/copy/parser work.
    internal long QueryCompletedTimestamp { get; init; }
}

/// <summary>
/// One owned-buffer query, surrounded by independent scope checks. The native API exposes
/// capacity, not bytes written; count provenance is the validated single-group contract.
/// Clearing prevents a preceding sample/allocator tail from being accepted as a new record.
/// </summary>
internal sealed class WindowsProcessorPowerSampler(
    Func<ProcessorPowerScope> readScope,
    Func<nint, uint, int> query,
    Action<nint, int> clear,
    Func<nint, int, byte[]> copy)
{
    internal ProcessorPowerScope CaptureScope() => readScope();

    internal void VerifyScope(ProcessorPowerScope scope)
    {
        if (!scope.SameAs(readScope()))
            throw new ProcessorPowerUnsupportedException("電源取樣期間 processor scope 改變；不輸出部分結果。");
    }

    internal ProcessorPowerRange Sample(ProcessorPowerScope scope, nint buffer, bool requireLimit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        VerifyScope(scope);
        token.ThrowIfCancellationRequested();
        clear(buffer, scope.BufferBytes);
        int status = query(buffer, checked((uint)scope.BufferBytes));
        long queryEnd = Stopwatch.GetTimestamp();
        token.ThrowIfCancellationRequested();
        if (status != 0)
            throw new ProcessorPowerUnsupportedException($"CallNtPowerInformation returned {status}；不輸出部分電源取樣。");
        VerifyScope(scope);
        token.ThrowIfCancellationRequested();
        return Parse(scope, copy(buffer, scope.BufferBytes), requireLimit) with { QueryCompletedTimestamp = queryEnd };
    }

    internal static ProcessorPowerRange Parse(ProcessorPowerScope scope, ReadOnlySpan<byte> records, bool requireLimit)
    {
        if (records.Length != scope.BufferBytes)
            throw new ProcessorPowerUnsupportedException("電源紀錄長度與已確認 native scope 不符；拒絕短讀、尾段與未對齊資料。");
        ulong seen = 0;
        double currentMin = double.MaxValue, currentMax = 0, limitMin = double.MaxValue, limitMax = 0;
        for (int index = 0; index < scope.RecordCount; index++)
        {
            ReadOnlySpan<byte> record = records.Slice(index * ProcessorPowerScope.RecordSize, ProcessorPowerScope.RecordSize);
            uint number = BinaryPrimitives.ReadUInt32LittleEndian(record);
            uint maxMhz = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            uint currentMhz = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
            uint limitMhz = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            if (number >= (uint)scope.PointerBits)
                throw new ProcessorPowerUnsupportedException($"電源 API processor ID {number} 超出 mask 範圍。");
            ulong bit = 1UL << (int)number;
            if ((scope.ProcessorMask & bit) == 0 || (seen & bit) != 0)
                throw new ProcessorPowerUnsupportedException($"電源 API processor ID {number} 重複或不在預期集合。");
            seen |= bit;
            if (maxMhz == 0 || currentMhz == 0 || (requireLimit && limitMhz == 0))
                throw new ProcessorPowerUnsupportedException($"電源 API processor {number} 必要 MHz 欄位不完整；不猜測頻率。");
            currentMin = Math.Min(currentMin, currentMhz);
            currentMax = Math.Max(currentMax, currentMhz);
            limitMin = Math.Min(limitMin, limitMhz);
            limitMax = Math.Max(limitMax, limitMhz);
        }
        if (seen != scope.ProcessorMask)
            throw new ProcessorPowerUnsupportedException("電源 API 未覆蓋完整預期 processor ID 集合。");
        return new ProcessorPowerRange(currentMin, currentMax, limitMin, limitMax);
    }

    internal static WindowsProcessorPowerSampler CreateWindows() => new(
        ReadWindowsScope,
        QueryWindows,
        (buffer, bytes) => Marshal.Copy(new byte[bytes], 0, buffer, bytes),
        (buffer, bytes) => { byte[] result = new byte[bytes]; Marshal.Copy(buffer, result, 0, bytes); return result; });

    private static ProcessorPowerScope ReadWindowsScope()
    {
        try
        {
            return CaptureSingleGroupScope(GetActiveProcessorGroupCount, () =>
            {
                GetSystemInfo(out SystemInfo info);
                return (info.NumberOfProcessors, (ulong)info.ActiveProcessorMask, IntPtr.Size * 8);
            });
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            throw new ProcessorPowerUnsupportedException($"電源 native scope API 不可用：{error.GetType().Name}。");
        }
    }

    // This is the native acquisition order itself, exposed to pure injected regressions.
    internal static ProcessorPowerScope CaptureSingleGroupScope(
        Func<ushort> groupCount, Func<(uint Count, ulong Mask, int PointerBits)> systemInfo)
    {
        ushort before = groupCount();
        if (before != 1)
            throw new ProcessorPowerUnsupportedException("legacy level-11 電源取樣未支援未知／多 active processor groups。");
        var info = systemInfo();
        ushort after = groupCount();
        if (before != after)
            throw new ProcessorPowerUnsupportedException("取得電源 scope 時 active processor groups 改變。");
        return ProcessorPowerScope.Create(after, info.Count, info.Mask, info.PointerBits);
    }

    private static int QueryWindows(nint buffer, uint bytes)
    {
        try { return CallNtPowerInformation(11, 0, 0, buffer, bytes); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            throw new ProcessorPowerUnsupportedException($"電源 native query API 不可用：{error.GetType().Name}。");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemInfo
    {
        internal uint OemId;
        internal uint PageSize;
        internal nint MinimumApplicationAddress;
        internal nint MaximumApplicationAddress;
        internal nuint ActiveProcessorMask;
        internal uint NumberOfProcessors;
        internal uint ProcessorType;
        internal uint AllocationGranularity;
        internal ushort ProcessorLevel;
        internal ushort ProcessorRevision;
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();
    [DllImport("kernel32.dll")]
    private static extern void GetSystemInfo(out SystemInfo systemInfo);
    [DllImport("powrprof.dll")]
    private static extern int CallNtPowerInformation(int level, nint input, uint inputBytes, nint output, uint outputBytes);
}
