using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// Windows 使用者模式電源狀態取樣；只呼叫 CallNtPowerInformation(ProcessorInformation)。
/// 查詢失敗或緩衝不足會丟出 Unsupported，不把失敗換成零或上一筆值。
/// </summary>
public sealed class WindowsPowerStateLatencyEngine : IPowerStateLatencyEngine
{
    private const int ProcessorInformation = 11;
    private const int ProcessorPowerRecordSize = 24;

    public async Task<PowerStateLatencyRun> ObserveAsync(
        PowerStateLatencyWorkload workload,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.Rounds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.ObservationMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.SamplingIntervalMs, 1);

        int processorCount = Environment.ProcessorCount;
        uint bufferBytes = (uint)(processorCount * ProcessorPowerRecordSize);
        nint buffer = Marshal.AllocHGlobal(processorCount * ProcessorPowerRecordSize);
        try
        {
            var rounds = new List<PowerStateLatencyRound>(workload.Rounds);
            for (int round = 0; round < workload.Rounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rounds.Add(await ObserveRoundAsync(
                    round,
                    workload,
                    processorCount,
                    buffer,
                    bufferBytes,
                    cancellationToken).ConfigureAwait(false));
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new PowerStateLatencyRun(processorCount, rounds);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static async Task<PowerStateLatencyRound> ObserveRoundAsync(
        int round,
        PowerStateLatencyWorkload workload,
        int processorCount,
        nint buffer,
        uint bufferBytes,
        CancellationToken cancellationToken)
    {
        _ = processorCount;
        List<PowerStateObservation> observations = [];
        long roundStart = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(roundStart, Stopwatch.GetTimestamp()).TotalMilliseconds < workload.ObservationMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long queryStart = Stopwatch.GetTimestamp();
            int status = CallNtPowerInformation(
                ProcessorInformation,
                0,
                0,
                buffer,
                bufferBytes);
            long queryEnd = Stopwatch.GetTimestamp();
            if (status != 0)
            {
                throw new PowerStateLatencyUnsupportedException(
                    $"CallNtPowerInformation returned {status}（round {round + 1}）；不輸出部分電源狀態結果。");
            }

            observations.Add(CreateObservation(
                Stopwatch.GetElapsedTime(roundStart, queryEnd).TotalMilliseconds,
                Stopwatch.GetElapsedTime(queryStart, queryEnd).TotalMicroseconds,
                processorCount,
                buffer));
            await Task.Delay(workload.SamplingIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        return new PowerStateLatencyRound(observations);
    }

    private static PowerStateObservation CreateObservation(
        double timeMs,
        double queryLatencyUs,
        int processorCount,
        nint buffer)
    {
        uint currentMin = uint.MaxValue;
        uint currentMax = 0;
        uint limitMin = uint.MaxValue;
        uint limitMax = 0;
        uint idleMin = uint.MaxValue;
        uint idleMax = 0;
        for (int index = 0; index < processorCount; index++)
        {
            nint record = buffer + index * ProcessorPowerRecordSize;
            uint number = unchecked((uint)Marshal.ReadInt32(record));
            uint maxMhz = unchecked((uint)Marshal.ReadInt32(record + 4));
            uint currentMhz = unchecked((uint)Marshal.ReadInt32(record + 8));
            uint mhzLimit = unchecked((uint)Marshal.ReadInt32(record + 12));
            _ = unchecked((uint)Marshal.ReadInt32(record + 16));
            uint currentIdleState = unchecked((uint)Marshal.ReadInt32(record + 20));
            if (number >= (uint)processorCount || maxMhz == 0)
            {
                throw new PowerStateLatencyUnsupportedException(
                    $"電源 API 回報的核心索引或 MaxMhz 不完整（processor {number}）；不輸出可疑狀態結果。");
            }

            currentMin = Math.Min(currentMin, currentMhz);
            currentMax = Math.Max(currentMax, currentMhz);
            limitMin = Math.Min(limitMin, mhzLimit);
            limitMax = Math.Max(limitMax, mhzLimit);
            idleMin = Math.Min(idleMin, currentIdleState);
            idleMax = Math.Max(idleMax, currentIdleState);
        }

        return new PowerStateObservation(
            timeMs,
            queryLatencyUs,
            currentMin,
            currentMax,
            limitMin,
            limitMax,
            idleMin,
            idleMax);
    }

    [DllImport("powrprof.dll")]
    private static extern int CallNtPowerInformation(
        int level,
        nint inputBuffer,
        uint inputBufferSize,
        nint outputBuffer,
        uint outputBufferSize);
}
